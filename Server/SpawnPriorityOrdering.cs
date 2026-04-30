using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Sort entity spawns by distance so nearby entities appear first after teleport.
    /// Sends farthest-first because client uses LIFO stack (EntityLoadQueue is Stack&lt;Entity&gt;)
    /// — inversion means nearest entities are processed first on client.
    /// Vanilla behavior preserved: same entities spawn, just in better order.
    /// </summary>
    public static class SpawnPriorityOrdering
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;

        // IL-emitted fast accessors via Harmony — FieldRef<object, F> for internal types
        private static AccessTools.FieldRef<object, System.Collections.IList> clientListRef;
        private static AccessTools.FieldRef<object, System.Collections.IList> nowInRangeRef;
        private static AccessTools.FieldRef<object, IServerPlayer> playerRef;
        private static AccessTools.FieldRef<object, Entity> entityRef;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var physMgr = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
            if (physMgr == null)
            {
                api.Logger.Warning("[Synergy] SpawnPriority: Could not find PhysicsManager, skipping");
                return;
            }

            var entityInRangeType = AccessTools.TypeByName("Vintagestory.Server.EntityInRange");
            if (entityInRangeType != null)
                entityRef = AccessTools.FieldRefAccess<Entity>(entityInRangeType, "Entity");

            var connClientType = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");
            if (connClientType != null)
            {
                nowInRangeRef = AccessTools.FieldRefAccess<System.Collections.IList>(connClientType, "entitiesNowInRange");
                playerRef = AccessTools.FieldRefAccess<IServerPlayer>(connClientType, "Player");
            }

            clientListRef = AccessTools.FieldRefAccess<System.Collections.IList>(physMgr, "ClientList");

            var sendChanges = AccessTools.Method(physMgr, "SendTrackedEntitiesStateChanges");
            if (sendChanges == null)
            {
                api.Logger.Warning("[Synergy] SpawnPriority: Could not find SendTrackedEntitiesStateChanges, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(sendChanges, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(sendChanges,
                prefix: new HarmonyMethod(typeof(SpawnPriorityOrdering), nameof(Prefix_SendTrackedEntitiesStateChanges)));

            api.Logger.Notification("[Synergy] SpawnPriority: Entity spawn priority ordering active");
        }

        public static void Prefix_SendTrackedEntitiesStateChanges(object __instance)
        {
            if (disabled || entityRef == null || clientListRef == null) return;

            try
            {
                var clientList = clientListRef(__instance);
                if (clientList == null || clientList.Count == 0) return;

                // Copy to avoid "Collection was modified" if another mod/thread modifies ClientList
                var clients = new object[clientList.Count];
                clientList.CopyTo(clients, 0);

                foreach (var client in clients)
                {
                    if (nowInRangeRef == null) continue;

                    var nowInRange = nowInRangeRef(client);
                    if (nowInRange == null || nowInRange.Count <= 1) continue;

                    if (playerRef == null) continue;
                    var player = playerRef(client);
                    if (player?.Entity == null) continue;

                    var playerPos = player.Entity.Pos.XYZ;

                    var sortList = new List<(object item, double distSq)>(nowInRange.Count);
                    foreach (var item in nowInRange)
                    {
                        var entity = entityRef(item);
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
                    sapi?.Logger.Warning("[Synergy] SpawnPriority: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
            }
        }
    }
}
