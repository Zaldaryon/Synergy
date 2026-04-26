using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// P11: Skip OnGameTick for non-player entities beyond activation range.
    /// Uses entity.NearestPlayerDistance (already computed by PhysicsManager).
    /// Whitelisted behaviors (breathe, health, despawn, decay) still tick for sleeping entities.
    /// Calendar-based behaviors (growth, breeding, harvestable) catch up automatically.
    /// Vanilla behavior preserved: all time-sensitive behaviors use Calendar.TotalHours.
    /// </summary>
    public static class EntityActivationRange
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private static float activationRange = 48f;

        // Cached reflection — resolved once at init
        private static MethodInfo despawnMethod;
        private static FieldInfo frameProfilerField;
        private static FieldInfo loadedEntitiesField;
        private static FieldInfo serverApiField;
        private static MethodInfo profilerEnter;
        private static MethodInfo profilerLeave;
        private static MethodInfo profilerMark;

        private static readonly HashSet<string> whitelistedBehaviors = new(StringComparer.OrdinalIgnoreCase)
        {
            "breathe",
            "health",
            "despawn",
            "deaddecay",
            "decay",
            "grow",
            "multiply",
            "harvestable"
        };

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;
            activationRange = SynergyMod.Config?.EntityActivationRangeBlocks ?? 48f;

            var targetType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemEntitySimulation");
            if (targetType == null)
            {
                api.Logger.Warning("[Synergy] P11: Could not find ServerSystemEntitySimulation, skipping");
                return;
            }

            var tickEntities = AccessTools.Method(targetType, "TickEntities", new[] { typeof(float) });
            if (tickEntities == null)
            {
                api.Logger.Warning("[Synergy] P11: Could not find TickEntities method, skipping");
                return;
            }

            // Cache despawn method on ServerMain
            var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
            if (serverMainType != null)
            {
                despawnMethod = AccessTools.Method(serverMainType, "DespawnEntity",
                    new[] { typeof(Entity), typeof(EntityDespawnData) });
            }

            // Cache FrameProfiler static field
            if (serverMainType != null)
            {
                frameProfilerField = AccessTools.Field(serverMainType, "FrameProfiler");
                loadedEntitiesField = AccessTools.Field(serverMainType, "LoadedEntities");
            }

            // Cache api field for world access
            var serverApiType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
            if (serverApiType != null)
                serverApiField = AccessTools.Field(serverApiType, "api");

            // Cache FrameProfiler methods
            var profilerType = AccessTools.TypeByName("Vintagestory.API.Common.FrameProfilerUtil");
            if (profilerType != null)
            {
                profilerEnter = AccessTools.Method(profilerType, "Enter", new[] { typeof(string) });
                profilerLeave = AccessTools.Method(profilerType, "Leave");
                profilerMark = AccessTools.Method(profilerType, "Mark", new[] { typeof(string), typeof(string) });
            }

            if (!ConflictDetector.IsSafeToPatch(tickEntities, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(tickEntities,
                prefix: new HarmonyMethod(typeof(EntityActivationRange), nameof(Prefix_TickEntities)));

            api.Logger.Notification("[Synergy] P11: Entity activation range optimization active (range: {0} blocks)", activationRange);
        }

        public static bool Prefix_TickEntities(float dt, object __instance, object ___server)
        {
            if (disabled) return true;

            try
            {
                var loadedEntities = loadedEntitiesField?.GetValue(___server) as IDictionary<long, Entity>;
                if (loadedEntities == null) return true;

                // FrameProfiler — match vanilla's profiling calls
                object profiler = frameProfilerField?.GetValue(null);
                if (profiler != null) profilerEnter?.Invoke(profiler, new object[] { "tickentities" });

                var despawnList = new List<KeyValuePair<Entity, EntityDespawnData>>();

                foreach (Entity entity in loadedEntities.Values)
                {
                    if (Dimensions.ShouldNotTick(entity.Pos, entity.Api))
                        continue;

                    try
                    {
                        if (ShouldFullTick(entity))
                        {
                            entity.OnGameTick(dt);
                        }
                        else
                        {
                            TickWhitelistedBehaviors(entity, dt);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (entity is EntityPlayer ep)
                            sapi?.Logger.Warning("[Synergy] P11: Exception ticking player {0} ({1}) at {2}: {3}",
                                ep.GetName(), ep.PlayerUID, ep.Pos, ex.Message);
                        else
                            sapi?.Logger.Warning("[Synergy] P11: Exception ticking entity {0} ({1}) at {2}: {3}",
                                entity.GetName(), entity.Properties?.Class, entity.Pos, ex.Message);
                    }

                    if (entity.ShouldDespawn)
                        despawnList.Add(new KeyValuePair<Entity, EntityDespawnData>(entity, entity.DespawnReason));
                }

                if (profiler != null) profilerEnter?.Invoke(profiler, new object[] { "despawning" });

                foreach (var kvp in despawnList)
                {
                    despawnMethod?.Invoke(___server, new object[] { kvp.Key, kvp.Value });

                    if (profiler != null) profilerMark?.Invoke(profiler, new object[] { "despawned-", kvp.Key.Code?.Path ?? "unknown" });
                }

                if (profiler != null) profilerLeave?.Invoke(profiler, null);
                if (profiler != null) profilerLeave?.Invoke(profiler, null);

                return false;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] P11: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }

        private static bool ShouldFullTick(Entity entity)
        {
            if (entity.AlwaysActive) return true;
            if (entity is EntityPlayer) return true;
            if (entity.NearestPlayerDistance <= activationRange) return true;
            // Entities in lava or on fire must full-tick for ignition/damage/extinguishing logic
            if (entity.InLava || entity.IsOnFire) return true;
            return false;
        }

        private static void TickWhitelistedBehaviors(Entity entity, float dt)
        {
            // Only tick ServerBehaviorsMainThread — ServerBehaviorsThreadsafe are ticked
            // by PhysicsManager on the physics thread, not by TickEntities
            TickBehaviorArray(entity.ServerBehaviorsMainThread, dt);
        }

        private static void TickBehaviorArray(EntityBehavior[] behaviors, float dt)
        {
            if (behaviors == null) return;

            for (int i = 0; i < behaviors.Length; i++)
            {
                var behavior = behaviors[i];
                if (behavior == null) continue;

                string name = behavior.PropertyName();
                if (name != null && whitelistedBehaviors.Contains(name))
                {
                    try
                    {
                        behavior.OnGameTick(dt);
                    }
                    catch (Exception ex)
                    {
                        sapi?.Logger.Debug("[Synergy] P11: Error in whitelisted behavior {0}: {1}", name, ex.Message);
                    }
                }
            }
        }
    }
}
