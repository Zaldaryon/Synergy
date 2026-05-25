using System;
using System.Text;
using Synergy.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy
{
    public class SynergyCommand
    {
        private readonly ICoreServerAPI api;

        private static SynergyConfig Config => SynergyMod.Config;

        private static readonly (string key, string label, System.Func<SynergyConfig, bool> get, Action<SynergyConfig, bool> set)[] toggles =
        {
            ("activationrange",  "Entity Activation Range",     c => c.EntityActivationRangeEnabled,          (c, v) => c.EntityActivationRangeEnabled = v),
            ("collisionfastpath", "Collision Fast-Path",         c => c.CollisionFastPathEnabled,              (c, v) => c.CollisionFastPathEnabled = v),
            ("networkflush",     "Network Flush Consolidation",  c => c.NetworkFlushConsolidationEnabled,      (c, v) => c.NetworkFlushConsolidationEnabled = v),
            ("inventoryscan",    "Inventory Dirty Scan",         c => c.InventoryDirtyScanEnabled,             (c, v) => c.InventoryDirtyScanEnabled = v),
            ("pathfindingpool",  "Pathfinding Node Pooling",     c => c.PathfindingOptimizationsEnabled,       (c, v) => c.PathfindingOptimizationsEnabled = v),
            ("pathfindingthrottle", "Pathfinding Throttle",      c => c.PathfindingThrottleEnabled,            (c, v) => c.PathfindingThrottleEnabled = v),
            ("hysteresis",       "Tracking Hysteresis",          c => c.EntityTrackingHysteresisEnabled,       (c, v) => c.EntityTrackingHysteresisEnabled = v),
            ("sendfrequency",    "Distance Send Frequency",      c => c.DistanceBasedSendFrequencyEnabled,     (c, v) => c.DistanceBasedSendFrequencyEnabled = v),
            ("attributedelta",   "Attribute Sync Delta",         c => c.AttributeSyncResyncPreventionEnabled,  (c, v) => c.AttributeSyncResyncPreventionEnabled = v),
            ("spawnpriority",    "Spawn Priority Ordering",      c => c.EntitySpawnPriorityOrderingEnabled,    (c, v) => c.EntitySpawnPriorityOrderingEnabled = v),
            ("repulsethrottle",  "Repulse Agents Throttle",      c => c.RepulseAgentsThrottleEnabled,          (c, v) => c.RepulseAgentsThrottleEnabled = v),
            ("aithrottle",       "AI Brain Throttle",            c => c.AiBrainThrottleEnabled,                (c, v) => c.AiBrainThrottleEnabled = v),
            ("deltaencoding",    "Delta Position Encoding",      c => c.DeltaEncodingEnabled,                  (c, v) => c.DeltaEncodingEnabled = v),
            ("entityloadbudget", "Entity Load Budgeting",        c => c.EntityLoadBudgetingEnabled,            (c, v) => c.EntityLoadBudgetingEnabled = v),
            ("blocktickpool",    "Block Tick Pooling",            c => c.BlockTickPoolingEnabled,               (c, v) => c.BlockTickPoolingEnabled = v),
        };

        public SynergyCommand(ICoreServerAPI api, SynergyConfig config)
        {
            this.api = api;
        }

        public void Register()
        {
            var onOff = new[] { "on", "off" };

            var cmd = api.ChatCommands.Create("synergy")
                .WithDescription("Synergy performance optimization controls")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => ShowStatus());

            cmd.BeginSubCommand("all")
                .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                .HandleWith(args => ToggleAll(args))
            .EndSubCommand();

            cmd.BeginSubCommand("reload")
                .HandleWith(args => Reload())
            .EndSubCommand();

            cmd.BeginSubCommand("stats")
                .HandleWith(args => ShowStats())
            .EndSubCommand();

            // /synergy diag [module] [on|off|dump|reset]
            cmd.BeginSubCommand("diag")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("module"),
                          api.ChatCommands.Parsers.OptionalWord("action"))
                .HandleWith(args => HandleDiag(args))
            .EndSubCommand();

            foreach (var (key, label, get, set) in toggles)
            {
                var k = key;
                var s = set;
                cmd.BeginSubCommand(k)
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => Toggle(args, k, s))
                .EndSubCommand();
            }
        }

        private TextCommandResult ShowStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Synergy v{api.ModLoader.GetMod("synergy")?.Info?.Version ?? "?"} ===");

            sb.AppendLine("Optimizations:");
            foreach (var (key, label, get, _) in toggles)
            {
                sb.AppendLine($"  {key}: {(get(Config) ? "ON" : "OFF")}");
            }

            sb.AppendLine($"  activation-range: {Config.EntityActivationRangeBlocks} blocks");
            sb.AppendLine($"  gc-diagnostics: {(Config.GcDiagnosticsEnabled ? "ON" : "OFF")}");

            sb.AppendLine();
            sb.AppendLine(CircuitBreakerStatus.GetSummary());
            CircuitBreakerStatus.AppendDetails(sb);

            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult ToggleAll(TextCommandCallingArgs args)
        {
            string action = (string)args.Parsers[0].GetValue();
            bool enable = action == "on";

            foreach (var (_, _, _, set) in toggles)
                set(Config, enable);

            Config.Save(api);
            return TextCommandResult.Success(
                $"All optimizations set to {(enable ? "ON" : "OFF")}.\n⚠️ Server restart required.");
        }

        private TextCommandResult Toggle(TextCommandCallingArgs args, string key, Action<SynergyConfig, bool> set)
        {
            string action = (string)args.Parsers[0].GetValue();
            bool enable = action == "on";

            set(Config, enable);
            Config.Save(api);

            return TextCommandResult.Success(
                $"{key}: {(enable ? "ON" : "OFF")}.\n⚠️ Server restart required.");
        }

        private TextCommandResult Reload()
        {
            var fresh = SynergyConfig.Load(api);
            if (fresh != null)
            {
                SynergyMod.Config = fresh;
                return TextCommandResult.Success("Config reloaded from disk.");
            }
            return TextCommandResult.Error("Failed to reload config.");
        }

        private TextCommandResult ShowStats()
        {
            return TextCommandResult.Success(SynergyMetrics.GetStatsText());
        }

        private TextCommandResult HandleDiag(TextCommandCallingArgs args)
        {
            var caller = args.Caller.Player as IServerPlayer;
            string module = (string)args.Parsers[0].GetValue();
            string action = (string)args.Parsers[1].GetValue();

            // /synergy diag — list all modules
            if (string.IsNullOrEmpty(module))
            {
                DiagRegistry.ListModules(api, caller);
                return TextCommandResult.Success();
            }

            // /synergy diag all [on|off|dump|reset]
            if (module.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                switch (action?.ToLowerInvariant())
                {
                    case "on":
                        DiagRegistry.EnableAll();
                        return TextCommandResult.Success("All diagnostic modules enabled.");
                    case "off":
                        DiagRegistry.DisableAll();
                        return TextCommandResult.Success("All diagnostic modules disabled.");
                    case "reset":
                        DiagRegistry.ResetAll();
                        return TextCommandResult.Success("All diagnostic modules reset.");
                    case "dump":
                    default:
                        DiagRegistry.DumpAll(api, caller);
                        return TextCommandResult.Success();
                }
            }

            // /synergy diag <module> [on|off|dump|reset]
            var mod = DiagRegistry.Get(module);
            if (mod == null)
                return TextCommandResult.Error($"Unknown diagnostic module: {module}");

            switch (action?.ToLowerInvariant())
            {
                case "on":
                    mod.Enable();
                    return TextCommandResult.Success($"Diagnostic module '{mod.ShortName}' enabled.");
                case "off":
                    mod.Disable();
                    return TextCommandResult.Success($"Diagnostic module '{mod.ShortName}' disabled.");
                case "reset":
                    mod.Reset();
                    return TextCommandResult.Success($"Diagnostic module '{mod.ShortName}' reset.");
                case "dump":
                default:
                    DiagLog.Header(api, caller, mod.ShortName);
                    mod.Dump(api, caller);
                    DiagLog.Footer(api, caller);
                    return TextCommandResult.Success();
            }
        }
    }
}
