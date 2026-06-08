using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using HarmonyLib;
using Synergy.Diagnostics;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Batch small TCP packets into a single SendAsync call per connection per tick.
    ///
    /// In 1.22, the hot path is HiPerformanceSend → PreparePacketForSending → SendPreparedBytes.
    /// The legacy Send(byte[], bool) is rarely called. We patch SendPreparedBytes which receives
    /// a pre-formatted buffer (4 bytes header space + payload) and writes the length header in-place.
    ///
    /// Consolidation: buffer multiple SendPreparedBytes calls into one TCP segment, flush at
    /// end-of-tick or when buffer exceeds MTU.
    /// </summary>
    public static class NetworkFlushConsolidation
    {
        private static ICoreServerAPI sapi;
        internal static int errorCount;
        internal static bool disabled;
        private const int MtuThreshold = 1400;

        private static AccessTools.FieldRef<object, bool> connectedRef;
        private static AccessTools.FieldRef<object, Socket> tcpSocketRef;
        private static AccessTools.FieldRef<object, CancellationTokenSource> ctsRef;
        private static object enumOk;

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

            // Patch SendPreparedBytes — this is the actual hot path in 1.22+
            var sendPreparedBytes = AccessTools.Method(tcpConnType, "SendPreparedBytes",
                new[] { typeof(byte[]), typeof(int), typeof(bool) });
            if (sendPreparedBytes == null)
            {
                api.Logger.Warning("[Synergy] NetworkFlush: Could not find SendPreparedBytes, skipping");
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

            var enumType = AccessTools.TypeByName("Vintagestory.Common.EnumSendResult");
            if (enumType != null)
                enumOk = Enum.Parse(enumType, "Ok");

            if (!ConflictDetector.IsSafeToPatch(sendPreparedBytes, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(sendPreparedBytes,
                prefix: new HarmonyMethod(typeof(NetworkFlushConsolidation), nameof(Prefix_SendPreparedBytes)));

            // Also patch ServerMain.Process for end-of-tick flush
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

        /// <summary>
        /// Prefix on SendPreparedBytes(byte[] dataWithLength, int length, bool compressedFlag).
        /// dataWithLength has 4 bytes header space at start, followed by payload.
        /// Total send size = length + 4 bytes.
        /// </summary>
        public static bool Prefix_SendPreparedBytes(object __instance,
            byte[] dataWithLength, int length, bool compressedFlag,
            ref object __result)
        {
            if (disabled || flushing || dataWithLength == null || length <= 0) return true;

            try
            {
                int totalSize = length + 4;

                // Large or compressed packets: flush buffer first (preserve order), then let vanilla send
                if (totalSize > MtuThreshold || compressedFlag)
                {
                    FlushConnection(__instance);
                    DiagNetworkFlush.OnPassthrough();
                    return true;
                }

                // Write the header that vanilla would write
                int header = length | (compressedFlag ? (1 << 31) : 0);
                dataWithLength[0] = (byte)(header >> 24);
                dataWithLength[1] = (byte)(header >> 16);
                dataWithLength[2] = (byte)(header >> 8);
                dataWithLength[3] = (byte)header;

                var state = connectionBuffers.GetOrAdd(__instance, _ => new BufferState());
                lock (state.syncLock)
                {
                    state.EnsureCapacity(totalSize);
                    Buffer.BlockCopy(dataWithLength, 0, state.buffer, state.writePos, totalSize);
                    state.writePos += totalSize;

                    if (state.writePos >= MtuThreshold)
                        FlushConnectionLocked(__instance, state);
                }

                // Return EnumSendResult.Ok
                if (enumOk != null) __result = enumOk;
                DiagNetworkFlush.OnBuffered(totalSize);
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

            var payload = new byte[state.writePos];
            Buffer.BlockCopy(state.buffer, 0, payload, 0, state.writePos);
            state.Reset();

            DiagNetworkFlush.OnFlush();

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
