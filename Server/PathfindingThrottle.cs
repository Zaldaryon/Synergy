using System;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Essentials;

namespace Synergy.Server
{
    /// <summary>
    /// Phase 2: DEAR-style pathfinding throttle — reduce pathfinding frequency for distant entities.
    ///
    /// Entities close to players pathfind every request. Distant entities have requests
    /// throttled based on distance² to nearest player, using the task's startBlockPos.
    ///
    /// Reference: Airplane/Pufferfish DEAR (Dynamic Entity Activation Range) for Minecraft.
    /// Formula: skip if (distance² / 2048) > (tickCounter % maxFreq)
    ///   - &lt;32 blocks: every request accepted
    ///   - 32-64 blocks: ~1 in 2 accepted
    ///   - 64-96 blocks: ~1 in 4 accepted
    ///   - &gt;96 blocks: ~1 in 8 accepted (entities beyond EntityActivationRange don't tick at all)
    ///
    /// Skipped tasks are immediately marked Finished with waypoints=null (no path found).
    /// The entity's AI will retry on the next tick — the throttle just delays the response.
    ///
    /// Vanilla behavior: entities react slightly slower at distance (imperceptible at 50+ blocks).
    /// </summary>
    public static class PathfindingThrottle
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private static int tickCounter;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;
            tickCounter = 0;

            var asyncType = typeof(PathfindingAsync);
            var enqueue = AccessTools.Method(asyncType, "EnqueuePathfinderTask",
                new[] { typeof(PathfinderTask) });
            if (enqueue == null)
            {
                api.Logger.Warning("[Synergy] PathfindingThrottle: Could not find EnqueuePathfinderTask, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(enqueue, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(enqueue,
                prefix: new HarmonyMethod(typeof(PathfindingThrottle), nameof(Prefix_Enqueue)));

            api.Event.RegisterGameTickListener(_ => Interlocked.Increment(ref tickCounter), 20);

            api.Logger.Notification("[Synergy] PathfindingThrottle: Pathfinding distance throttle active");
        }

        public static bool Prefix_Enqueue(PathfinderTask task)
        {
            if (disabled || task == null) return true;

            try
            {
                var startPos = task.startBlockPos;
                if (startPos == null) return true;

                // Find nearest player distance²
                double minDistSq = double.MaxValue;
                foreach (var player in sapi.World.AllOnlinePlayers)
                {
                    if (player.Entity == null) continue;
                    var ppos = player.Entity.Pos;
                    double dx = startPos.X - ppos.X;
                    double dy = startPos.Y - ppos.Y;
                    double dz = startPos.Z - ppos.Z;
                    double distSq = dx * dx + dy * dy + dz * dz;
                    if (distSq < minDistSq) minDistSq = distSq;
                }

                // Close entities: always accept (< 32 blocks = 1024 distSq)
                if (minDistSq < 1024 || minDistSq == double.MaxValue) return true;

                // Throttle: skip proportional to distance
                // freq = distSq / 2048, clamped to [1, 8]
                int freq = Math.Clamp((int)(minDistSq / 2048), 1, 8);
                int tick = Volatile.Read(ref tickCounter);
                if (tick % freq != 0)
                {
                    // Skip this request — mark as finished with no path
                    task.waypoints = null;
                    task.Finished = true;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] PathfindingThrottle: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
