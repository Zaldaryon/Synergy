using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagBlockTickPooling : IDiagModule
    {
        public string ShortName => "blocktickpool";
        public string DisplayName => "Block Tick Pooling";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long pooled;
        private static long fallbacks;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref pooled, 0);
            Interlocked.Exchange(ref fallbacks, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long p = Volatile.Read(ref pooled);
            long f = Volatile.Read(ref fallbacks);
            long total = p + f;
            int reusePct = total > 0 ? (int)(p * 100 / total) : 0;
            double rate = elapsed > 0 ? total / elapsed : 0;
            double gcSavedMB = p * 48.0 / 1048576.0;

            DiagLog.Line(api, caller, $"blocktickpool: pooled={p} fallbacks={f} reuseRate={reusePct}% rate={rate:F0}/s gcSaved~{gcSavedMB:F2}MB elapsed={elapsed:F1}s");
        }

        public static void OnPooled()
        {
            if (!enabled) return;
            Interlocked.Increment(ref pooled);
        }

        public static void OnFallback()
        {
            if (!enabled) return;
            Interlocked.Increment(ref fallbacks);
        }
    }
}
