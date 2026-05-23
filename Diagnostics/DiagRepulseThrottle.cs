using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagRepulseThrottle : IDiagModule
    {
        public string ShortName => "repulsethrottle";
        public string DisplayName => "Repulse Agents Throttle";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long ticked;
        private static long skipped;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref ticked, 0);
            Interlocked.Exchange(ref skipped, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long t = Volatile.Read(ref ticked);
            long s = Volatile.Read(ref skipped);
            long total = t + s;
            int skipPct = total > 0 ? (int)(s * 100 / total) : 0;

            DiagLog.Line(api, caller, $"repulsethrottle: ticked={t} skipped={s} ({skipPct}% throttled) elapsed={elapsed:F1}s");
        }

        public static void OnTicked()
        {
            if (!enabled) return;
            Interlocked.Increment(ref ticked);
        }

        public static void OnSkipped()
        {
            if (!enabled) return;
            Interlocked.Increment(ref skipped);
        }
    }
}
