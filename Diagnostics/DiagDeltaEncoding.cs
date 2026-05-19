using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagDeltaEncoding : IDiagModule
    {
        public string ShortName => "deltaencoding";
        public string DisplayName => "Entity Position Delta Encoding";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long deltaBatches;
        private static long vanillaBatches;
        private static long deltaBytes;
        private static long vanillaEquivBytes;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref deltaBatches, 0);
            Interlocked.Exchange(ref vanillaBatches, 0);
            Interlocked.Exchange(ref deltaBytes, 0);
            Interlocked.Exchange(ref vanillaEquivBytes, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long db = Volatile.Read(ref deltaBatches);
            long vb = Volatile.Read(ref vanillaBatches);
            long dBytes = Volatile.Read(ref deltaBytes);
            long vBytes = Volatile.Read(ref vanillaEquivBytes);
            long saved = db > 0 && vBytes > dBytes ? vBytes - dBytes : 0;
            int savePct = db > 0 && vBytes > 0 ? (int)(saved * 100 / vBytes) : 0;
            double savedKBs = elapsed > 0 ? saved / 1024.0 / elapsed : 0;

            DiagLog.Line(api, caller, $"deltaencoding: deltaBatches={db} vanillaBatches={vb} deltaBytes={dBytes / 1024}KB vanillaEquiv={vBytes / 1024}KB saved={savePct}% (~{savedKBs:F1}KB/s) elapsed={elapsed:F1}s");
        }

        public static void OnDeltaBatch(int bytes)
        {
            if (!enabled) return;
            Interlocked.Increment(ref deltaBatches);
            Interlocked.Add(ref deltaBytes, bytes);
        }

        public static void OnVanillaBatch(int bytes)
        {
            if (!enabled) return;
            Interlocked.Increment(ref vanillaBatches);
            Interlocked.Add(ref vanillaEquivBytes, bytes);
        }

        public static void OnVanillaEquivBytes(int bytes)
        {
            if (!enabled) return;
            Interlocked.Add(ref vanillaEquivBytes, bytes);
        }
    }
}
