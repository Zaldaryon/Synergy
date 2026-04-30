using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Batch small TCP packets into fewer network frames.
    /// Patches TcpNetConnection.Send to buffer small uncompressed packets,
    /// then flushes all buffers at end-of-tick via a postfix on ServerMain.Process().
    /// High-priority packets (compressed or large) flush immediately.
    /// Vanilla behavior preserved: same data sent, same header format, same async path.
    ///
    /// Design follows the industry-standard flush-at-end-of-tick pattern used by:
    /// - Netty FlushConsolidationHandler (buffer during read, flush at channelReadComplete)
    /// - Minecraft Paper (write during tick, single flush at end)
    /// - Source Engine (collect state changes, send snapshot at end of tick)
    /// - Gaffer on Games (send after simulation step completes)
    ///
    /// The VS server tick loop runs: Systems.OnServerTick → EventManager.TriggerGameTick → ProcessMain.
    /// ProcessMain handles client packets (inventory moves, slot activations) which generate response
    /// packets. A postfix on Process() ensures these are flushed in the same tick, not the next one.
    /// </summary>
    public static class NetworkFlushConsolidation
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private const int MtuThreshold = 1400;

        // Cached reflection
        private static object enumOk;

        // IL-emitted delegate via DynamicMethod (skipVisibility:true) — bypasses
        // Delegate.CreateDelegate limitation with internal types. Near-native call speed.
        private delegate void FastSendDelegate(object instance, byte[] data, bool compressed);
        private static FastSendDelegate vanillaSendFast;
        private static AccessTools.FieldRef<object, bool> connectedRef;

        [ThreadStatic] private static bool flushing;

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
                api.Logger.Warning("[Synergy] NetworkFlush: Could not find TcpNetConnection, skipping");
                return;
            }

            // IL-emitted delegate via DynamicMethod — bypasses Delegate.CreateDelegate
            // limitation with internal return types (EnumSendResult). skipVisibility:true
            // allows calling methods on internal types from another assembly.
            var vanillaSendMethod = AccessTools.Method(tcpConnType, "Send",
                new[] { typeof(byte[]), typeof(bool) });
            if (vanillaSendMethod == null)
            {
                api.Logger.Warning("[Synergy] NetworkFlush: Could not find TcpNetConnection.Send, skipping");
                return;
            }

            var dm = new System.Reflection.Emit.DynamicMethod(
                "Synergy_FastSend", typeof(void),
                new[] { typeof(object), typeof(byte[]), typeof(bool) },
                typeof(NetworkFlushConsolidation), skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, tcpConnType);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
            il.Emit(System.Reflection.Emit.OpCodes.Callvirt, vanillaSendMethod);
            il.Emit(System.Reflection.Emit.OpCodes.Pop); // discard EnumSendResult
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            vanillaSendFast = (FastSendDelegate)dm.CreateDelegate(typeof(FastSendDelegate));

            connectedRef = AccessTools.FieldRefAccess<bool>(tcpConnType, "Connected");

            var enumType = AccessTools.TypeByName("Vintagestory.Common.EnumSendResult")
                ?? AccessTools.TypeByName("Vintagestory.Server.Network.EnumSendResult");
            if (enumType != null)
                enumOk = Enum.Parse(enumType, "Ok");

            if (!ConflictDetector.IsSafeToPatch(vanillaSendMethod, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(vanillaSendMethod,
                prefix: new HarmonyMethod(typeof(NetworkFlushConsolidation), nameof(Prefix_Send)));

            // Flush at end-of-tick via postfix on ServerMain.Process()
            // This ensures packets from ProcessMain (inventory interactions) are flushed
            // in the same tick they're generated, not delayed to the next tick.
            var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
            var processMethod = serverMainType != null
                ? AccessTools.Method(serverMainType, "Process", Type.EmptyTypes)
                : null;

            if (processMethod != null &&
                ConflictDetector.IsSafeToPatch(processMethod, SynergyMod.HarmonyId, api.Logger))
            {
                harmony.Patch(processMethod,
                    postfix: new HarmonyMethod(typeof(NetworkFlushConsolidation), nameof(Postfix_Process)));
                api.Logger.Notification("[Synergy] NetworkFlush: Network flush consolidation active (end-of-tick flush)");
            }
            else
            {
                api.Logger.Warning("[Synergy] NetworkFlush: Could not patch ServerMain.Process, skipping");
            }
        }

        public static bool Prefix_Send(object __instance, byte[] data, bool compressedFlag, ref object __result)
        {
            if (disabled || flushing || data == null || data.Length == 0) return true;

            try
            {
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
                    sapi?.Logger.Warning("[Synergy] NetworkFlush: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }

        public static void Postfix_Process()
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
                if (connectedRef != null && !connectedRef(connection))
                {
                    state.pendingPackets.Clear();
                    state.totalBytes = 0;
                    connectionBuffers.TryRemove(connection, out _);
                    return;
                }

                flushing = true;
                try
                {
                    foreach (var packet in state.pendingPackets)
                    {
                        vanillaSendFast(connection, packet, false);
                    }
                }
                finally
                {
                    flushing = false;
                }
            }
            catch (Exception ex)
            {
                sapi?.Logger.Debug("[Synergy] NetworkFlush: Flush error: {0}", ex.Message);
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
        }
    }
}
