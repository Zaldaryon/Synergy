using System;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Synergy.Server
{
    /// <summary>
    /// Throttle entity repulsion calculations based on distance to nearest player.
    ///
    /// Vanilla runs EntityBehaviorRepulseAgents.OnGameTick every tick per entity with
    /// zero distance-based throttling. In dense animal pens this is O(N²) — 50 animals
    /// means 2500 pair checks per tick.
    ///
    /// Throttle tiers:
    ///   &lt;32 blocks: every tick (vanilla)
    ///   32-64 blocks: every 4th tick
    ///   &gt;64 blocks: skip entirely
    ///
    /// Repulsion is purely cosmetic at distance — no gameplay impact from skipping.
    /// Entities may overlap slightly when far from players; self-corrects on approach.
    ///
    /// Reference: OptiTime has client-side RepulseAgentsOptimization (skips beyond 64 blocks).
    /// This is the server-side equivalent.
    /// </summary>
    public static class RepulseAgentsThrottle
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

            var behaviorType = typeof(EntityBehaviorRepulseAgents);
            var onGameTick = AccessTools.Method(behaviorType, "OnGameTick", new[] { typeof(float) });
            if (onGameTick == null)
            {
                api.Logger.Warning("[Synergy] P16: Could not find EntityBehaviorRepulseAgents.OnGameTick, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(onGameTick, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(onGameTick,
                prefix: new HarmonyMethod(typeof(RepulseAgentsThrottle), nameof(Prefix_OnGameTick)));

            api.Event.RegisterGameTickListener(_ => Interlocked.Increment(ref tickCounter), 1);

            api.Logger.Notification("[Synergy] P16: Entity repulsion throttle active");
        }

        public static bool Prefix_OnGameTick(EntityBehaviorRepulseAgents __instance)
        {
            if (disabled) return true;

            try
            {
                var entity = __instance.entity;
                if (entity == null) return true;

                float dist = entity.NearestPlayerDistance;

                // Close entities: always run (vanilla behavior)
                if (dist < 32f) return true;

                // Beyond 64 blocks: skip entirely
                if (dist > 64f) return false;

                // 32-64 blocks: every 4th tick
                int tick = Volatile.Read(ref tickCounter);
                if (tick % 4 != 0) return false;

                return true;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] P16: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
