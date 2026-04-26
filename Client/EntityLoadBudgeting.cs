using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Synergy.Server;

namespace Synergy.Client
{
    /// <summary>
    /// Budgets entity load processing to prevent frame spikes on login/teleport.
    ///
    /// Problem: HandleEntitiesPacket (login bulk) processes ALL entities inline in one
    /// tick — each triggers renderer creation (GPU alloc). 200 entities = 100-500ms freeze.
    ///
    /// Fix: Patch HandleEntitiesPacket to push entities to EntityLoadQueue (addToLoadQueue=true)
    /// instead of processing inline. Patch OnGameTick to drain the queue with a budget of
    /// N entities per tick. Entities appear gradually, nearest first (via P13 SpawnPriorityOrdering).
    ///
    /// Reference: Unreal Engine spawn queue, Unity Netcode GhostSpawnBuffer.
    /// </summary>
    public static class EntityLoadBudgeting
    {
        private static ICoreClientAPI capi;
        private static int errorCount;
        private static bool disabled;

        private const int MaxEntitiesPerTick = 5;

        // Cached reference to createOrUpdateEntityFromPacket(Packet_Entity, ClientMain, bool)
        private static MethodInfo createOrUpdateMethod;

        // Track entity IDs despawned while still in the load queue.
        // Prevents ghost entities: entity queued → despawn received → queue drains → ghost.
        private static readonly HashSet<long> despawnedWhileQueued = new();

        public static void Initialize(ICoreClientAPI api, Harmony harmony)
        {
            capi = api;
            errorCount = 0;
            disabled = false;

            var csEntities = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSystemEntities");
            var clientMain = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain");
            var pktEntity = AccessTools.TypeByName("Packet_Entity");
            var pktServer = AccessTools.TypeByName("Packet_Server");

            if (csEntities == null || clientMain == null || pktEntity == null || pktServer == null)
            {
                api.Logger.Warning("[Synergy] EntityLoadBudget: Required types not found, skipping");
                return;
            }

            createOrUpdateMethod = AccessTools.Method(csEntities, "createOrUpdateEntityFromPacket",
                new[] { pktEntity, clientMain, typeof(bool) });
            if (createOrUpdateMethod == null)
            {
                api.Logger.Warning("[Synergy] EntityLoadBudget: createOrUpdateEntityFromPacket not found, skipping");
                return;
            }

            // Patch HandleEntitiesPacket — redirect bulk login entities to queue
            var handleEntities = AccessTools.Method(csEntities, "HandleEntitiesPacket", new[] { pktServer });
            if (handleEntities != null)
            {
                if (!ConflictDetector.IsSafeToPatch(handleEntities, SynergyMod.HarmonyId, api.Logger))
                    return;
                harmony.Patch(handleEntities,
                    prefix: new HarmonyMethod(typeof(EntityLoadBudgeting), nameof(Prefix_HandleEntitiesPacket)));
            }

            // Patch OnGameTick — budget the queue drain
            var onGameTick = AccessTools.Method(csEntities, "OnGameTick", new[] { typeof(float) });
            if (onGameTick != null)
            {
                if (!ConflictDetector.IsSafeToPatch(onGameTick, SynergyMod.HarmonyId, api.Logger))
                    return;
                harmony.Patch(onGameTick,
                    prefix: new HarmonyMethod(typeof(EntityLoadBudgeting), nameof(Prefix_OnGameTick)));
            }

            api.Logger.Notification("[Synergy] EntityLoadBudget: Active (max {0} entities/tick)", MaxEntitiesPerTick);

            // Track despawns to prevent ghost entities when despawn arrives while entity is queued
            api.Event.OnEntityDespawn += OnEntityDespawn;
        }

        private static void OnEntityDespawn(Entity entity, EntityDespawnData reason)
        {
            despawnedWhileQueued.Add(entity.EntityId);
        }

        public static void Cleanup()
        {
            if (capi != null)
                capi.Event.OnEntityDespawn -= OnEntityDespawn;
            despawnedWhileQueued.Clear();
        }

        /// <summary>
        /// Replaces HandleEntitiesPacket to use EntityLoadQueue instead of inline processing.
        /// Calls createOrUpdateEntityFromPacket with addToLoadQueue=true so entities are
        /// queued for budgeted processing in OnGameTick.
        /// </summary>
        public static bool Prefix_HandleEntitiesPacket(object __instance, object serverpacket)
        {
            if (disabled) return true;

            try
            {
                var game = capi.World as Vintagestory.Client.NoObf.ClientMain;
                if (game == null) return true;

                // Don't budget during initial login — player entity must be in LoadedEntities
                // before HandlePlayerData runs, otherwise game.EntityPlayer is null (crash).
                // Only budget after player has spawned (teleport/dimension change bulk packets).
                if (!game.Spawned) return true;

                // Access serverpacket.Entities.Entities (Packet_Entity[])
                var entitiesField = serverpacket.GetType().GetField("Entities");
                if (entitiesField == null) return true;
                var entitiesObj = entitiesField.GetValue(serverpacket);
                if (entitiesObj == null) return true;
                var entityArrayField = entitiesObj.GetType().GetField("Entities");
                if (entityArrayField == null) return true;
                var entities = entityArrayField.GetValue(entitiesObj) as Array;
                if (entities == null) return true;

                if (game.ClassRegistryInt.entityClassNameToTypeMapping.Count == 0)
                {
                    game.Logger.Error("Server sent entity packets but class mapping not ready. Ignoring.");
                    return false;
                }

                for (int i = 0; i < entities.Length; i++)
                {
                    var pkt = entities.GetValue(i);
                    if (pkt == null) break;

                    // Check if entity already exists (will be updated in-place, not queued)
                    var entityIdField = pkt.GetType().GetField("EntityId");
                    long entityId = entityIdField != null ? (long)entityIdField.GetValue(pkt) : 0;
                    bool alreadyExists = entityId != 0 && game.LoadedEntities.ContainsKey(entityId);

                    // addToLoadQueue=true: new entities are queued for budgeted processing.
                    // Existing entities are updated in-place by createOrUpdateEntityFromPacket.
                    var entity = (Entity)createOrUpdateMethod.Invoke(null, new object[] { pkt, game, true });
                    if (entity == null)
                    {
                        capi.Logger.Error("[Synergy] EntityLoadBudget: Server sent entity packet but entity could not be created");
                    }
                    else if (alreadyExists)
                    {
                        // Vanilla calls TriggerEntityLoaded for all entities in bulk packet,
                        // including already-existing ones. Replicate that for updated entities.
                        game.eventManager?.TriggerEntityLoaded(entity);
                    }
                }

                return false; // Skip vanilla HandleEntitiesPacket
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errorCount) >= 5)
                {
                    disabled = true;
                    capi?.Logger.Warning("[Synergy] EntityLoadBudget: Auto-disabled after {0} errors: {1}",
                        errorCount, ex.Message);
                }
                return true;
            }
        }

        /// <summary>
        /// Replaces OnGameTick to budget EntityLoadQueue drain.
        /// Max MaxEntitiesPerTick entities per tick. Also runs entity.OnGameTick loop.
        /// Separate try-catch for queue drain vs entity tick to prevent double-ticking on error.
        /// </summary>
        public static bool Prefix_OnGameTick(float dt)
        {
            if (disabled) return true;

            var game = capi.World as Vintagestory.Client.NoObf.ClientMain;
            if (game == null || game.IsPaused) return true;

            // Phase 1: Budgeted queue drain (with error handling that falls back to vanilla)
            try
            {
                int processed = 0;
                lock (game.EntityLoadQueueLock)
                {
                    while (game.EntityLoadQueue.Count > 0 && processed < MaxEntitiesPerTick)
                    {
                        Entity entity = game.EntityLoadQueue.Pop();

                        // Skip entities that were despawned while waiting in the queue
                        if (despawnedWhileQueued.Remove(entity.EntityId)) continue;

                        if (!game.LoadedEntities.ContainsKey(entity.EntityId))
                        {
                            game.LoadedEntities[entity.EntityId] = entity;
                            game.eventManager?.TriggerEntityLoaded(entity);
                            processed++;
                        }
                    }

                    // Clear despawn tracking when queue is empty (no more risk)
                    if (game.EntityLoadQueue.Count == 0)
                        despawnedWhileQueued.Clear();
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errorCount) >= 5)
                {
                    disabled = true;
                    capi?.Logger.Warning("[Synergy] EntityLoadBudget: Auto-disabled after {0} errors: {1}",
                        errorCount, ex.Message);
                }
                return true; // Fall back to vanilla for this tick
            }

            // Phase 2: Entity tick loop (always runs, never falls back to vanilla to avoid double-tick)
            game.api.World.FrameProfiler.Mark("loadedEntityQueue-lockcontention");
            foreach (Entity item in (IEnumerable<Entity>)game.LoadedEntities.Values)
            {
                item.OnGameTick(dt);
            }

            return false; // Skip vanilla OnGameTick
        }
    }
}
