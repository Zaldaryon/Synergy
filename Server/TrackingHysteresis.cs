using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Add hysteresis buffer to entity tracking range to prevent pop-in/pop-out flickering.
    /// Spawn at trackingRange, despawn at trackingRange + buffer.
    /// Buffer is proportional to entity speed: max(8, motion.Length * 20) blocks.
    /// Vanilla behavior preserved: same entities tracked, just with stable boundary.
    /// </summary>
    public static class TrackingHysteresis
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private const double MinBufferBlocks = 8.0;
        private const double SpeedMultiplier = 20.0;

        // IL-emitted fast accessors via Harmony — FieldRef<object, F> for internal types
        private static AccessTools.FieldRef<object, System.Collections.IList> outOfRangeRef;
        private static AccessTools.FieldRef<object, IServerPlayer> playerRef;
        private static AccessTools.FieldRef<object, HashSet<long>> trackedEntitiesRef;
        private static AccessTools.FieldRef<object, object> esRef;
        private static AccessTools.FieldRef<object, int> trackingRangeSqRef;
        private static AccessTools.FieldRef<object, long> entityIdRef;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var physMgr = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
            if (physMgr == null)
            {
                api.Logger.Warning("[Synergy] TrackingHysteresis: Could not find PhysicsManager, skipping");
                return;
            }

            var connClientType = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");
            if (connClientType == null)
            {
                api.Logger.Warning("[Synergy] TrackingHysteresis: Could not find ConnectedClient, skipping");
                return;
            }

            // IL-emitted fast field accessors for internal types
            outOfRangeRef = AccessTools.FieldRefAccess<System.Collections.IList>(connClientType, "entitiesNowOutOfRange");
            playerRef = AccessTools.FieldRefAccess<IServerPlayer>(connClientType, "Player");
            trackedEntitiesRef = AccessTools.FieldRefAccess<HashSet<long>>(connClientType, "TrackedEntities");
            esRef = AccessTools.FieldRefAccess<object>(physMgr, "es");

            var entityDespawnType = AccessTools.TypeByName("Vintagestory.Server.EntityDespawn");
            if (entityDespawnType != null)
                entityIdRef = AccessTools.FieldRefAccess<long>(entityDespawnType, "EntityId");

            var esType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemEntitySimulation");
            if (esType != null)
                trackingRangeSqRef = AccessTools.FieldRefAccess<int>(esType, "trackingRangeSq");

            var updateLists = AccessTools.Method(physMgr, "UpdateTrackedEntityLists",
                new[] { connClientType, typeof(int) });
            if (updateLists == null)
            {
                api.Logger.Warning("[Synergy] TrackingHysteresis: Could not find UpdateTrackedEntityLists, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(updateLists, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(updateLists,
                postfix: new HarmonyMethod(typeof(TrackingHysteresis), nameof(Postfix_UpdateTrackedEntityLists)));

            api.Logger.Notification("[Synergy] TrackingHysteresis: Entity tracking hysteresis active");
        }

        public static void Postfix_UpdateTrackedEntityLists(object client, int threadCount, object __instance)
        {
            if (disabled) return;

            try
            {
                if (outOfRangeRef == null || entityIdRef == null) return;

                var outOfRange = outOfRangeRef(client);
                if (outOfRange == null || outOfRange.Count == 0) return;

                var player = playerRef?.Invoke(client);
                if (player?.Entity == null) return;

                var playerPos = player.Entity.Pos;

                // Get tracking range squared
                int trackingRangeSq = 16384; // default 128²
                if (esRef != null && trackingRangeSqRef != null)
                {
                    var es = esRef(__instance);
                    if (es != null)
                        trackingRangeSq = trackingRangeSqRef(es);
                }

                var trackedEntities = trackedEntitiesRef?.Invoke(client);

                var toKeep = new List<int>();
                for (int i = 0; i < outOfRange.Count; i++)
                {
                    var despawnItem = outOfRange[i];
                    long entityId = entityIdRef(despawnItem);
                    var entity = sapi.World.GetEntityById(entityId);
                    if (entity == null) continue;

                    double motionLen = entity.Pos.Motion.Length();
                    double bufferBlocks = Math.Max(MinBufferBlocks, motionLen * SpeedMultiplier);
                    double trackingRange = Math.Sqrt(trackingRangeSq);
                    double bufferRangeSq = (trackingRange + bufferBlocks) * (trackingRange + bufferBlocks);

                    double dx = entity.Pos.X - playerPos.X;
                    double dy = entity.Pos.Y - playerPos.Y;
                    double dz = entity.Pos.Z - playerPos.Z;
                    double distSq = dx * dx + dy * dy + dz * dz;

                    if (distSq < bufferRangeSq)
                    {
                        toKeep.Add(i);
                        trackedEntities?.Add(entityId);
                    }
                }

                for (int i = toKeep.Count - 1; i >= 0; i--)
                {
                    outOfRange.RemoveAt(toKeep[i]);
                }
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] TrackingHysteresis: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
            }
        }
    }
}
