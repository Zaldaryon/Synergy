using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagInventoryDirtyScan : IDiagModule
    {
        public string ShortName => "inventoryscan";
        public string DisplayName => "Inventory Dirty Scan";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long skipped;
        private static long ranVanilla;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref skipped, 0);
            Interlocked.Exchange(ref ranVanilla, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long s = Volatile.Read(ref skipped);
            long v = Volatile.Read(ref ranVanilla);
            long total = s + v;
            int skipPct = total > 0 ? (int)(s * 100 / total) : 0;
            double rate = elapsed > 0 ? total / elapsed : 0;

            DiagLog.Line(api, caller, $"inventoryscan: skipped={s} scanned={v} ({skipPct}% skipped) rate={rate:F0}/s elapsed={elapsed:F1}s");
        }

        public static void OnSkipped()
        {
            if (!enabled) return;
            Interlocked.Increment(ref skipped);
        }

        public static void OnScanned()
        {
            if (!enabled) return;
            Interlocked.Increment(ref ranVanilla);
        }
    }
}
