using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// S1: Add hysteresis buffer to entity tracking range to prevent pop-in/pop-out flickering.
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

        // Cached reflection — resolved once at init
        private static FieldInfo outOfRangeField;
        private static FieldInfo playerField;
        private static FieldInfo esField;
        private static FieldInfo trackingRangeSqField;
        private static FieldInfo trackedEntitiesField;
        private static FieldInfo entityIdField;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var physMgr = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
            if (physMgr == null)
            {
                api.Logger.Warning("[Synergy] S1: Could not find PhysicsManager, skipping");
                return;
            }

            var connClientType = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");
            if (connClientType == null)
            {
                api.Logger.Warning("[Synergy] S1: Could not find ConnectedClient, skipping");
                return;
            }

            // Cache all FieldInfo at init
            outOfRangeField = AccessTools.Field(connClientType, "entitiesNowOutOfRange");
            playerField = AccessTools.Field(connClientType, "Player");
            trackedEntitiesField = AccessTools.Field(connClientType, "TrackedEntities");
            esField = AccessTools.Field(physMgr, "es");

            var entityDespawnType = AccessTools.TypeByName("Vintagestory.Server.EntityDespawn");
            if (entityDespawnType != null)
                entityIdField = AccessTools.Field(entityDespawnType, "EntityId");

            // Resolve trackingRangeSq field type from es field's type
            var esType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemEntitySimulation");
            if (esType != null)
                trackingRangeSqField = AccessTools.Field(esType, "trackingRangeSq");

            var updateLists = AccessTools.Method(physMgr, "UpdateTrackedEntityLists",
                new[] { connClientType, typeof(int) });
            if (updateLists == null)
            {
                api.Logger.Warning("[Synergy] S1: Could not find UpdateTrackedEntityLists, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(updateLists, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(updateLists,
                postfix: new HarmonyMethod(typeof(TrackingHysteresis), nameof(Postfix_UpdateTrackedEntityLists)));

            api.Logger.Notification("[Synergy] S1: Entity tracking hysteresis active");
        }

        public static void Postfix_UpdateTrackedEntityLists(object client, int threadCount, object __instance)
        {
            if (disabled) return;

            try
            {
                if (outOfRangeField == null || entityIdField == null) return;

                var outOfRange = outOfRangeField.GetValue(client) as System.Collections.IList;
                if (outOfRange == null || outOfRange.Count == 0) return;

                var player = playerField?.GetValue(client) as IServerPlayer;
                if (player?.Entity == null) return;

                var playerPos = player.Entity.Pos;

                // Get tracking range squared
                int trackingRangeSq = 16384; // default 128²
                if (esField != null && trackingRangeSqField != null)
                {
                    var es = esField.GetValue(__instance);
                    if (es != null)
                    {
                        var val = trackingRangeSqField.GetValue(es);
                        if (val is int intVal) trackingRangeSq = intVal;
                        else if (val is long longVal) trackingRangeSq = (int)longVal;
                    }
                }

                var trackedEntities = trackedEntitiesField?.GetValue(client) as HashSet<long>;

                var toKeep = new List<int>();
                for (int i = 0; i < outOfRange.Count; i++)
                {
                    var despawnItem = outOfRange[i];
                    long entityId;
                    var idVal = entityIdField.GetValue(despawnItem);
                    if (idVal is long l) entityId = l;
                    else if (idVal is int iv) entityId = iv;
                    else continue;
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
                    sapi?.Logger.Warning("[Synergy] S1: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
            }
        }
    }
}
