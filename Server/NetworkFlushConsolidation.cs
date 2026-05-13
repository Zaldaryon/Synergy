using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Batch small TCP packets into a single SendAsync call per connection per tick.
    /// Each buffered packet is written as [4-byte header][payload] into a consolidated buffer.
    /// On flush (end-of-tick or buffer >= MTU), one SendAsync emits all accumulated bytes
    /// as a single TCP segment — reducing per-packet syscall overhead and TCP segment count.
    ///
    /// Large or compressed packets flush the buffer first (preserving order) then go direct.
    /// </summary>
    public static class NetworkFlushConsolidation
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private const int MtuThreshold = 1400;

        private static object enumOk;
        private static AccessTools.FieldRef<object, bool> connectedRef;
        private static AccessTools.FieldRef<object, Socket> tcpSocketRef;
        private static AccessTools.FieldRef<object, CancellationTokenSource> ctsRef;

        [ThreadStatic] private static bool flushing;

        private static readonly ConcurrentDictionary<object, BufferState> connectionBuffers = new();

        private sealed class BufferState
        {
            public byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            public int writePos;
            public readonly object syncLock = new();

            public void EnsureCapacity(int additionalBytes)
            {
                int need = writePos + additionalBytes;
                if (need <= buffer.Length) return;
                int newSize = buffer.Length;
                while (newSize < need) newSize *= 2;
                var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
                Buffer.BlockCopy(buffer, 0, newBuf, 0, writePos);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = newBuf;
            }

            public void Reset()
            {
                writePos = 0;
            }
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

            var vanillaSendMethod = AccessTools.Method(tcpConnType, "Send",
                new[] { typeof(byte[]), typeof(bool) });
            if (vanillaSendMethod == null)
            {
                api.Logger.Warning("[Synergy] NetworkFlush: Could not find TcpNetConnection.Send, skipping");
                return;
            }

            connectedRef = AccessTools.FieldRefAccess<bool>(tcpConnType, "Connected");
            tcpSocketRef = AccessTools.FieldRefAccess<Socket>(tcpConnType, "TcpSocket");
            ctsRef = AccessTools.FieldRefAccess<CancellationTokenSource>(tcpConnType, "cts");

            if (tcpSocketRef == null || ctsRef == null)
            {
                api.Logger.Warning("[Synergy] NetworkFlush: Could not find Socket/CTS fields, skipping");
                return;
            }

            var enumType = AccessTools.TypeByName("Vintagestory.Common.EnumSendResult")
                ?? AccessTools.TypeByName("Vintagestory.Server.Network.EnumSendResult");
            if (enumType != null)
                enumOk = Enum.Parse(enumType, "Ok");

            if (!ConflictDetector.IsSafeToPatch(vanillaSendMethod, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(vanillaSendMethod,
                prefix: new HarmonyMethod(typeof(NetworkFlushConsolidation), nameof(Prefix_Send)));

            var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
            var processMethod = serverMainType != null
                ? AccessTools.Method(serverMainType, "Process", Type.EmptyTypes)
                : null;

            if (processMethod != null &&
                ConflictDetector.IsSafeToPatch(processMethod, SynergyMod.HarmonyId, api.Logger))
            {
                harmony.Patch(processMethod,
                    postfix: new HarmonyMethod(typeof(NetworkFlushConsolidation), nameof(Postfix_Process)));
                api.Logger.Notification("[Synergy] NetworkFlush: Network flush consolidation active (true consolidation)");
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
                // Large or compressed packets: flush buffer first (preserve order), then let vanilla send
                if (data.Length > MtuThreshold || compressedFlag)
                {
                    FlushConnection(__instance);
                    return true;
                }

                var state = connectionBuffers.GetOrAdd(__instance, _ => new BufferState());
                lock (state.syncLock)
                {
                    int packetSize = 4 + data.Length; // 4-byte header + payload
                    state.EnsureCapacity(packetSize);

                    // Write vanilla header: length | (compressed ? 1<<31 : 0)
                    int header = data.Length; // uncompressed, top bit = 0
                    state.buffer[state.writePos++] = (byte)(header >> 24);
                    state.buffer[state.writePos++] = (byte)(header >> 16);
                    state.buffer[state.writePos++] = (byte)(header >> 8);
                    state.buffer[state.writePos++] = (byte)header;
                    Buffer.BlockCopy(data, 0, state.buffer, state.writePos, data.Length);
                    state.writePos += data.Length;

                    if (state.writePos >= MtuThreshold)
                        FlushConnectionLocked(__instance, state);
                }

                if (enumOk != null) __result = enumOk;
                return false;
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errorCount) >= 5)
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

            // Periodic cleanup of disconnected connections (every 256 ticks)
            if ((++flushTickCounter & 0xFF) == 0)
                PurgeDisconnected();
        }

        private static int flushTickCounter;

        private static void PurgeDisconnected()
        {
            foreach (var kvp in connectionBuffers)
            {
                if (connectedRef != null && !connectedRef(kvp.Key))
                {
                    if (connectionBuffers.TryRemove(kvp.Key, out var state))
                    {
                        lock (state.syncLock)
                        {
                            ArrayPool<byte>.Shared.Return(state.buffer);
                        }
                    }
                }
            }
        }

        public static void FlushAll()
        {
            foreach (var kvp in connectionBuffers)
                FlushConnection(kvp.Key);
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
            if (state.writePos == 0) return;

            if (connectedRef != null && !connectedRef(connection))
            {
                state.Reset();
                connectionBuffers.TryRemove(connection, out _);
                return;
            }

            var socket = tcpSocketRef(connection);
            var cts = ctsRef(connection);
            if (socket == null || cts == null)
            {
                state.Reset();
                return;
            }

            // Copy to a right-sized array — SendAsync is fire-and-forget,
            // we cannot reuse the pooled buffer until the syscall completes.
            var payload = new byte[state.writePos];
            Buffer.BlockCopy(state.buffer, 0, payload, 0, state.writePos);
            state.Reset();

            try
            {
                flushing = true;
                socket.SendAsync(payload, SocketFlags.None, cts.Token);
            }
            catch (Exception ex)
            {
                sapi?.Logger.Debug("[Synergy] NetworkFlush: Flush error: {0}", ex.Message);
            }
            finally
            {
                flushing = false;
            }
        }

        public static void Cleanup()
        {
            FlushAll();
            foreach (var kvp in connectionBuffers)
            {
                lock (kvp.Value.syncLock)
                {
                    ArrayPool<byte>.Shared.Return(kvp.Value.buffer);
                }
            }
            connectionBuffers.Clear();
        }
    }
}
