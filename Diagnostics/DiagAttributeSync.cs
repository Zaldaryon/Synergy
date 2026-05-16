using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagAttributeSync : IDiagModule
    {
        public string ShortName => "attributesync";
        public string DisplayName => "Attribute Sync Delta Updates";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long deltaUpdates;
        private static long bytesSaved;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref deltaUpdates, 0);
            Interlocked.Exchange(ref bytesSaved, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long d = Volatile.Read(ref deltaUpdates);
            long b = Volatile.Read(ref bytesSaved);
            double rate = elapsed > 0 ? d / elapsed : 0;

            DiagLog.Line(api, caller, $"attributesync: deltaUpdates={d} bytesSaved~{b / 1024}KB rate={rate:F1}/s elapsed={elapsed:F1}s");
        }

        public static void OnDeltaUpdate(int estimatedBytesSaved)
        {
            if (!enabled) return;
            Interlocked.Increment(ref deltaUpdates);
            Interlocked.Add(ref bytesSaved, estimatedBytesSaved);
        }
    }
}
