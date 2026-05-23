using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public sealed class DiagSpawnPriority : IDiagModule
    {
        public string ShortName => "spawnpriority";
        public string DisplayName => "Entity Spawn Priority Ordering";
        public bool Enabled => enabled;

        private static volatile bool enabled;
        private static long startTick;
        private static long sortOperations;
        private static long entitiesSorted;

        public void Enable() { enabled = true; Reset(); }
        public void Disable() { enabled = false; }

        public void Reset()
        {
            startTick = Environment.TickCount64;
            Interlocked.Exchange(ref sortOperations, 0);
            Interlocked.Exchange(ref entitiesSorted, 0);
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            double elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            long ops = Volatile.Read(ref sortOperations);
            long ents = Volatile.Read(ref entitiesSorted);
            double avgPerSort = ops > 0 ? (double)ents / ops : 0;

            DiagLog.Line(api, caller, $"spawnpriority: sorts={ops} entitiesSorted={ents} avgPerSort={avgPerSort:F1} elapsed={elapsed:F1}s");
        }

        public static void OnSort(int entityCount)
        {
            if (!enabled) return;
            Interlocked.Increment(ref sortOperations);
            Interlocked.Add(ref entitiesSorted, entityCount);
        }
    }
}
