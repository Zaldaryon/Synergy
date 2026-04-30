using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Skip OnGameTick for non-player entities beyond activation range.
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

        // IL-emitted fast accessors via Harmony
        private delegate void FastDespawnDelegate(object server, Entity entity, EntityDespawnData data);
        private static FastDespawnDelegate despawnFast;
        private static AccessTools.FieldRef<object, IDictionary<long, Entity>> loadedEntitiesRef;

        // FrameProfilerUtil is public — use StaticFieldRefAccess for the static field, then direct method calls
        private static AccessTools.FieldRef<FrameProfilerUtil> frameProfilerRef;

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
                api.Logger.Warning("[Synergy] ActivationRange: Could not find ServerSystemEntitySimulation, skipping");
                return;
            }

            var tickEntities = AccessTools.Method(targetType, "TickEntities", new[] { typeof(float) });
            if (tickEntities == null)
            {
                api.Logger.Warning("[Synergy] ActivationRange: Could not find TickEntities method, skipping");
                return;
            }

            // Cache despawn delegate via DynamicMethod (skipVisibility:true bypasses
            // Delegate.CreateDelegate limitation with internal types)
            var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
            if (serverMainType != null)
            {
                var despawnMethod = AccessTools.Method(serverMainType, "DespawnEntity",
                    new[] { typeof(Entity), typeof(EntityDespawnData) });
                if (despawnMethod != null)
                {
                    var dm = new System.Reflection.Emit.DynamicMethod(
                        "Synergy_FastDespawn", typeof(void),
                        new[] { typeof(object), typeof(Entity), typeof(EntityDespawnData) },
                        typeof(EntityActivationRange), skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Castclass, serverMainType);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
                    il.Emit(System.Reflection.Emit.OpCodes.Callvirt, despawnMethod);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    despawnFast = (FastDespawnDelegate)dm.CreateDelegate(typeof(FastDespawnDelegate));
                }

                loadedEntitiesRef = AccessTools.FieldRefAccess<IDictionary<long, Entity>>(serverMainType, "LoadedEntities");

                // FrameProfiler is public static FrameProfilerUtil on ServerMain
                frameProfilerRef = AccessTools.StaticFieldRefAccess<FrameProfilerUtil>(
                    AccessTools.Field(serverMainType, "FrameProfiler"));
            }

            if (!ConflictDetector.IsSafeToPatch(tickEntities, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(tickEntities,
                prefix: new HarmonyMethod(typeof(EntityActivationRange), nameof(Prefix_TickEntities)));

            api.Logger.Notification("[Synergy] ActivationRange: Entity activation range optimization active (range: {0} blocks)", activationRange);
        }

        public static bool Prefix_TickEntities(float dt, object __instance, object ___server)
        {
            if (disabled) return true;

            try
            {
                var loadedEntities = loadedEntitiesRef?.Invoke(___server);
                if (loadedEntities == null) return true;

                // FrameProfilerUtil is public — direct access, zero reflection
                var profiler = frameProfilerRef?.Invoke();
                profiler?.Enter("tickentities");

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
                            sapi?.Logger.Warning("[Synergy] ActivationRange: Exception ticking player {0} ({1}) at {2}: {3}",
                                ep.GetName(), ep.PlayerUID, ep.Pos, ex.Message);
                        else
                            sapi?.Logger.Warning("[Synergy] ActivationRange: Exception ticking entity {0} ({1}) at {2}: {3}",
                                entity.GetName(), entity.Properties?.Class, entity.Pos, ex.Message);
                    }

                    if (entity.ShouldDespawn)
                        despawnList.Add(new KeyValuePair<Entity, EntityDespawnData>(entity, entity.DespawnReason));
                }

                profiler?.Enter("despawning");

                foreach (var kvp in despawnList)
                {
                    despawnFast?.Invoke(___server, kvp.Key, kvp.Value);

                    profiler?.Mark("despawned-", kvp.Key.Code?.Path ?? "unknown");
                }

                profiler?.Leave();
                profiler?.Leave();

                return false;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] ActivationRange: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
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
                        sapi?.Logger.Debug("[Synergy] ActivationRange: Error in whitelisted behavior {0}: {1}", name, ex.Message);
                    }
                }
            }
        }
    }
}
