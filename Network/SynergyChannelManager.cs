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
        /// <summary>
        /// Number of slots over which baseline resets are staggered.
        /// 30 slots at 33ms/tick = ~1s full cycle (matches vanilla forceUpdate cadence).
        /// Each tick, only entities with (EntityId % StaggerSlots == currentSlot) are invalidated,
        /// spreading absolute packets evenly instead of a synchronized storm.
        /// </summary>
        public const int StaggerSlots = 30;

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
        /// Incremented every tick (~33ms) on the main thread. Physics threads compare entity
        /// baseline generation against this. Baselines with age >= StaggerSlots are stale.
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

            // Increment generation every tick (~33ms). Baselines are invalidated when
            // (entityId % StaggerSlots) == (generation % StaggerSlots), so each entity
            // resets once per StaggerSlots ticks (~1s). This spreads absolute packets evenly
            // across the second instead of all firing on one tick.
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

        /// <summary>
        /// Removes a despawned entity's baseline from all connected delta clients.
        /// Prevents unbounded growth of per-client Baselines dictionaries from short-lived
        /// entities (dropped items, projectiles, falling blocks).
        /// </summary>
        public void PruneBaseline(long entityId)
        {
            foreach (var kvp in clients)
                kvp.Value.Baselines.TryRemove(entityId, out _);
        }

        public int ClientCount => clients.Count;
    }
}
