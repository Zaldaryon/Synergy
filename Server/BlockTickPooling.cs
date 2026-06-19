using System.Threading;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Synergy.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Pool BlockPos objects in random block tick loop to reduce GC pressure.
    /// Patches tryTickBlock to reuse BlockPos from a ThreadStatic pool instead of calling Copy().
    /// Two separate pools (BlockPos and FluidBlockPos) preserve type identity for main-thread
    /// dequeue logic which dispatches based on `is FluidBlockPos` type checks.
    /// Pool resets only when queuedTicks is empty to prevent coordinate corruption from
    /// overwriting in-flight entries awaiting main-thread consumption.
    /// Vanilla behavior preserved: same blocks ticked at same rate, only allocation pattern changes.
    /// </summary>
    public static class BlockTickPooling
    {
        private static ICoreServerAPI sapi;
        internal static int errorCount;
        internal static bool disabled;

        private const int PoolSize = 512;

        [ThreadStatic] private static BlockPos[] posPool;
        [ThreadStatic] private static int posPoolIndex;

        [ThreadStatic] private static FluidBlockPos[] fluidPosPool;
        [ThreadStatic] private static int fluidPosPoolIndex;

        // Cached at init — ConstructorInfo is more reliable than Activator for private nested types
        private static ConstructorInfo blockPosWithExtraCtor;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var targetType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemBlockSimulation");
            if (targetType == null)
            {
                api.Logger.Warning("[Synergy] BlockTickPooling: Could not find ServerSystemBlockSimulation, skipping");
                return;
            }

            var tryTickBlock = AccessTools.Method(targetType, "tryTickBlock",
                new[] { typeof(Block), typeof(BlockPos) });
            if (tryTickBlock == null)
            {
                api.Logger.Warning("[Synergy] BlockTickPooling: Could not find tryTickBlock method, skipping");
                return;
            }

            var onSepThread = AccessTools.Method(targetType, "OnSeparateThreadTick", Type.EmptyTypes);
            if (onSepThread == null)
            {
                api.Logger.Warning("[Synergy] BlockTickPooling: Could not find OnSeparateThreadTick, skipping");
                return;
            }

            // Cache ConstructorInfo for the private nested type
            var blockPosWithExtraType = targetType.GetNestedType("BlockPosWithExtraObject", BindingFlags.NonPublic | BindingFlags.Public);
            if (blockPosWithExtraType != null)
            {
                blockPosWithExtraCtor = blockPosWithExtraType.GetConstructor(new[] { typeof(BlockPos), typeof(object) });
            }

            if (blockPosWithExtraCtor == null)
            {
                api.Logger.Warning("[Synergy] BlockTickPooling: Could not resolve BlockPosWithExtraObject constructor, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(tryTickBlock, SynergyMod.HarmonyId, api.Logger))
                return;
            if (!ConflictDetector.IsSafeToPatch(onSepThread, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(tryTickBlock,
                prefix: new HarmonyMethod(typeof(BlockTickPooling), nameof(Prefix_tryTickBlock)));
            harmony.Patch(onSepThread,
                prefix: new HarmonyMethod(typeof(BlockTickPooling), nameof(Prefix_OnSeparateThreadTick)));

            api.Logger.Notification("[Synergy] BlockTickPooling: Block tick pooling optimization active");
        }

        public static void Prefix_OnSeparateThreadTick(ConcurrentQueue<object> ___queuedTicks)
        {
            // Only reset pools when queue is empty — pooled objects may still be awaiting
            // main-thread consumption. Resetting while entries are in-flight overwrites
            // their coordinates → NullReferenceException on GetBlock().
            if (___queuedTicks == null || ___queuedTicks.IsEmpty)
            {
                posPoolIndex = 0;
                fluidPosPoolIndex = 0;
            }
        }

        private static BlockPos GetPooledPos(BlockPos source)
        {
            if (source is FluidBlockPos)
                return GetPooledFluidPos(source);

            if (posPool == null)
            {
                posPool = new BlockPos[PoolSize];
                for (int i = 0; i < PoolSize; i++)
                    posPool[i] = new BlockPos(0);
            }

            if (posPoolIndex < PoolSize)
            {
                var pos = posPool[posPoolIndex++];
                pos.Set(source.X, source.Y, source.Z);
                pos.SetDimension(source.dimension);
                DiagBlockTickPooling.OnPooled();
                return pos;
            }

            DiagBlockTickPooling.OnFallback();
            return source.Copy();
        }

        private static FluidBlockPos GetPooledFluidPos(BlockPos source)
        {
            if (fluidPosPool == null)
            {
                fluidPosPool = new FluidBlockPos[PoolSize];
                for (int i = 0; i < PoolSize; i++)
                    fluidPosPool[i] = new FluidBlockPos();
            }

            if (fluidPosPoolIndex < PoolSize)
            {
                var pos = fluidPosPool[fluidPosPoolIndex++];
                pos.Set(source.X, source.Y, source.Z);
                pos.SetDimension(source.dimension);
                DiagBlockTickPooling.OnPooled();
                return pos;
            }

            DiagBlockTickPooling.OnFallback();
            return (FluidBlockPos)source.Copy();
        }

        public static bool Prefix_tryTickBlock(Block block, BlockPos atPos,
            ref bool __result, Random ___rand, ConcurrentQueue<object> ___queuedTicks)
        {
            if (disabled) return true;

            try
            {
                if (!block.ShouldReceiveServerGameTicks(sapi.World, atPos, ___rand, out var extra))
                {
                    __result = false;
                    return false;
                }

                if (extra != null)
                {
                    // Extra-carrying tick: use Copy() which preserves FluidBlockPos type,
                    // and wrap in BlockPosWithExtraObject via cached ConstructorInfo.
                    var posCopy = atPos.Copy();
                    ___queuedTicks.Enqueue(blockPosWithExtraCtor.Invoke(new object[] { posCopy, extra }));
                }
                else
                {
                    // No-extra tick: use type-aware pooled pos (BlockPos or FluidBlockPos)
                    ___queuedTicks.Enqueue(GetPooledPos(atPos));
                }

                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errorCount) >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] BlockTickPooling: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
