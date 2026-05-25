using System;
using System.Runtime;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagGc : IDiagModule
    {
        public string ShortName => "gc";
        public string DisplayName => "GC Diagnostics";
        public bool Enabled => enabled;

        private static volatile bool enabled;

        public void Enable() { enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            try
            {
                var info = GC.GetGCMemoryInfo();
                DiagLog.Line(api, caller, $"gc: serverGC={GCSettings.IsServerGC} latency={GCSettings.LatencyMode} heap={info.HeapSizeBytes / 1048576.0:F0}MB committed={info.TotalCommittedBytes / 1048576.0:F0}MB fragmented={info.FragmentedBytes / 1048576.0:F0}MB");
                DiagLog.Line(api, caller, $"gc: gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} totalMem={GC.GetTotalMemory(false) / 1048576.0:F0}MB");
            }
            catch (Exception ex)
            {
                DiagLog.Line(api, caller, $"gc: unavailable ({ex.Message})");
            }
        }
    }
}
