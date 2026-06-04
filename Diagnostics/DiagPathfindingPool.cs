using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagPathfindingPool : IDiagModule
    {
        public string ShortName => "pathfindingpool";
        public string DisplayName => "Pathfinding Node Pooling";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long pooled;
        private static long fallbacks;
        private static long searches;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref pooled, 0);
            Interlocked.Exchange(ref fallbacks, 0);
            Interlocked.Exchange(ref searches, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long p = Volatile.Read(ref pooled);
            long f = Volatile.Read(ref fallbacks);
            long s = Volatile.Read(ref searches);
            long total = p + f;
            int reusePct = total > 0 ? (int)(p * 100 / total) : 0;
            double avgNodesPerSearch = s > 0 ? (double)total / s : 0;
            double gcSavedMB = p * 64.0 / 1048576.0;

            DiagLog.Line(api, caller, $"pathfindingpool: pooled={p} fallbacks={f} reuseRate={reusePct}% searches={s} avgNodes/search={avgNodesPerSearch:F0} gcSaved~{gcSavedMB:F2}MB elapsed={elapsed:F1}s");
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

        public static void OnSearchStart()
        {
            if (!enabled) return;
            Interlocked.Increment(ref searches);
        }
    }
}
