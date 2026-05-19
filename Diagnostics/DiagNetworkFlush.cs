using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagNetworkFlush : IDiagModule
    {
        public string ShortName => "networkflush";
        public string DisplayName => "Network Flush Consolidation";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long packetsBuffered;
        private static long packetsPassedThrough;
        private static long flushes;
        private static long bytesBuffered;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref packetsBuffered, 0);
            Interlocked.Exchange(ref packetsPassedThrough, 0);
            Interlocked.Exchange(ref flushes, 0);
            Interlocked.Exchange(ref bytesBuffered, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long buf = Volatile.Read(ref packetsBuffered);
            long pass = Volatile.Read(ref packetsPassedThrough);
            long fl = Volatile.Read(ref flushes);
            long bytes = Volatile.Read(ref bytesBuffered);
            long total = buf + pass;
            int bufPct = total > 0 ? (int)(buf * 100 / total) : 0;
            double avgPerFlush = fl > 0 ? (double)buf / fl : 0;

            DiagLog.Line(api, caller, $"networkflush: buffered={buf} passthrough={pass} ({bufPct}% consolidated) flushes={fl} avgPkts/flush={avgPerFlush:F1} bytesBuffered={bytes / 1024}KB elapsed={elapsed:F1}s");
        }

        public static void OnBuffered(int dataLen)
        {
            if (!enabled) return;
            Interlocked.Increment(ref packetsBuffered);
            Interlocked.Add(ref bytesBuffered, dataLen);
        }

        public static void OnPassthrough()
        {
            if (!enabled) return;
            Interlocked.Increment(ref packetsPassedThrough);
        }

        public static void OnFlush()
        {
            if (!enabled) return;
            Interlocked.Increment(ref flushes);
        }
    }
}
