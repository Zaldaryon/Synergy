using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Synergy.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy
{
    /// <summary>
    /// Manages the Synergy network channel: handshake protocol, client capability tracking,
    /// and per-client delta encoding baselines.
    ///
    /// Baseline reset strategy: instead of clearing dictionaries (race with physics threads),
    /// we use a generation counter. Physics threads read the counter; when it changes, they
    /// know baselines are stale and send absolute. Zero contention, zero allocation.
    /// </summary>
    public class SynergyChannelManager
    {
        public struct EntityBaseline
        {
            public long X, Y, Z;
            public long MotionX, MotionY, MotionZ;
            public int Generation; // matches BaselineGeneration when valid
        }

        public class ClientState
        {
            public int Capabilities;
            public readonly ConcurrentDictionary<long, EntityBaseline> Baselines = new();
        }

        private readonly ICoreServerAPI sapi;
        private IServerNetworkChannel handshakeChannel;
        private IServerNetworkChannel deltaChannel;
        private long tickListenerId;

        /// <summary>
        /// Incremented every ~1s on the main thread. Physics threads compare entity baseline
        /// generation against this. Mismatch = send absolute and update baseline.
        /// Volatile read/write — no lock needed.
        /// </summary>
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
                .SetMessageHandler<SynergyHandshake>(OnClientHandshake);

            deltaChannel = sapi.Network.RegisterUdpChannel("synergy-delta")
                .RegisterMessageType<DeltaPositionBatch>();

            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

            // Bump generation every ~1s to force periodic absolute packets (matches vanilla forceUpdate).
            // Also prune stale baselines from previous generations to prevent memory growth.
            tickListenerId = sapi.Event.RegisterGameTickListener(_ =>
            {
                int oldGen = Volatile.Read(ref baselineGeneration);
                Interlocked.Increment(ref baselineGeneration);

                // Prune baselines from generations older than the one we just retired.
                // Safe: ConcurrentDictionary iteration tolerates concurrent modifications.
                // Physics threads may write new baselines during pruning — those will have
                // the new generation and won't be removed.
                foreach (var kvp in clients)
                {
                    var baselines = kvp.Value.Baselines;
                    foreach (var entry in baselines)
                    {
                        if (entry.Value.Generation < oldGen)
                        {
                            // Atomic remove only if value hasn't been updated by a physics thread
                            ((ICollection<KeyValuePair<long, EntityBaseline>>)baselines).Remove(entry);
                        }
                    }
                }
            }, 1000);

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
                    Capabilities = SynergyHandshake.CapDeltaEncoding
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
                Capabilities = packet.Capabilities & SynergyHandshake.CapDeltaEncoding
            };
            clients[player.PlayerUID] = state;

            bool delta = (state.Capabilities & SynergyHandshake.CapDeltaEncoding) != 0;
            sapi.Logger.Notification("[Synergy] Handshake: {0} v{1} (delta={2})",
                player.PlayerName, packet.Version, delta);
            SynergyMetrics.SetDeltaClients(ClientCount);
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            clients.TryRemove(player.PlayerUID, out _);
            SynergyMetrics.SetDeltaClients(ClientCount);
        }

        public ClientState GetDeltaClient(string playerUid)
        {
            if (clients.TryGetValue(playerUid, out var state) &&
                (state.Capabilities & SynergyHandshake.CapDeltaEncoding) != 0)
                return state;
            return null;
        }

        public void SendDeltaBatch(DeltaPositionBatch batch, IServerPlayer player)
        {
            try { deltaChannel.SendPacket(batch, player); }
            catch { /* Player may have disconnected — ignore, circuit breaker handles repeated failures */ }
        }

        public int ClientCount => clients.Count;
    }
}
