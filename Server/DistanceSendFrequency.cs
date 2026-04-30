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
    /// Also suppress force-update position packets for truly stationary entities.
    ///
    /// Distance throttle:
    /// IsTracked==2 (≤50 blocks): every tick (30Hz). IsTracked==1 (50-128 blocks): every 2nd tick (15Hz).
    /// Fast entities (motion > threshold) always send at 30Hz regardless of distance.
    ///
    /// Stationary suppression:
    /// Vanilla sends a force-update position packet every 30 ticks (~1s) even for entities that
    /// haven't moved. This wastes ~100 bytes × entities/s per client. We skip the packet when
    /// forceUpdate=true but position/angles/motion haven't changed since last send.
    /// The tick counter only increments when a packet is actually created (vanilla behavior),
    /// so client interpolation (tickDiff) remains correct.
    ///
    /// Vanilla behavior preserved: interpolation handles variable rates via tickDiff.
    /// CompletePositionUpdate still runs (it's called after BuildPositionPacket regardless).
    /// </summary>
    public static class DistanceSendFrequency
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private const double FastMotionThresholdSq = 0.04; // ~0.2 blocks/tick
        private static int frameCounter;

        // Fast accessor for internal Entity.tagsDirty field
        private static AccessTools.FieldRef<Entity, bool> tagsDirtyRef;

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

            tagsDirtyRef = AccessTools.FieldRefAccess<Entity, bool>("tagsDirty");

            harmony.Patch(buildPos,
                prefix: new HarmonyMethod(typeof(DistanceSendFrequency), nameof(Prefix_BuildPositionPacket)));

            // Increment frame counter each server tick (atomic for thread safety)
            api.Event.RegisterGameTickListener(_ => Interlocked.Increment(ref frameCounter), 1);

            api.Logger.Notification("[Synergy] SendFrequency: Distance-based send frequency active");
        }

        public static bool Prefix_BuildPositionPacket(Entity entity, bool forceUpdate)
        {
            if (disabled) return true;

            try
            {
                // Players handle their own position packets via ServerUdpNetwork
                if (entity is EntityPlayer) return true;

                // Stationary suppression: skip force-update when entity hasn't moved at all.
                // The tick counter (GetIntAndIncrement) only runs when vanilla creates a packet,
                // so skipping here keeps tick counters correct. CompletePositionUpdate runs
                // regardless (it's called after BuildPositionPacket in vanilla).
                if (forceUpdate)
                {
                    var agent = entity as EntityAgent;
                    bool controlsDirty = agent != null && agent.Controls.Dirty;
                    if (!controlsDirty && !(tagsDirtyRef?.Invoke(entity) ?? false) &&
                        entity.Pos.BasicallySameAs(entity.PreviousServerPos))
                    {
                        return false;
                    }
                    return true;
                }

                // Distance throttle: only for distant tracked entities (50-128 blocks)
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
