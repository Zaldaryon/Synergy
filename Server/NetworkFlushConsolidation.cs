using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// P8: Batch small TCP packets into fewer network frames.
    /// Patches TcpNetConnection.Send to buffer small uncompressed packets,
    /// then calls vanilla Send for each during flush (preserving header format and async path).
    /// High-priority packets (compressed or large) flush immediately.
    /// Vanilla behavior preserved: same data sent, same header format, same async path.
    /// </summary>
    public static class NetworkFlushConsolidation
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private const int MtuThreshold = 1400;

        // Cached reflection
        private static MethodInfo vanillaSendMethod;
        private static FieldInfo connectedField;
        private static object enumOk;

        [ThreadStatic] private static bool flushing;

        private static long tickListenerId;

        private static readonly ConcurrentDictionary<object, BufferState> connectionBuffers = new();

        private class BufferState
        {
            public readonly List<byte[]> pendingPackets = new();
            public int totalBytes;
            public readonly object syncLock = new();
        }

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var tcpConnType = AccessTools.TypeByName("Vintagestory.Server.Network.TcpNetConnection");
            if (tcpConnType == null)
            {
                api.Logger.Warning("[Synergy] P8: Could not find TcpNetConnection, skipping");
                return;
            }

            vanillaSendMethod = AccessTools.Method(tcpConnType, "Send",
                new[] { typeof(byte[]), typeof(bool) });
            if (vanillaSendMethod == null)
            {
                api.Logger.Warning("[Synergy] P8: Could not find TcpNetConnection.Send, skipping");
                return;
            }

            connectedField = AccessTools.Field(tcpConnType, "Connected");

            // Resolve EnumSendResult.Ok — try both known namespaces
            var enumType = AccessTools.TypeByName("Vintagestory.Common.EnumSendResult")
                ?? AccessTools.TypeByName("Vintagestory.Server.Network.EnumSendResult");
            if (enumType != null)
                enumOk = Enum.Parse(enumType, "Ok");

            if (!ConflictDetector.IsSafeToPatch(vanillaSendMethod, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(vanillaSendMethod,
                prefix: new HarmonyMethod(typeof(NetworkFlushConsolidation), nameof(Prefix_Send)));

            tickListenerId = api.Event.RegisterGameTickListener(OnServerTick, 1);

            api.Logger.Notification("[Synergy] P8: Network flush consolidation active");
        }

        public static bool Prefix_Send(object __instance, byte[] data, bool compressedFlag, ref object __result)
        {
            if (disabled || flushing || data == null || data.Length == 0) return true;

            try
            {
                // Large or compressed packets: flush pending then let vanilla handle this one
                if (data.Length > MtuThreshold || compressedFlag)
                {
                    FlushConnection(__instance);
                    return true;
                }

                var state = connectionBuffers.GetOrAdd(__instance, _ => new BufferState());
                lock (state.syncLock)
                {
                    state.pendingPackets.Add(data);
                    state.totalBytes += data.Length;

                    if (state.totalBytes >= MtuThreshold)
                    {
                        FlushConnectionLocked(__instance, state);
                    }
                }

                if (enumOk != null)
                    __result = enumOk;
                return false;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] P8: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }

        private static void OnServerTick(float dt)
        {
            if (disabled) return;
            FlushAll();
        }

        public static void FlushAll()
        {
            foreach (var kvp in connectionBuffers)
            {
                FlushConnection(kvp.Key);
            }
        }

        private static void FlushConnection(object connection)
        {
            if (!connectionBuffers.TryGetValue(connection, out var state)) return;
            lock (state.syncLock)
            {
                FlushConnectionLocked(connection, state);
            }
        }

        private static void FlushConnectionLocked(object connection, BufferState state)
        {
            if (state.pendingPackets.Count == 0) return;

            try
            {
                // Check connection alive
                if (connectedField != null && !(bool)connectedField.GetValue(connection))
                {
                    state.pendingPackets.Clear();
                    state.totalBytes = 0;
                    connectionBuffers.TryRemove(connection, out _);
                    return;
                }

                // Use ThreadStatic re-entry guard so other threads aren't affected
                flushing = true;
                try
                {
                    foreach (var packet in state.pendingPackets)
                    {
                        vanillaSendMethod.Invoke(connection, new object[] { packet, false });
                    }
                }
                finally
                {
                    flushing = false;
                }
            }
            catch (Exception ex)
            {
                sapi?.Logger.Debug("[Synergy] P8: Flush error: {0}", ex.Message);
            }
            finally
            {
                state.pendingPackets.Clear();
                state.totalBytes = 0;
            }
        }

        public static void Cleanup()
        {
            FlushAll();
            connectionBuffers.Clear();
            if (sapi != null && tickListenerId != 0)
            {
                sapi.Event.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }
        }
    }
}
