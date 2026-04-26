using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy
{
    public class SynergyCommand
    {
        private readonly ICoreServerAPI api;
        private readonly SynergyConfig config;

        private static readonly (string key, string label, System.Func<SynergyConfig, bool> get, System.Action<SynergyConfig, bool> set)[] toggles =
        {
            ("activationrange",  "Entity Activation Range",     c => c.EntityActivationRangeEnabled,          (c, v) => c.EntityActivationRangeEnabled = v),
            ("collisionfastpath", "Collision Fast-Path",         c => c.CollisionFastPathEnabled,              (c, v) => c.CollisionFastPathEnabled = v),
            ("networkflush",     "Network Flush Consolidation",  c => c.NetworkFlushConsolidationEnabled,      (c, v) => c.NetworkFlushConsolidationEnabled = v),
            ("blocktickpool",    "Block Tick Pooling",           c => c.BlockTickPoolingEnabled,               (c, v) => c.BlockTickPoolingEnabled = v),
            ("inventoryscan",    "Inventory Dirty Scan",         c => c.InventoryDirtyScanEnabled,             (c, v) => c.InventoryDirtyScanEnabled = v),
            ("pathfindingpool",  "Pathfinding Node Pooling",     c => c.PathfindingOptimizationsEnabled,       (c, v) => c.PathfindingOptimizationsEnabled = v),
            ("pathfindingthrottle", "Pathfinding Throttle",      c => c.PathfindingThrottleEnabled,            (c, v) => c.PathfindingThrottleEnabled = v),
            ("hysteresis",       "Tracking Hysteresis",          c => c.EntityTrackingHysteresisEnabled,       (c, v) => c.EntityTrackingHysteresisEnabled = v),
            ("sendfrequency",    "Distance Send Frequency",      c => c.DistanceBasedSendFrequencyEnabled,     (c, v) => c.DistanceBasedSendFrequencyEnabled = v),
            ("attributedelta",   "Attribute Sync Delta",         c => c.AttributeSyncResyncPreventionEnabled,  (c, v) => c.AttributeSyncResyncPreventionEnabled = v),
            ("spawnpriority",    "Spawn Priority Ordering",      c => c.EntitySpawnPriorityOrderingEnabled,    (c, v) => c.EntitySpawnPriorityOrderingEnabled = v),
            ("repulsethrottle",  "Repulse Agents Throttle",      c => c.RepulseAgentsThrottleEnabled,          (c, v) => c.RepulseAgentsThrottleEnabled = v),
        };

        public SynergyCommand(ICoreServerAPI api, SynergyConfig config)
        {
            this.api = api;
            this.config = config;
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
            sb.AppendLine("=== Synergy v1.0.0 ===");

            sb.AppendLine("Server Performance:");
            foreach (var (key, label, get, _) in toggles)
            {
                sb.AppendLine($"  {key}: {(get(config) ? "ON" : "OFF")}");
            }

            sb.AppendLine($"  activation-range: {config.EntityActivationRangeBlocks} blocks");

            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult ToggleAll(TextCommandCallingArgs args)
        {
            string action = (string)args.Parsers[0].GetValue();
            bool enable = action == "on";

            foreach (var (_, _, _, set) in toggles)
                set(config, enable);

            config.Save(api);
            return TextCommandResult.Success(
                $"All optimizations set to {(enable ? "ON" : "OFF")}.\n⚠️ Server restart required.");
        }

        private TextCommandResult Toggle(TextCommandCallingArgs args, string key, System.Action<SynergyConfig, bool> set)
        {
            string action = (string)args.Parsers[0].GetValue();
            bool enable = action == "on";

            set(config, enable);
            config.Save(api);

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
    }
}
