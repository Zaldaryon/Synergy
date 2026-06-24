using System;
using System.Collections.Concurrent;
using System.Threading;
using Synergy.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy
{
    /// <summary>
    /// Manages the Synergy network channel: handshake, client capabilities, and the per-client
    /// ack-based delta state.
    ///
    /// Model (Quake 3 / Source / Gaffer): the server delta-encodes each entity against the value the
    /// client last ACKnowledged. A lost UDP datagram is never acked, so the server keeps resending the
    /// current change against the older acked base until a later datagram carrying that entity is
    /// acked. Each entity keeps a small ring of sent values by datagram generation, and an ack only
    /// promotes tracks that were actually present in that exact datagram.
    /// </summary>
    public class SynergyChannelManager
    {
        /// <summary>Client sends exact datagram acks every N ticks (~100ms).</summary>
        public const int AckIntervalTicks = 3; // client sends an ack every N ticks (~100ms)

        /// <summary>Max un-acked sends kept per entity. Bounds delta age (and so delta magnitude) and memory.</summary>
        public const int RingCapacity = 64;

        /// <summary>If the acked base is older than this many generations, send absolute instead (reseed).</summary>
        public const int MaxBaseAge = RingCapacity - 2;

        public struct PosSample
        {
            public int Gen;
            public long X, Y, Z, MotionX, MotionY, MotionZ;
        }

        /// <summary>
        /// Per-client, per-entity delta state. All access is serialised via <c>lock(track)</c>:
        /// physics threads call <see cref="RecordSend"/> while the network thread calls
        /// <see cref="Promote"/>.
        /// </summary>
        public sealed class EntityTrack
        {
            // Acked baseline — the entity value as of AckedGen, confirmed received by the client.
            public long X, Y, Z, MotionX, MotionY, MotionZ;
            public int AckedGen;
            public bool HasAcked;

            // Last non-position fields sent (yaw/controls/tags/...). Position+motion are delta-encoded
            // against the acked baseline; everything else is sent verbatim every packet, so to resend a
            // now-stationary entity (no fresh vanilla packet) we reuse the last values seen.
            public int LYaw, LPitch, LRoll, LHeadYaw, LHeadPitch, LBodyYaw, LControls, LTick, LPosVersion, LMountControls;
            public long LTags1, LTags2, LTags3, LTags4;

            public void SetLastFields(int yaw, int pitch, int roll, int headYaw, int headPitch, int bodyYaw,
                int controls, int tick, int posVer, int mountControls, long t1, long t2, long t3, long t4)
            {
                LYaw = yaw; LPitch = pitch; LRoll = roll; LHeadYaw = headYaw; LHeadPitch = headPitch; LBodyYaw = bodyYaw;
                LControls = controls; LTick = tick; LPosVersion = posVer; LMountControls = mountControls;
                LTags1 = t1; LTags2 = t2; LTags3 = t3; LTags4 = t4;
            }

            public bool AckedEquals(long x, long y, long z, long mx, long my, long mz)
                => HasAcked && X == x && Y == y && Z == z && MotionX == mx && MotionY == my && MotionZ == mz;

            // Ring of un-acked sent datagrams for this entity, oldest..newest, strictly increasing Gen.
            private PosSample[] ring;
            private int head;   // index of oldest
            private int count;

            public bool HasPending => count > 0;

            /// <summary>True if v equals the newest pending value (or, if none pending, the acked base).</summary>
            public bool LatestEquals(long x, long y, long z, long mx, long my, long mz)
            {
                if (count > 0)
                {
                    ref var s = ref ring[(head + count - 1) % RingCapacity];
                    return s.X == x && s.Y == y && s.Z == z && s.MotionX == mx && s.MotionY == my && s.MotionZ == mz;
                }
                return HasAcked && X == x && Y == y && Z == z && MotionX == mx && MotionY == my && MotionZ == mz;
            }

            /// <summary>Newest pending sample if any, else the acked base. Used to resend a stationary entity.</summary>
            public PosSample Latest()
            {
                if (count > 0) return ring[(head + count - 1) % RingCapacity];
                return new PosSample { Gen = AckedGen, X = X, Y = Y, Z = Z, MotionX = MotionX, MotionY = MotionY, MotionZ = MotionZ };
            }

            /// <summary>Record a value sent in datagram <paramref name="gen"/>. Drops the oldest if full.</summary>
            public void RecordSend(int gen, long x, long y, long z, long mx, long my, long mz)
            {
                ring ??= new PosSample[RingCapacity];
                if (count == RingCapacity) { head = (head + 1) % RingCapacity; count--; }
                ring[(head + count) % RingCapacity] = new PosSample
                {
                    Gen = gen, X = x, Y = y, Z = z, MotionX = mx, MotionY = my, MotionZ = mz
                };
                count++;
            }

            /// <summary>
            /// Advance the acked baseline only if this entity was included in the exact acked
            /// datagram. This prevents one received sibling datagram from acknowledging another
            /// datagram that was lost.
            /// </summary>
            public void Promote(int ackGen)
            {
                int promoteOffset = -1;
                PosSample promo = default;

                for (int i = 0; i < count; i++)
                {
                    ref var sample = ref ring[(head + i) % RingCapacity];
                    if (sample.Gen == ackGen)
                    {
                        promoteOffset = i;
                        promo = sample;
                        break;
                    }
                }

                if (promoteOffset < 0) return;

                int removeCount = promoteOffset + 1;
                head = (head + removeCount) % RingCapacity;
                count -= removeCount;

                if (!HasAcked || ackGen > AckedGen)
                {
                    X = promo.X; Y = promo.Y; Z = promo.Z;
                    MotionX = promo.MotionX; MotionY = promo.MotionY; MotionZ = promo.MotionZ;
                    AckedGen = ackGen;
                    HasAcked = true;
                }
            }
        }

        public class ClientState
        {
            public int Capabilities;
            public readonly ConcurrentDictionary<long, EntityTrack> Tracks = new();
        }

        private readonly ICoreServerAPI sapi;
        private IServerNetworkChannel handshakeChannel;
        private IServerNetworkChannel deltaChannel;
        private long tickListenerId;

        private int baselineGeneration;
        public int BaselineGeneration => Volatile.Read(ref baselineGeneration);

        public int NextGeneration() => Interlocked.Increment(ref baselineGeneration);

        private readonly ConcurrentDictionary<string, ClientState> clients = new();

        public SynergyChannelManager(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
        }

        public void Initialize()
        {
            handshakeChannel = sapi.Network.RegisterChannel("synergy")
                .RegisterMessageType<SynergyHandshake>()
                .RegisterMessageType<SynergyAck>()
                .SetMessageHandler<SynergyHandshake>(OnClientHandshake)
                .SetMessageHandler<SynergyAck>(OnClientAck);

            deltaChannel = sapi.Network.RegisterUdpChannel("synergy-delta")
                .RegisterMessageType<DeltaPositionBatch>();

            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

            // Snapshot generation ++ every ~33ms (one server tick). Tags each batch; acks reference it.
            tickListenerId = sapi.Event.RegisterGameTickListener(_ =>
            {
                Interlocked.Increment(ref baselineGeneration);
            }, 33);

            sapi.Logger.Notification("[Synergy] Channel manager initialized");
        }

        public void Dispose()
        {
            if (sapi != null)
            {
                sapi.Event.UnregisterGameTickListener(tickListenerId);
                sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
                sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
            }
            clients.Clear();
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            try
            {
                handshakeChannel.SendPacket(new SynergyHandshake
                {
                    Version = sapi.ModLoader.GetMod("synergy")?.Info?.Version ?? "unknown",
                    Capabilities = SynergyHandshake.CapDeltaAck
                }, player);
            }
            catch (Exception ex)
            {
                sapi.Logger.Debug("[Synergy] Could not send handshake to {0}: {1}", player.PlayerName, ex.Message);
            }
        }

        private void OnClientHandshake(IServerPlayer player, SynergyHandshake packet)
        {
            var state = new ClientState
            {
                Capabilities = packet.Capabilities & SynergyHandshake.CapDeltaAck
            };
            clients[player.PlayerUID] = state;

            bool delta = (state.Capabilities & SynergyHandshake.CapDeltaAck) != 0;
            sapi.Logger.Notification("[Synergy] Handshake: {0} v{1} (delta-ack={2})",
                player.PlayerName, packet.Version, delta);
            SynergyMetrics.SetDeltaClients(ClientCount);
        }

        private void OnClientAck(IServerPlayer player, SynergyAck ack)
        {
            if (!clients.TryGetValue(player.PlayerUID, out var state)) return;

            if (ack.Generations != null && ack.Generations.Length > 0)
            {
                foreach (int generation in ack.Generations)
                    PromoteReceivedGeneration(state, generation);
                return;
            }

            PromoteReceivedGeneration(state, ack.Generation);
        }

        private static void PromoteReceivedGeneration(ClientState state, int generation)
        {
            if (generation < 0) return;

            foreach (var kvp in state.Tracks)
            {
                var track = kvp.Value;
                lock (track) track.Promote(generation);
            }
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            clients.TryRemove(player.PlayerUID, out _);
            SynergyMetrics.SetDeltaClients(ClientCount);
        }

        public ClientState GetDeltaClient(string playerUid)
        {
            if (clients.TryGetValue(playerUid, out var state) &&
                (state.Capabilities & SynergyHandshake.CapDeltaAck) != 0)
                return state;
            return null;
        }

        public void SendDeltaBatch(DeltaPositionBatch batch, IServerPlayer player)
        {
            try { deltaChannel.SendPacket(batch, player); }
            catch { /* Player may have disconnected — ignore; circuit breaker handles repeats */ }
        }

        /// <summary>Remove a despawned entity's track from all clients (short-lived entity churn).</summary>
        public void PruneBaseline(long entityId)
        {
            foreach (var kvp in clients)
                kvp.Value.Tracks.TryRemove(entityId, out _);
        }

        public int ClientCount => clients.Count;
    }
}
