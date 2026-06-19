using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Synergy.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Synergy.Server
{
    /// <summary>
    /// Skip OnGameTick for non-player entities beyond activation range.
    /// Uses entity.NearestPlayerDistance (already computed by PhysicsManager).
    /// Whitelisted behaviors (breathe, health, despawn, decay) still tick for sleeping entities.
    /// Calendar-based behaviors (growth, breeding, harvestable) catch up automatically.
    ///
    /// For entities with AI (taskai behavior), sleeping entities still get ProcessRunningTasks
    /// called (without StartNewTasks) so that running tasks continue executing and their
    /// internal timers (stuck detection, timeouts) don't corrupt. This matches Paper MC's
    /// "inactive-goal-selector-disable" pattern: running goals continue, new goal selection stops.
    /// </summary>
    public static class EntityActivationRange
    {
        private static ICoreServerAPI sapi;
        internal static int errorCount;
        internal static bool disabled;
        private static float activationRange = 48f;
        private static HashSet<string> excludedEntityCodes;

        // Load-adaptive throttling
        private static readonly System.Diagnostics.Stopwatch tickStopwatch = new();
        internal static long LastTickDurationMs = 50;
        private static bool adaptiveEnabled = true;
        private static float adaptiveMinBlocks = 16f;
        internal static int adaptiveThresholdMs = 200;
        private static int postSaveCooldown;

        // IL-emitted fast accessors via Harmony
        private delegate void FastDespawnDelegate(object server, Entity entity, EntityDespawnData data);
        private static FastDespawnDelegate despawnFast;
        private static AccessTools.FieldRef<object, IDictionary<long, Entity>> loadedEntitiesRef;

        // FrameProfilerUtil is public — use StaticFieldRefAccess for the static field, then direct method calls
        private static AccessTools.FieldRef<FrameProfilerUtil> frameProfilerRef;

        // IL-emitted delegate to call AiTaskManager.ProcessRunningTasks(float) directly
        private delegate void ProcessRunningTasksFn(object taskManager, float dt);
        private static ProcessRunningTasksFn processRunningTasks;

        private static readonly HashSet<string> whitelistedBehaviors = new(StringComparer.OrdinalIgnoreCase)
        {
            "breathe",
            "health",
            "timeddespawn",
            "deaddecay",
            "grow",
            "multiply",
            // "harvestable" removed — behavior has ThreadSafe=true, lives in ServerBehaviorsThreadsafe
            // (ticked by PhysicsManager on physics thread), never found in ServerBehaviorsMainThread.
        };

        // When no players are online, skip whitelisted ticks entirely.
        // This prevents sleeping entity behaviors (breathe reads blocks) from
        // keeping chunks "fresh" via lastReadOrWrite, which blocks chunk unloading
        // and causes infinite loops in mods like Farseer that rely on ChunkColumnLoaded events.
        // All whitelisted behaviors are calendar-based or non-observable without players.
        private static bool noPlayersOnline;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;
            activationRange = SynergyMod.Config?.EntityActivationRangeBlocks ?? 48f;
            adaptiveEnabled = SynergyMod.Config?.AdaptiveRangeEnabled ?? true;
            adaptiveMinBlocks = SynergyMod.Config?.AdaptiveRangeMinBlocks ?? 16f;
            adaptiveThresholdMs = SynergyMod.Config?.AdaptiveRangeThresholdMs ?? 200;

            var exclusions = SynergyMod.Config?.ActivationRangeExcludedEntities;
            excludedEntityCodes = exclusions != null && exclusions.Length > 0
                ? new HashSet<string>(exclusions, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            // Resolve AiTaskManager.ProcessRunningTasks(float) for reduced-rate AI ticking
            var taskMgrType = typeof(AiTaskManager);
            var prtMethod = AccessTools.Method(taskMgrType, "ProcessRunningTasks", new[] { typeof(float) });
            if (prtMethod != null)
            {
                var dm = new DynamicMethod("EAR_ProcessRunningTasks", typeof(void),
                    new[] { typeof(object), typeof(float) }, taskMgrType, true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, taskMgrType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, prtMethod);
                il.Emit(OpCodes.Ret);
                processRunningTasks = (ProcessRunningTasksFn)dm.CreateDelegate(typeof(ProcessRunningTasksFn));
            }
            else
            {
                api.Logger.Warning("[Synergy] ActivationRange: Could not resolve ProcessRunningTasks — sleeping AI maintenance disabled");
            }

            if (!ConflictDetector.IsSafeToPatch(tickEntities, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(tickEntities,
                prefix: new HarmonyMethod(typeof(EntityActivationRange), nameof(Prefix_TickEntities)));

            api.Event.GameWorldSave += () => { postSaveCooldown = 3; };
            tickStopwatch.Start();

            api.Logger.Notification("[Synergy] ActivationRange: Entity activation range optimization active (range: {0} blocks)", activationRange);
            if (excludedEntityCodes.Count > 0)
                api.Logger.Notification("[Synergy] ActivationRange: Excluded entities (always full-tick): {0}", string.Join(", ", excludedEntityCodes));
        }

        public static bool Prefix_TickEntities(float dt, object __instance, object ___server)
        {
            if (disabled) return true;

            try
            {
                // Measure previous tick duration for adaptive throttling
                long elapsed = tickStopwatch.ElapsedMilliseconds;
                if (elapsed > 0) LastTickDurationMs = elapsed;
                tickStopwatch.Restart();

                // Compute effective activation range based on server load
                float effectiveRange = activationRange;
                if (adaptiveEnabled && LastTickDurationMs > adaptiveThresholdMs)
                {
                    float loadFactor = Math.Min(1f, (float)adaptiveThresholdMs / Math.Max(50f, LastTickDurationMs));
                    effectiveRange = Math.Max(adaptiveMinBlocks, activationRange * loadFactor);
                }
                if (postSaveCooldown > 0)
                {
                    effectiveRange *= 0.5f;
                    effectiveRange = Math.Max(adaptiveMinBlocks, effectiveRange);
                    postSaveCooldown--;
                }

                var loadedEntities = loadedEntitiesRef?.Invoke(___server);
                if (loadedEntities == null) return true;

                noPlayersOnline = sapi.World.AllOnlinePlayers.Length == 0;

                // FrameProfilerUtil is public — direct access, zero reflection
                var profiler = frameProfilerRef?.Invoke();
                profiler?.Enter("tickentities");

                var despawnList = new List<KeyValuePair<Entity, EntityDespawnData>>();
                int activeCount = 0, sleepingCount = 0;

                foreach (Entity entity in loadedEntities.Values)
                {
                    if (Dimensions.ShouldNotTick(entity.Pos, entity.Api))
                        continue;

                    try
                    {
                        if (ShouldFullTick(entity, effectiveRange))
                        {
                            entity.OnGameTick(dt);
                            DiagActivationRange.OnFullTick();
                            activeCount++;
                        }
                        else
                        {
                            if (!noPlayersOnline)
                                TickWhitelistedBehaviors(entity, dt);
                            DiagActivationRange.OnSleeping();
                            sleepingCount++;
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

                SynergyMetrics.SetEntityCounts(activeCount, sleepingCount);

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

        private static bool ShouldFullTick(Entity entity, float effectiveRange)
        {
            if (entity.AlwaysActive) return true;
            if (entity is EntityPlayer) return true;
            if (entity.NearestPlayerDistance <= effectiveRange) return true;
            // Entities in hazardous environments must full-tick so they can react (move, flee, swim)
            if (entity.InLava || entity.IsOnFire) return true;
            if (entity.Swimming || entity.FeetInLiquid) return true;
            // Config-driven exclusion for mod entities that require continuous ticking (e.g. smoke dissipation)
            if (excludedEntityCodes.Count > 0 && entity.Code != null && excludedEntityCodes.Contains(entity.Code.Path)) return true;
            // VSVillage entities use custom AI/pathfinding that breaks under activation range sleeping
            if (ModEntityExclusions.IsExcludedMod(entity)) return true;
            return false;
        }

        private static void TickWhitelistedBehaviors(Entity entity, float dt)
        {
            // Only tick ServerBehaviorsMainThread — ServerBehaviorsThreadsafe are ticked
            // by PhysicsManager on the physics thread, not by TickEntities
            var behaviors = entity.ServerBehaviorsMainThread;
            if (behaviors == null) return;

            for (int i = 0; i < behaviors.Length; i++)
            {
                var behavior = behaviors[i];
                if (behavior == null) continue;

                string name = behavior.PropertyName();
                if (name == null) continue;

                if (whitelistedBehaviors.Contains(name))
                {
                    try
                    {
                        behavior.OnGameTick(dt);
                        DiagActivationRange.OnWhitelistedTick();
                    }
                    catch (Exception ex)
                    {
                        sapi?.Logger.Debug("[Synergy] ActivationRange: Error in whitelisted behavior {0}: {1}", name, ex.Message);
                    }
                }
                else if (name == "taskai" && processRunningTasks != null)
                {
                    // Reduced-rate AI tick: only ProcessRunningTasks (no StartNewTasks).
                    // This keeps running tasks alive (ContinueExecute) so their internal
                    // timers don't corrupt, while skipping expensive new task evaluation.
                    // Matches Paper MC's "inactive-goal-selector-disable" pattern.
                    try
                    {
                        var taskAi = behavior as EntityBehaviorTaskAI;
                        if (taskAi != null && entity.State == EnumEntityState.Active && entity.Alive)
                        {
                            taskAi.PathTraverser?.OnGameTick(dt);
                            processRunningTasks(taskAi.TaskManager, dt);
                            DiagActivationRange.OnWhitelistedTick();
                        }
                    }
                    catch (Exception ex)
                    {
                        sapi?.Logger.Debug("[Synergy] ActivationRange: Error in sleeping AI tick: {0}", ex.Message);
                    }
                }
            }
        }
    }
}
