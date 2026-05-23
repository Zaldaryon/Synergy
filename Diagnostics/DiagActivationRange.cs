using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagActivationRange : IDiagModule
    {
        public string ShortName => "activationrange";
        public string DisplayName => "Entity Activation Range";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long entitiesFullTicked;
        private static long entitiesSleeping;
        private static long whitelistedTicks;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref entitiesFullTicked, 0);
            Interlocked.Exchange(ref entitiesSleeping, 0);
            Interlocked.Exchange(ref whitelistedTicks, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long full = Volatile.Read(ref entitiesFullTicked);
            long sleep = Volatile.Read(ref entitiesSleeping);
            long wl = Volatile.Read(ref whitelistedTicks);
            long total = full + sleep;
            int sleepPct = total > 0 ? (int)(sleep * 100 / total) : 0;

            DiagLog.Line(api, caller, $"activationrange: fullTick={full} sleeping={sleep} ({sleepPct}%) whitelistedBehaviorTicks={wl} elapsed={elapsed:F1}s");
        }

        public static void OnFullTick()
        {
            if (!enabled) return;
            Interlocked.Increment(ref entitiesFullTicked);
        }

        public static void OnSleeping()
        {
            if (!enabled) return;
            Interlocked.Increment(ref entitiesSleeping);
        }

        public static void OnWhitelistedTick()
        {
            if (!enabled) return;
            Interlocked.Increment(ref whitelistedTicks);
        }
    }
}
