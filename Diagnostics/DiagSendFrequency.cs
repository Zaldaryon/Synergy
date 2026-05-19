using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagSendFrequency : IDiagModule
    {
        public string ShortName => "sendfrequency";
        public string DisplayName => "Distance-Based Send Frequency";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long sent;
        private static long suppressed;
        private static long stationarySuppressed;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref sent, 0);
            Interlocked.Exchange(ref suppressed, 0);
            Interlocked.Exchange(ref stationarySuppressed, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long s = Volatile.Read(ref sent);
            long sup = Volatile.Read(ref suppressed);
            long stat = Volatile.Read(ref stationarySuppressed);
            long total = s + sup + stat;
            int savePct = total > 0 ? (int)((sup + stat) * 100 / total) : 0;

            DiagLog.Line(api, caller, $"sendfrequency: sent={s} distThrottled={sup} stationarySuppressed={stat} ({savePct}% bandwidth saved) elapsed={elapsed:F1}s");
        }

        public static void OnSent()
        {
            if (!enabled) return;
            Interlocked.Increment(ref sent);
        }

        public static void OnDistanceThrottled()
        {
            if (!enabled) return;
            Interlocked.Increment(ref suppressed);
        }

        public static void OnStationarySuppressed()
        {
            if (!enabled) return;
            Interlocked.Increment(ref stationarySuppressed);
        }
    }
}
