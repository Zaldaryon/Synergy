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
    /// Client-side handler for delta-encoded entity position packets.
    /// Receives DeltaPositionBatch, reconstructs absolute positions, and applies
    /// them directly to entities via public API — zero reflection, zero packet reconstruction.
    ///
    /// When connected to a vanilla server (no Synergy), this handler is never invoked.
    /// </summary>
    public class DeltaPositionHandler
    {
        private readonly ICoreClientAPI capi;
        private IClientNetworkChannel handshakeChannel;
        private IClientNetworkChannel deltaChannel;
        private bool serverHasSynergy;

        // Per-entity baselines for delta reconstruction (client-side, main thread only)
        private readonly Dictionary<long, SynergyChannelManager.EntityBaseline> baselines = new();

        public DeltaPositionHandler(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public void Initialize()
        {
            handshakeChannel = capi.Network.RegisterChannel("synergy")
                .RegisterMessageType<SynergyHandshake>()
                .SetMessageHandler<SynergyHandshake>(OnServerHandshake);

            deltaChannel = capi.Network.RegisterUdpChannel("synergy-delta")
                .RegisterMessageType<DeltaPositionBatch>()
                .SetMessageHandler<DeltaPositionBatch>(OnDeltaBatch);

            capi.Event.OnEntityDespawn += OnEntityDespawn;

            capi.Logger.Notification("[Synergy] Client delta handler initialized");
        }

        public void Dispose()
        {
            baselines.Clear();
            serverHasSynergy = false;
            if (capi != null)
                capi.Event.OnEntityDespawn -= OnEntityDespawn;
        }

        private void OnEntityDespawn(Entity entity, EntityDespawnData reason)
        {
            baselines.Remove(entity.EntityId);
        }

        private void OnServerHandshake(SynergyHandshake packet)
        {
            serverHasSynergy = true;
            capi.Logger.Notification("[Synergy] Server handshake received (v{0}, delta={1})",
                packet.Version, (packet.Capabilities & SynergyHandshake.CapDeltaEncoding) != 0);

            if (handshakeChannel.Connected)
            {
                handshakeChannel.SendPacket(new SynergyHandshake
                {
                    Version = capi.ModLoader.GetMod("synergy")?.Info?.Version ?? "unknown",
                    Capabilities = SynergyHandshake.CapDeltaEncoding
                });
            }
        }

        // Reusable decode buffer (main thread only, no concurrency)
        private readonly DeltaEntry[] decodeBuffer = new DeltaEntry[1024];

        private void OnDeltaBatch(DeltaPositionBatch batch)
        {
            if (batch?.Data == null || batch.Data.Length == 0) return;

            try
            {
                int count = DeltaCodec.Decode(batch.Data, decodeBuffer);
                for (int i = 0; i < count; i++)
                    ApplyDelta(ref decodeBuffer[i]);
            }
            catch (Exception ex)
            {
                capi.Logger.Debug("[Synergy] Delta decode error: {0}", ex.Message);
            }
        }

        private void ApplyDelta(ref DeltaEntry delta)
        {
            Entity entity = capi.World.GetEntityById(delta.EntityId);
            if (entity == null) return;

            // Stale packet check (matches vanilla HandleSinglePacket)
            int prevTick = entity.Attributes.GetInt("tick");
            if (delta.Tick <= prevTick) return;

            bool isAbsolute = (delta.Flags & DeltaCodec.FlagAbsolute) != 0;
            bool teleport = (delta.Flags & DeltaCodec.FlagTeleport) != 0;

            // Reconstruct absolute values
            long absX, absY, absZ, absMX, absMY, absMZ;

            if (isAbsolute || teleport ||
                !baselines.TryGetValue(delta.EntityId, out var baseline))
            {
                absX = delta.DeltaX;
                absY = delta.DeltaY;
                absZ = delta.DeltaZ;
                absMX = delta.DeltaMotionX;
                absMY = delta.DeltaMotionY;
                absMZ = delta.DeltaMotionZ;
            }
            else
            {
                absX = baseline.X + delta.DeltaX;
                absY = baseline.Y + delta.DeltaY;
                absZ = baseline.Z + delta.DeltaZ;
                absMX = baseline.MotionX + delta.DeltaMotionX;
                absMY = baseline.MotionY + delta.DeltaMotionY;
                absMZ = baseline.MotionZ + delta.DeltaMotionZ;
            }

            // Always update client baseline (including first-packet).
            // Previous bug: early return on prevTick==0 skipped this, causing desync
            // on the next relative packet.
            baselines[delta.EntityId] = new SynergyChannelManager.EntityBaseline
            {
                X = absX, Y = absY, Z = absZ,
                MotionX = absMX, MotionY = absMY, MotionZ = absMZ
            };

            // First-tick guard: when entity just spawned (tick=0), only set the tick counter
            // and store baseline without applying position. Matches vanilla's bulkPositions
            // first-packet behavior which prevents a redundant interpolation frame from spawn position.
            if (prevTick == 0)
            {
                entity.Attributes.SetInt("tick", delta.Tick);
                return;
            }

            // 1. Tick attributes (before position, matching vanilla order)
            entity.Attributes.SetInt("tickDiff", Math.Min(delta.Tick - prevTick, 5));
            entity.Attributes.SetInt("tick", delta.Tick);

            // 2. Position — inline deserialization: (double)v / 16384.0 and (float)v / 1024f
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

            // 3. Agent-specific fields
            if (entity is EntityAgent agent)
            {
                agent.BodyYawServer = (float)delta.BodyYaw / 1024f;
                agent.Controls.FromInt(delta.Controls & 0x210);
                if (agent.EntityId != (capi.World.Player?.Entity?.EntityId ?? -1))
                    agent.ServerControls.FromInt(delta.Controls);
            }

            // 4. Mount controls
            (entity.SidedProperties == null ? null :
                entity.GetInterface<IMountable>()?.ControllingControls)?.FromInt(delta.MountControls);

            // 5. Trigger interpolation chain
            entity.OnReceivedServerPos(teleport);

            // 6. Tags (AFTER OnReceivedServerPos, matching vanilla order)
            entity.Tags = new TagSetFast(Vector256.Create(
                (ulong)delta.TagsPart1, (ulong)delta.TagsPart2,
                (ulong)delta.TagsPart3, (ulong)delta.TagsPart4));
        }

        public bool ServerHasSynergy => serverHasSynergy;
    }
}
