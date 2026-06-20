using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagAiBrainThrottle : IDiagModule
    {
        public string ShortName => "aithrottle";
        public string DisplayName => "AI Brain Tick Throttle";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long fullTicks;
        private static long throttledTicks;
        private static long combatBypasses;
        private static long feedBypasses;
        private static long safetyBypasses;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref fullTicks, 0);
            Interlocked.Exchange(ref throttledTicks, 0);
            Interlocked.Exchange(ref combatBypasses, 0);
            Interlocked.Exchange(ref feedBypasses, 0);
            Interlocked.Exchange(ref safetyBypasses, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long full = Volatile.Read(ref fullTicks);
            long throttled = Volatile.Read(ref throttledTicks);
            long combat = Volatile.Read(ref combatBypasses);
            long feed = Volatile.Read(ref feedBypasses);
            long safety = Volatile.Read(ref safetyBypasses);
            long total = full + throttled;
            int throttlePct = total > 0 ? (int)(throttled * 100 / total) : 0;

            DiagLog.Line(api, caller, $"aithrottle: full={full} throttled={throttled} ({throttlePct}% saved) combatBypass={combat} feedBypass={feed} safetyBypass={safety} elapsed={elapsed:F1}s");
        }

        public static void OnFullTick()
        {
            if (!enabled) return;
            Interlocked.Increment(ref fullTicks);
        }

        public static void OnThrottled()
        {
            if (!enabled) return;
            Interlocked.Increment(ref throttledTicks);
        }

        public static void OnCombatBypass()
        {
            if (!enabled) return;
            Interlocked.Increment(ref combatBypasses);
        }

        public static void OnFeedBypass()
        {
            if (!enabled) return;
            Interlocked.Increment(ref feedBypasses);
        }

        public static void OnSafetyBypass()
        {
            if (!enabled) return;
            Interlocked.Increment(ref safetyBypasses);
        }
    }
}
