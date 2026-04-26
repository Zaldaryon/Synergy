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
    /// S4: Sort entity spawns by distance so nearby entities appear first after teleport.
    /// Sends farthest-first because client uses LIFO stack (EntityLoadQueue is Stack&lt;Entity&gt;)
    /// — inversion means nearest entities are processed first on client.
    /// Vanilla behavior preserved: same entities spawn, just in better order.
    /// </summary>
    public static class SpawnPriorityOrdering
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;

        // Cached reflection
        private static FieldInfo clientListField;
        private static FieldInfo nowInRangeField;
        private static FieldInfo playerField;
        private static FieldInfo entityField;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var physMgr = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
            if (physMgr == null)
            {
                api.Logger.Warning("[Synergy] S4: Could not find PhysicsManager, skipping");
                return;
            }

            var entityInRangeType = AccessTools.TypeByName("Vintagestory.Server.EntityInRange");
            if (entityInRangeType != null)
                entityField = AccessTools.Field(entityInRangeType, "Entity");

            var connClientType = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");
            if (connClientType != null)
            {
                nowInRangeField = AccessTools.Field(connClientType, "entitiesNowInRange");
                playerField = AccessTools.Field(connClientType, "Player");
            }

            clientListField = AccessTools.Field(physMgr, "ClientList");

            var sendChanges = AccessTools.Method(physMgr, "SendTrackedEntitiesStateChanges");
            if (sendChanges == null)
            {
                api.Logger.Warning("[Synergy] S4: Could not find SendTrackedEntitiesStateChanges, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(sendChanges, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(sendChanges,
                prefix: new HarmonyMethod(typeof(SpawnPriorityOrdering), nameof(Prefix_SendTrackedEntitiesStateChanges)));

            api.Logger.Notification("[Synergy] S4: Entity spawn priority ordering active");
        }

        public static void Prefix_SendTrackedEntitiesStateChanges(object __instance)
        {
            if (disabled || entityField == null || clientListField == null) return;

            try
            {
                var clientList = clientListField.GetValue(__instance) as System.Collections.IList;
                if (clientList == null || clientList.Count == 0) return;

                // Copy to avoid "Collection was modified" if another mod/thread modifies ClientList
                var clients = new object[clientList.Count];
                clientList.CopyTo(clients, 0);

                foreach (var client in clients)
                {
                    if (nowInRangeField == null) continue;

                    var nowInRange = nowInRangeField.GetValue(client) as System.Collections.IList;
                    if (nowInRange == null || nowInRange.Count <= 1) continue;

                    if (playerField == null) continue;
                    var player = playerField.GetValue(client) as IServerPlayer;
                    if (player?.Entity == null) continue;

                    var playerPos = player.Entity.Pos.XYZ;

                    var sortList = new List<(object item, double distSq)>(nowInRange.Count);
                    foreach (var item in nowInRange)
                    {
                        var entity = entityField.GetValue(item) as Entity;
                        if (entity == null)
                        {
                            sortList.Add((item, 0));
                            continue;
                        }
                        double dx = entity.Pos.X - playerPos.X;
                        double dy = entity.Pos.Y - playerPos.Y;
                        double dz = entity.Pos.Z - playerPos.Z;
                        sortList.Add((item, dx * dx + dy * dy + dz * dz));
                    }

                    // Farthest-first: client LIFO stack inverts → nearest processed first
                    sortList.Sort((a, b) => b.distSq.CompareTo(a.distSq));

                    nowInRange.Clear();
                    foreach (var (item, _) in sortList)
                    {
                        nowInRange.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] S4: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
            }
        }
    }
}
