using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagPathfindingThrottle : IDiagModule
    {
        public string ShortName => "pathfindingthrottle";
        public string DisplayName => "Pathfinding Distance Throttle";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long accepted;
        private static long throttled;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref accepted, 0);
            Interlocked.Exchange(ref throttled, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long a = Volatile.Read(ref accepted);
            long t = Volatile.Read(ref throttled);
            long total = a + t;
            int throttlePct = total > 0 ? (int)(t * 100 / total) : 0;
            double rate = elapsed > 0 ? total / elapsed : 0;

            DiagLog.Line(api, caller, $"pathfindingthrottle: accepted={a} throttled={t} ({throttlePct}% skipped) rate={rate:F0}/s elapsed={elapsed:F1}s");
        }

        public static void OnAccepted()
        {
            if (!enabled) return;
            Interlocked.Increment(ref accepted);
        }

        public static void OnThrottled()
        {
            if (!enabled) return;
            Interlocked.Increment(ref throttled);
        }
    }
}
