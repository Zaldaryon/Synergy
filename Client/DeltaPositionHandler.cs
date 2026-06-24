using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using Synergy.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace Synergy.Client
{
    /// <summary>
    /// Client side of the ack-based delta protocol (Quake 3 / Source model).
    ///
    /// Each datagram is tagged with a generation. A relative entry carries the generation of
    /// the baseline it was encoded against (BaseGen); the client reconstructs the absolute position
    /// from the value it held at that generation. The client keeps a small per-entity ring of recent
    /// values for that lookup, and periodically acks the exact datagram generations it received.
    /// Because the server only ever encodes against an acked baseline and resends until acked, a
    /// lost UDP packet costs at most a frame — there is no permanent desync.
    ///
    /// When connected to a vanilla / non-Synergy server this handler is never invoked.
    /// </summary>
    public class DeltaPositionHandler
    {
        private readonly ICoreClientAPI capi;
        private IClientNetworkChannel handshakeChannel;
        private IClientNetworkChannel deltaChannel;
        private bool serverHasSynergy;

        private sealed class ClientTrack
        {
            private SynergyChannelManager.PosSample[] ring;
            private int head, count;
            public int LastGen = -1;
            public int LastTick = -1;

            public void Push(int gen, long x, long y, long z, long mx, long my, long mz)
            {
                ring ??= new SynergyChannelManager.PosSample[SynergyChannelManager.RingCapacity];
                if (count == SynergyChannelManager.RingCapacity) { head = (head + 1) % SynergyChannelManager.RingCapacity; count--; }
                ring[(head + count) % SynergyChannelManager.RingCapacity] = new SynergyChannelManager.PosSample
                {
                    Gen = gen, X = x, Y = y, Z = z, MotionX = mx, MotionY = my, MotionZ = mz
                };
                count++;
            }

            /// <summary>Value held at the largest stored generation ≤ baseGen (the delta base).</summary>
            public bool TryGetBase(int baseGen, out SynergyChannelManager.PosSample sample)
            {
                sample = default;
                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    ref var s = ref ring[(head + i) % SynergyChannelManager.RingCapacity];
                    if (s.Gen <= baseGen && (!found || s.Gen > sample.Gen)) { sample = s; found = true; }
                }
                return found;
            }
        }

        private readonly Dictionary<long, ClientTrack> tracks = new();
        private readonly List<int> pendingAckGenerations = new();
        private readonly HashSet<int> pendingAckSet = new();
        private int ackTickCounter;
        private long ackListenerId;

        public DeltaPositionHandler(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public void Initialize()
        {
            handshakeChannel = capi.Network.RegisterChannel("synergy")
                .RegisterMessageType<SynergyHandshake>()
                .RegisterMessageType<SynergyAck>()
                .SetMessageHandler<SynergyHandshake>(OnServerHandshake);

            deltaChannel = capi.Network.RegisterUdpChannel("synergy-delta")
                .RegisterMessageType<DeltaPositionBatch>()
                .SetMessageHandler<DeltaPositionBatch>(OnDeltaBatch);

            capi.Event.OnEntityDespawn += OnEntityDespawn;

            // Send exact datagram acks every AckIntervalTicks ticks (~100ms) so the server can
            // advance only entity baselines that were actually included in received datagrams.
            ackListenerId = capi.Event.RegisterGameTickListener(_ => SendAckIfNeeded(), 33);

            capi.Logger.Notification("[Synergy] Client delta handler initialized");
        }

        public void Dispose()
        {
            tracks.Clear();
            pendingAckGenerations.Clear();
            pendingAckSet.Clear();
            serverHasSynergy = false;
            if (capi != null)
            {
                capi.Event.OnEntityDespawn -= OnEntityDespawn;
                capi.Event.UnregisterGameTickListener(ackListenerId);
            }
        }

        private void OnEntityDespawn(Entity entity, EntityDespawnData reason)
        {
            tracks.Remove(entity.EntityId);
        }

        private void OnServerHandshake(SynergyHandshake packet)
        {
            serverHasSynergy = true;
            capi.Logger.Notification("[Synergy] Server handshake received (v{0}, delta-ack={1})",
                packet.Version, (packet.Capabilities & SynergyHandshake.CapDeltaAck) != 0);

            if (handshakeChannel.Connected)
            {
                handshakeChannel.SendPacket(new SynergyHandshake
                {
                    Version = capi.ModLoader.GetMod("synergy")?.Info?.Version ?? "unknown",
                    Capabilities = SynergyHandshake.CapDeltaAck
                });
            }
        }

        private void SendAckIfNeeded()
        {
            if (++ackTickCounter < SynergyChannelManager.AckIntervalTicks) return;
            ackTickCounter = 0;
            if (pendingAckGenerations.Count == 0 || !serverHasSynergy) return;
            if (handshakeChannel == null || !handshakeChannel.Connected) return;

            int[] generations = pendingAckGenerations.ToArray();
            handshakeChannel.SendPacket(new SynergyAck
            {
                Generation = generations[generations.Length - 1],
                Generations = generations
            });
            pendingAckGenerations.Clear();
            pendingAckSet.Clear();
        }

        // Reusable decode buffer (main thread only, no concurrency)
        private readonly DeltaEntry[] decodeBuffer = new DeltaEntry[1024];

        private void OnDeltaBatch(DeltaPositionBatch batch)
        {
            if (batch?.Data == null || batch.Data.Length == 0) return;

            try
            {
                int count = DeltaCodec.Decode(batch.Data, decodeBuffer, out int generation);
                if (count == 0) return;

                int ackSafeCount = 0;
                for (int i = 0; i < count; i++)
                {
                    if (ApplyDelta(ref decodeBuffer[i], generation))
                        ackSafeCount++;
                }

                if (ShouldAdvanceAck(count, ackSafeCount))
                    QueueAck(generation);
            }
            catch (Exception ex)
            {
                capi.Logger.Debug("[Synergy] Delta decode error: {0}", ex.Message);
            }
        }

        public static bool ShouldAdvanceAck(int decodedCount, int ackSafeCount)
            => decodedCount > 0 && ackSafeCount == decodedCount;

        private void QueueAck(int generation)
        {
            if (pendingAckSet.Add(generation))
                pendingAckGenerations.Add(generation);
        }

        public static int CalculateTickDiff(int previousTick, int packetTick)
            => previousTick < 0 ? 1 : Math.Min(packetTick - previousTick, 5);

        private bool ApplyDelta(ref DeltaEntry delta, int generation)
        {
            Entity entity = capi.World.GetEntityById(delta.EntityId);
            if (entity == null) return false;

            if (!tracks.TryGetValue(delta.EntityId, out var track))
                tracks[delta.EntityId] = track = new ClientTrack();

            // Stale/reordered snapshot for this entity — we already have a newer one.
            if (generation <= track.LastGen) return true;

            bool isAbsolute = (delta.Flags & DeltaCodec.FlagAbsolute) != 0;
            bool teleport = (delta.Flags & DeltaCodec.FlagTeleport) != 0;

            long absX, absY, absZ, absMX, absMY, absMZ;
            if (isAbsolute || teleport)
            {
                absX = delta.DeltaX; absY = delta.DeltaY; absZ = delta.DeltaZ;
                absMX = delta.DeltaMotionX; absMY = delta.DeltaMotionY; absMZ = delta.DeltaMotionZ;
            }
            else if (track.TryGetBase(delta.BaseGen, out var b))
            {
                absX = b.X + delta.DeltaX; absY = b.Y + delta.DeltaY; absZ = b.Z + delta.DeltaZ;
                absMX = b.MotionX + delta.DeltaMotionX; absMY = b.MotionY + delta.DeltaMotionY; absMZ = b.MotionZ + delta.DeltaMotionZ;
            }
            else
            {
                // No stored baseline for this BaseGen — should not happen (the server only encodes
                // against a generation we acked, which is within the ring). Defensive: skip and wait
                // for the server's age-based absolute resync (within MaxBaseAge generations).
                return false;
            }

            int prevTick = track.LastTick;
            if (prevTick >= 0 && delta.Tick <= prevTick)
            {
                track.LastGen = generation;
                return true;
            }

            track.Push(generation, absX, absY, absZ, absMX, absMY, absMZ);
            track.LastGen = generation;
            track.LastTick = delta.Tick;

            // Match vanilla interpolation pacing: tickDiff is based on the per-entity packet tick.
            int tickDiff = CalculateTickDiff(prevTick, delta.Tick);
            entity.Attributes.SetInt("tickDiff", tickDiff);
            entity.Attributes.SetInt("tick", delta.Tick);

            // Position — inline deserialization: (double)v / 16384.0 and (float)v / 1024f
            var pos = entity.Pos;
            pos.X = (double)absX / 16384.0;
            pos.Y = (double)absY / 16384.0;
            pos.Z = (double)absZ / 16384.0;
            pos.Yaw = (float)delta.Yaw / 1024f;
            pos.Pitch = (float)delta.Pitch / 1024f;
            pos.Roll = (float)delta.Roll / 1024f;
            pos.HeadYaw = (float)delta.HeadYaw / 1024f;
            pos.HeadPitch = (float)delta.HeadPitch / 1024f;
            pos.Motion.X = (double)absMX / 16384.0;
            pos.Motion.Y = (double)absMY / 16384.0;
            pos.Motion.Z = (double)absMZ / 16384.0;

            if (entity is EntityAgent agent)
            {
                agent.BodyYawServer = (float)delta.BodyYaw / 1024f;
                agent.Controls.FromInt(delta.Controls & 0x210);
                if (agent.EntityId != (capi.World.Player?.Entity?.EntityId ?? -1))
                    agent.ServerControls.FromInt(delta.Controls);
            }

            (entity.SidedProperties == null ? null :
                entity.GetInterface<IMountable>()?.ControllingControls)?.FromInt(delta.MountControls);

            entity.OnReceivedServerPos(teleport);

            entity.Tags = new TagSetFast(Vector256.Create(
                (ulong)delta.TagsPart1, (ulong)delta.TagsPart2,
                (ulong)delta.TagsPart3, (ulong)delta.TagsPart4));

            return true;
        }

        public bool ServerHasSynergy => serverHasSynergy;
    }
}
