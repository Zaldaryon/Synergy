using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagEntityLoadBudget : IDiagModule
    {
        public string ShortName => "entityloadbudget";
        public string DisplayName => "Entity Load Budgeting";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long entitiesQueued;
        private static long entitiesProcessed;
        private static long budgetedTicks;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref entitiesQueued, 0);
            Interlocked.Exchange(ref entitiesProcessed, 0);
            Interlocked.Exchange(ref budgetedTicks, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long q = Volatile.Read(ref entitiesQueued);
            long p = Volatile.Read(ref entitiesProcessed);
            long ticks = Volatile.Read(ref budgetedTicks);
            double avgPerTick = ticks > 0 ? (double)p / ticks : 0;

            DiagLog.Line(api, caller, $"entityloadbudget: queued={q} processed={p} budgetedTicks={ticks} avgPerTick={avgPerTick:F1} elapsed={elapsed:F1}s");
        }

        public static void OnQueued(int count)
        {
            if (!enabled) return;
            Interlocked.Add(ref entitiesQueued, count);
        }

        public static void OnProcessed(int count)
        {
            if (!enabled) return;
            Interlocked.Add(ref entitiesProcessed, count);
            Interlocked.Increment(ref budgetedTicks);
        }
    }
}
