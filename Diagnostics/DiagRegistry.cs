using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public static class DiagRegistry
    {
        private static readonly Dictionary<string, IDiagModule> modules = new(StringComparer.OrdinalIgnoreCase);
        private static Timer autoDumpTimer;
        private static ICoreServerAPI sapi;
        private const int AutoDumpIntervalMs = 300_000; // 5 minutes

        public static IEnumerable<IDiagModule> All => modules.Values;

        public static void Initialize(ICoreServerAPI api)
        {
            sapi = api;
        }

        public static void Register(IDiagModule module)
        {
            modules[module.ShortName] = module;
        }

        public static IDiagModule Get(string shortName)
        {
            return modules.TryGetValue(shortName, out var m) ? m : null;
        }

        public static void EnableAll()
        {
            foreach (var m in modules.Values) m.Enable();
            StartAutoDump();
        }

        public static void DisableAll()
        {
            foreach (var m in modules.Values) m.Disable();
            StopAutoDump();
        }

        public static void ResetAll()
        {
            foreach (var m in modules.Values) m.Reset();
        }

        public static void DumpAll(ICoreServerAPI api, IServerPlayer caller)
        {
            DiagLog.Header(api, caller, "all modules");
            foreach (var m in modules.Values)
            {
                if (m.Enabled)
                    m.Dump(api, caller);
                else
                    DiagLog.Line(api, caller, $"{m.ShortName}: (disabled)");
            }
            DiagLog.Footer(api, caller);
        }

        public static void ListModules(ICoreServerAPI api, IServerPlayer caller)
        {
            DiagLog.Header(api, caller, "modules");
            foreach (var m in modules.Values)
            {
                DiagLog.Line(api, caller, $"{(m.Enabled ? "*" : " ")} {m.ShortName} — {m.DisplayName}");
            }
            DiagLog.Footer(api, caller);
        }

        public static void Clear()
        {
            StopAutoDump();
            modules.Clear();
        }

        private static void StartAutoDump()
        {
            if (autoDumpTimer != null || sapi == null) return;
            autoDumpTimer = new Timer(_ =>
            {
                try
                {
                    if (sapi == null) return;
                    sapi.Logger.Notification("[Synergy] === Auto-dump (periodic) ===");
                    foreach (var m in modules.Values)
                    {
                        if (m.Enabled)
                            m.Dump(sapi, null); // null caller = log-only
                    }
                }
                catch { /* swallow — timer must not crash server */ }
            }, null, AutoDumpIntervalMs, AutoDumpIntervalMs);
        }

        private static void StopAutoDump()
        {
            autoDumpTimer?.Dispose();
            autoDumpTimer = null;
        }
    }
}
