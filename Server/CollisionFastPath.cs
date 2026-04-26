using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// P13: Skip collision resolution for stationary entities with no environmental flags.
    /// Only skips when ALL conditions met: motion==0, not in liquid, not swimming, not on fire.
    /// Vanilla behavior preserved: stationary entities have no collision to resolve.
    /// </summary>
    public static class CollisionFastPath
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var collisionTester = typeof(CollisionTester);
            var applyCollision = AccessTools.Method(collisionTester, "ApplyTerrainCollision",
                new[] { typeof(Entity), typeof(EntityPos), typeof(float),
                        typeof(Vec3d).MakeByRefType(), typeof(float), typeof(float) });
            if (applyCollision == null)
            {
                api.Logger.Warning("[Synergy] P13: Could not find ApplyTerrainCollision, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(applyCollision, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(applyCollision,
                prefix: new HarmonyMethod(typeof(CollisionFastPath), nameof(Prefix_ApplyTerrainCollision)));

            api.Logger.Notification("[Synergy] P13: Entity collision fast-path active");
        }

        public static bool Prefix_ApplyTerrainCollision(Entity entity, EntityPos entityPos,
            float dtFactor, ref Vec3d newPosition)
        {
            if (disabled || entity == null || entityPos == null) return true;

            try
            {
                // Never skip for players — client needs full collision for movement/gravity
                if (entity is EntityPlayer) return true;

                var motion = entityPos.Motion;
                if (motion.X != 0 || motion.Y != 0 || motion.Z != 0) return true;

                if (entity.FeetInLiquid) return true;
                if (entity.Swimming) return true;
                if (entity.InLava) return true;
                if (entity.IsOnFire) return true;

                // Match vanilla behavior: with zero motion, both flags are always set to false
                entity.CollidedVertically = false;
                entity.CollidedHorizontally = false;

                newPosition.Set(entityPos.X, entityPos.Y, entityPos.Z);
                return false;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] P13: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
