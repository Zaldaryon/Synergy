using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Reduce position update rate for distant entities from 30Hz to 15Hz.
    /// IsTracked==2 (≤50 blocks): every tick (30Hz). IsTracked==1 (50-128 blocks): every 2nd tick (15Hz).
    /// Fast entities (motion > threshold) always send at 30Hz regardless of distance.
    /// Uses a separate frame counter to avoid interfering with vanilla's tick attribute.
    /// Vanilla behavior preserved: interpolation handles variable rates via tickDiff.
    /// </summary>
    public static class DistanceSendFrequency
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private const double FastMotionThresholdSq = 0.04; // ~0.2 blocks/tick
        private static int frameCounter;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;
            frameCounter = 0;

            var physMgr = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
            if (physMgr == null)
            {
                api.Logger.Warning("[Synergy] SendFrequency: Could not find PhysicsManager, skipping");
                return;
            }

            var pktType = AccessTools.TypeByName("Packet_EntityPosition")
                ?? AccessTools.TypeByName("Vintagestory.Server.Packet_EntityPosition");
            var animType = AccessTools.TypeByName("Vintagestory.Common.Network.Packets.AnimationPacket")
                ?? AccessTools.TypeByName("Vintagestory.Server.AnimationPacket");
            if (pktType == null || animType == null)
            {
                api.Logger.Warning("[Synergy] SendFrequency: Could not find packet types, skipping");
                return;
            }

            var dictPosType = typeof(Dictionary<,>).MakeGenericType(typeof(long), pktType);
            var dictAnimType = typeof(Dictionary<,>).MakeGenericType(typeof(long), animType);

            var buildPos = AccessTools.Method(physMgr, "BuildPositionPacket",
                new[] { typeof(Entity), typeof(bool), dictPosType, dictAnimType });
            if (buildPos == null)
            {
                api.Logger.Warning("[Synergy] SendFrequency: Could not find BuildPositionPacket, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(buildPos, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(buildPos,
                prefix: new HarmonyMethod(typeof(DistanceSendFrequency), nameof(Prefix_BuildPositionPacket)));

            // Increment frame counter each server tick (atomic for thread safety)
            api.Event.RegisterGameTickListener(_ => Interlocked.Increment(ref frameCounter), 1);

            api.Logger.Notification("[Synergy] SendFrequency: Distance-based send frequency active");
        }

        public static bool Prefix_BuildPositionPacket(Entity entity, bool forceUpdate)
        {
            if (disabled || forceUpdate) return true;

            try
            {
                // Only throttle low-res tracked entities (50-128 blocks)
                if (entity.IsTracked != 1) return true;

                // Fast-moving entities always send
                var motion = entity.Pos.Motion;
                if (motion.X * motion.X + motion.Y * motion.Y + motion.Z * motion.Z > FastMotionThresholdSq)
                    return true;

                // Skip odd frames for distant entities (30Hz → 15Hz)
                if (Volatile.Read(ref frameCounter) % 2 != 0)
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] SendFrequency: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
