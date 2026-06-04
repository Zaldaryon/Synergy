using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagTrackingHysteresis : IDiagModule
    {
        public string ShortName => "hysteresis";
        public string DisplayName => "Entity Tracking Hysteresis";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long entitiesKept;
        private static long entitiesDespawned;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref entitiesKept, 0);
            Interlocked.Exchange(ref entitiesDespawned, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long kept = Volatile.Read(ref entitiesKept);
            long desp = Volatile.Read(ref entitiesDespawned);
            long total = kept + desp;
            int keptPct = total > 0 ? (int)(kept * 100 / total) : 0;

            DiagLog.Line(api, caller, $"hysteresis: kept={kept} despawned={desp} ({keptPct}% prevented flickering) elapsed={elapsed:F1}s");
        }

        public static void OnKept()
        {
            if (!enabled) return;
            Interlocked.Increment(ref entitiesKept);
        }

        public static void OnDespawned()
        {
            if (!enabled) return;
            Interlocked.Increment(ref entitiesDespawned);
        }
    }
}
