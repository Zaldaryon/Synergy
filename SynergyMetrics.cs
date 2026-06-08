using System;
using System.Diagnostics.Metrics;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy
{
    /// <summary>
    /// Unified metrics collection for all Synergy optimizations.
    /// Combines: /synergy stats command, periodic log output, and .NET Metrics API
    /// (consumable by dotnet-counters, dotnet-monitor, Prometheus via OpenTelemetry).
    ///
    /// All counters use Interlocked for thread safety (physics threads increment).
    /// Periodic log runs every 60s on main thread.
    /// </summary>
    public static class SynergyMetrics
    {
        // .NET Metrics API (dotnet-counters compatible)
        private static Meter meter;

        // Counters (thread-safe, incremented from physics/main threads)
        private static long entitiesActive;
        private static long entitiesSleeping;
        private static long aiStartNewTasksSkipped;
        private static long aiStartNewTasksTotal;
        private static long deltaClientsCount;
        private static long deltaBatchesSent;
        private static long deltaBytesTotal;
        private static long vanillaBytesTotal;
        private static long entityLoadsQueued;
        private static long entityLoadsProcessed;

        private static ICoreServerAPI sapi;
        private static long tickListenerId;
        private static long startTimeMs;

        public static void Initialize(ICoreServerAPI api)
        {
            sapi = api;
            startTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // .NET Metrics API — visible via dotnet-counters monitor Synergy
            meter = new Meter("Synergy", "1.0");
            meter.CreateObservableGauge("synergy.entities.active", () => Volatile.Read(ref entitiesActive));
            meter.CreateObservableGauge("synergy.entities.sleeping", () => Volatile.Read(ref entitiesSleeping));
            meter.CreateObservableGauge("synergy.ai.throttle_pct", () =>
            {
                long total = Volatile.Read(ref aiStartNewTasksTotal);
                long skipped = Volatile.Read(ref aiStartNewTasksSkipped);
                return total > 0 ? (double)skipped / total * 100.0 : 0;
            });
            meter.CreateObservableGauge("synergy.delta.clients", () => Volatile.Read(ref deltaClientsCount));
            meter.CreateObservableCounter("synergy.delta.batches_sent", () => Volatile.Read(ref deltaBatchesSent));
            meter.CreateObservableGauge("synergy.delta.bytes_saved_per_sec", () =>
            {
                long vanilla = Volatile.Read(ref vanillaBytesTotal);
                long delta = Volatile.Read(ref deltaBytesTotal);
                long elapsed = Math.Max(1, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTimeMs) / 1000);
                return vanilla > delta ? (double)(vanilla - delta) / elapsed : 0;
            });

            // Periodic log (every 60s)
            tickListenerId = api.Event.RegisterGameTickListener(LogPeriodic, 60000);

            api.Logger.Notification("[Synergy] Metrics system initialized");
        }

        public static void Dispose()
        {
            if (sapi != null)
                sapi.Event.UnregisterGameTickListener(tickListenerId);
            meter?.Dispose();
        }

        // --- Increment methods (called from optimization patches) ---

        public static void SetEntityCounts(int active, int sleeping)
        {
            Volatile.Write(ref entitiesActive, active);
            Volatile.Write(ref entitiesSleeping, sleeping);
        }

        public static void RecordAiTick(bool wasThrottled)
        {
            Interlocked.Increment(ref aiStartNewTasksTotal);
            if (wasThrottled) Interlocked.Increment(ref aiStartNewTasksSkipped);
        }

        public static void SetDeltaClients(int count)
        {
            Volatile.Write(ref deltaClientsCount, count);
        }

        public static void RecordDeltaBatch(int deltaBytes, int vanillaEquivBytes)
        {
            Interlocked.Increment(ref deltaBatchesSent);
            Interlocked.Add(ref deltaBytesTotal, deltaBytes);
            Interlocked.Add(ref vanillaBytesTotal, vanillaEquivBytes);
        }

        public static void RecordEntityLoad(int queued, int processed)
        {
            Interlocked.Add(ref entityLoadsQueued, queued);
            Interlocked.Add(ref entityLoadsProcessed, processed);
        }

        // --- /synergy stats output ---

        public static string GetStatsText()
        {
            long uptimeS = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTimeMs) / 1000;
            long h = uptimeS / 3600, m = (uptimeS % 3600) / 60;

            long active = Volatile.Read(ref entitiesActive);
            long sleeping = Volatile.Read(ref entitiesSleeping);
            long aiTotal = Volatile.Read(ref aiStartNewTasksTotal);
            long aiSkipped = Volatile.Read(ref aiStartNewTasksSkipped);
            int aiPct = aiTotal > 0 ? (int)(aiSkipped * 100 / aiTotal) : 0;
            long dClients = Volatile.Read(ref deltaClientsCount);
            long dBatches = Volatile.Read(ref deltaBatchesSent);
            long dBytes = Volatile.Read(ref deltaBytesTotal);
            long vBytes = Volatile.Read(ref vanillaBytesTotal);
            long savedBytes = vBytes > dBytes ? vBytes - dBytes : 0;
            long savedKBs = uptimeS > 0 ? savedBytes / 1024 / Math.Max(1, uptimeS) : 0;
            long eQueued = Volatile.Read(ref entityLoadsQueued);
            long eProcessed = Volatile.Read(ref entityLoadsProcessed);

            return $"=== Synergy Stats ===\n" +
                   $"Uptime: {h}h {m}m\n" +
                   $"Entities: {active} active, {sleeping} sleeping\n" +
                   $"AI throttled: {aiPct}% of StartNewTasks skipped ({aiSkipped}/{aiTotal})\n" +
                   $"Delta clients: {dClients}\n" +
                   $"Delta batches sent: {dBatches}\n" +
                   $"Bandwidth saved: ~{savedKBs} KB/s\n" +
                   $"Entity loads: {eProcessed} processed, {eQueued} queued total";
        }

        // --- Periodic log ---

        private static void LogPeriodic(float dt)
        {
            long active = Volatile.Read(ref entitiesActive);
            long sleeping = Volatile.Read(ref entitiesSleeping);
            long aiTotal = Volatile.Read(ref aiStartNewTasksTotal);
            long aiSkipped = Volatile.Read(ref aiStartNewTasksSkipped);
            int aiPct = aiTotal > 0 ? (int)(aiSkipped * 100 / aiTotal) : 0;
            long dClients = Volatile.Read(ref deltaClientsCount);

            sapi?.Logger.Notification(
                "[Synergy] Stats: {0} active, {1} sleeping, {2}% AI throttled, {3} delta clients",
                active, sleeping, aiPct, dClients);
        }
    }
}
