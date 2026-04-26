using System;
using System.Collections.Concurrent;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// P3: Pool BlockPos objects in random block tick loop to reduce GC pressure.
    /// Patches tryTickBlock to reuse BlockPos from a ThreadStatic pool instead of calling Copy().
    /// Pool is pre-populated and resets index each tick cycle via OnSeparateThreadTick prefix.
    /// Vanilla behavior preserved: same blocks ticked at same rate, only allocation pattern changes.
    /// </summary>
    public static class BlockTickPooling
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;

        [ThreadStatic] private static BlockPos[] posPool;
        [ThreadStatic] private static int poolIndex;
        private const int PoolSize = 512;

        // Cached at init
        private static Type blockPosWithExtraType;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var targetType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemBlockSimulation");
            if (targetType == null)
            {
                api.Logger.Warning("[Synergy] P3: Could not find ServerSystemBlockSimulation, skipping");
                return;
            }

            var tryTickBlock = AccessTools.Method(targetType, "tryTickBlock",
                new[] { typeof(Block), typeof(BlockPos) });
            if (tryTickBlock == null)
            {
                api.Logger.Warning("[Synergy] P3: Could not find tryTickBlock method, skipping");
                return;
            }

            // 1.22: OnSeparateThreadTick is parameterless
            var onSepThread = AccessTools.Method(targetType, "OnSeparateThreadTick", Type.EmptyTypes);
            if (onSepThread == null)
            {
                api.Logger.Warning("[Synergy] P3: Could not find OnSeparateThreadTick, skipping");
                return;
            }

            // Cache the private nested type at init
            blockPosWithExtraType = AccessTools.TypeByName(
                "Vintagestory.Server.ServerSystemBlockSimulation+BlockPosWithExtraObject");

            if (!ConflictDetector.IsSafeToPatch(tryTickBlock, SynergyMod.HarmonyId, api.Logger))
                return;
            if (!ConflictDetector.IsSafeToPatch(onSepThread, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(tryTickBlock,
                prefix: new HarmonyMethod(typeof(BlockTickPooling), nameof(Prefix_tryTickBlock)));
            harmony.Patch(onSepThread,
                prefix: new HarmonyMethod(typeof(BlockTickPooling), nameof(Prefix_OnSeparateThreadTick)));

            api.Logger.Notification("[Synergy] P3: Block tick pooling optimization active");
        }

        public static void Prefix_OnSeparateThreadTick()
        {
            poolIndex = 0;
        }

        private static BlockPos GetPooledPos(BlockPos source)
        {
            if (posPool == null)
            {
                posPool = new BlockPos[PoolSize];
                for (int i = 0; i < PoolSize; i++)
                    posPool[i] = new BlockPos(0);
            }

            if (poolIndex < PoolSize)
            {
                var pos = posPool[poolIndex++];
                pos.Set(source.X, source.Y, source.Z);
                pos.SetDimension(source.dimension);
                return pos;
            }

            return source.Copy();
        }

        public static bool Prefix_tryTickBlock(Block block, BlockPos atPos,
            ref bool __result, object ___server, Random ___rand, ConcurrentQueue<object> ___queuedTicks)
        {
            if (disabled) return true;

            try
            {
                IWorldAccessor world = sapi.World;

                if (!block.ShouldReceiveServerGameTicks(world, atPos, ___rand, out var extra))
                {
                    __result = false;
                    return false;
                }

                var pooledPos = GetPooledPos(atPos);

                if (extra == null)
                {
                    ___queuedTicks.Enqueue(pooledPos);
                }
                else if (blockPosWithExtraType != null)
                {
                    ___queuedTicks.Enqueue(Activator.CreateInstance(blockPosWithExtraType, pooledPos, extra));
                }
                else
                {
                    ___queuedTicks.Enqueue(atPos.Copy());
                }

                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] P3: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
