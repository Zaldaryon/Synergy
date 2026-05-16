using System.Collections.Generic;
using System.Text;
using Synergy.Client;
using Synergy.Server;

namespace Synergy.Diagnostics
{
    public static class CircuitBreakerStatus
    {
        private static readonly (string name, System.Func<bool> isDisabled, System.Func<int> errors)[] checks =
        {
            ("ActivationRange",      () => EntityActivationRange.disabled,      () => EntityActivationRange.errorCount),
            ("CollisionFastPath",    () => CollisionFastPath.disabled,          () => CollisionFastPath.errorCount),
            ("NetworkFlush",         () => NetworkFlushConsolidation.disabled,   () => NetworkFlushConsolidation.errorCount),
            ("BlockTickPooling",     () => BlockTickPooling.disabled,            () => BlockTickPooling.errorCount),
            ("InventoryDirtyScan",   () => InventoryDirtyScan.disabled,          () => InventoryDirtyScan.errorCount),
            ("PathfindingPool",      () => PathfindingOptimizations.disabled,    () => PathfindingOptimizations.errorCount),
            ("PathfindingThrottle",  () => PathfindingThrottle.disabled,         () => PathfindingThrottle.errorCount),
            ("RepulseThrottle",      () => RepulseAgentsThrottle.disabled,       () => RepulseAgentsThrottle.errorCount),
            ("AiBrainThrottle",      () => AiBrainThrottle.disabled,             () => AiBrainThrottle.errorCount),
            ("TrackingHysteresis",   () => TrackingHysteresis.disabled,          () => TrackingHysteresis.errorCount),
            ("SendFrequency",        () => DistanceSendFrequency.disabled,       () => DistanceSendFrequency.errorCount),
            ("AttributeSync",        () => AttributeResyncPrevention.disabled,   () => AttributeResyncPrevention.errorCount),
            ("SpawnPriority",        () => SpawnPriorityOrdering.disabled,       () => SpawnPriorityOrdering.errorCount),
            ("DeltaEncoding",        () => DeltaPositionEncoding.disabled,       () => DeltaPositionEncoding.errorCount),
            ("EntityLoadBudget",     () => EntityLoadBudgeting.disabled,         () => EntityLoadBudgeting.errorCount),
        };

        public static string GetSummary()
        {
            var degraded = new List<string>();
            foreach (var (name, isDisabled, _) in checks)
            {
                if (isDisabled()) degraded.Add(name);
            }

            if (degraded.Count == 0)
                return "CircuitBreaker: all healthy (0 degraded)";

            return $"CircuitBreaker: {degraded.Count} degraded: {string.Join(", ", degraded)}";
        }

        public static void AppendDetails(StringBuilder sb)
        {
            foreach (var (name, isDisabled, errors) in checks)
            {
                int err = errors();
                if (err > 0 || isDisabled())
                    sb.AppendLine($"  {name}: {(isDisabled() ? "DISABLED" : "OK")} (errors: {err})");
            }
        }
    }
}
