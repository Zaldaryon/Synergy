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
    /// Model (Quake 3 / Source / Gaffer): the server delta-encodes each entity against the value
    /// the client last ACKnowledged. A lost UDP packet is never acked, so the server keeps resending
    /// the cumulative change against the older acked base until an ack advances it — no permanent
    /// desync is possible. Each entity keeps a small ring of its un-acked sent values so an incoming
    /// ack can promote the exact value the client held at the acked generation.
    /// </summary>
    public class SynergyChannelManager
    {
        /// <summary>Snapshot generation counter, ++ every ~33ms tick. Tags each batch; the unit acks reference.</summary>
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

            // Ring of distinct un-acked sent values, oldest..newest, strictly increasing Gen.
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

            /// <summary>Record a distinct new value sent at <paramref name="gen"/>. Drops the oldest if full.</summary>
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
            /// Advance the acked baseline to the newest sent value with Gen ≤ ackGen, and drop all
            /// samples up to it. Idempotent and monotonic — older/duplicate acks are no-ops.
            /// </summary>
            public void Promote(int ackGen)
            {
                bool promoted = false;
                PosSample promo = default;
                while (count > 0 && ring[head].Gen <= ackGen)
                {
                    promo = ring[head];
                    head = (head + 1) % RingCapacity;
                    count--;
                    promoted = true;
                }
                if (promoted && (!HasAcked || promo.Gen > AckedGen))
                {
                    // Baseline VALUE comes from the newest sent sample ≤ ackGen, but AckedGen is set to
                    // ackGen — the generation the client actually received & acked — NOT promo.Gen.
                    // The client keys its reconstruction ring by received generation; the server's
                    // sample generation may be one the client never got (it recovered the value via a
                    // later resend against an older base). Encoding the next delta against promo.Gen
                    // would point the client at a baseline it doesn't hold → permanent offset. ackGen
                    // is, by definition, a generation the client received and reconstructed.
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
            public int LastAckGen = -1; // guarded by Volatile; drops stale/out-of-order acks
        }

        private readonly ICoreServerAPI sapi;
        private IServerNetworkChannel handshakeChannel;
        private IServerNetworkChannel deltaChannel;
        private long tickListenerId;

        private int baselineGeneration;
        public int BaselineGeneration => Volatile.Read(ref baselineGeneration);

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

            // Drop stale/out-of-order acks (cumulative — only the highest matters).
            int prev = Volatile.Read(ref state.LastAckGen);
            if (ack.Generation <= prev) return;
            Volatile.Write(ref state.LastAckGen, ack.Generation);

            foreach (var kvp in state.Tracks)
            {
                var track = kvp.Value;
                lock (track) track.Promote(ack.Generation);
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
