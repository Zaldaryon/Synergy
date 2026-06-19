using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Synergy.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Synergy.Server
{
    /// <summary>
    /// Throttle AI task evaluation (StartNewTasks) based on distance to nearest player.
    /// ProcessRunningTasks always runs every tick — entities never freeze mid-action.
    ///
    /// Uses continuous DEAR formula from Airplane/Pufferfish:
    ///   freq = clamp(1, maxFreq, dist² / divisor)
    /// With divisor=512, maxFreq=20:
    ///   ≤22 blocks: every tick (vanilla)
    ///   32 blocks: every 2nd tick
    ///   45 blocks: every 4th tick
    ///
    /// Breathe, Health, and Damage are separate behaviors — not affected by this throttle.
    /// Entities under attack always get full-rate AI (emotion state check).
    ///
    /// References:
    /// - Airplane DEAR: blog.airplane.gg/dear-configuration/
    /// - Pufferfish DAB: github.com/pufferfish-gg/Pufferfish
    /// - Game AI Pro Ch.14: "Phenomenal AI Level-of-Detail Control with the LOD Trader"
    /// </summary>
    public static class AiBrainThrottle
    {
        private static ICoreServerAPI sapi;
        internal static int errorCount;
        internal static bool disabled;
        private static int tickCounter;

        private const int Divisor = 512;   // dist²/512: 22 blocks = freq 1, 32 = freq 2, 45 = freq 4
        private const int MaxFreq = 20;    // Cap: at most skip 19 out of 20 ticks

        // IL-emitted delegate to call private ProcessRunningTasks(float dt) directly
        private delegate void ProcessRunningTasksFn(object taskManager, float dt);
        private static ProcessRunningTasksFn processRunningTasks;

        // Accessor for AiTaskManager.entity field (to get NearestPlayerDistance)
        private static AccessTools.FieldRef<object, Entity> taskManagerEntityRef;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;
            tickCounter = 0;

            var taskMgrType = AccessTools.TypeByName("Vintagestory.API.Common.AiTaskManager")
                ?? AccessTools.TypeByName("AiTaskManager");
            if (taskMgrType == null)
            {
                api.Logger.Warning("[Synergy] AiBrainThrottle: AiTaskManager not found, skipping");
                return;
            }

            var onGameTick = AccessTools.Method(taskMgrType, "OnGameTick", new[] { typeof(float) });
            if (onGameTick == null)
            {
                api.Logger.Warning("[Synergy] AiBrainThrottle: OnGameTick not found, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(onGameTick, SynergyMod.HarmonyId, api.Logger))
                return;

            // Resolve private ProcessRunningTasks(float) via IL emit for zero-overhead calls
            var prtMethod = AccessTools.Method(taskMgrType, "ProcessRunningTasks", new[] { typeof(float) });
            if (prtMethod == null)
            {
                api.Logger.Warning("[Synergy] AiBrainThrottle: ProcessRunningTasks not found, skipping");
                return;
            }

            var dm = new DynamicMethod("Call_ProcessRunningTasks", typeof(void),
                new[] { typeof(object), typeof(float) }, taskMgrType, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, taskMgrType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, prtMethod);
            il.Emit(OpCodes.Ret);
            processRunningTasks = (ProcessRunningTasksFn)dm.CreateDelegate(typeof(ProcessRunningTasksFn));

            // Resolve entity field on AiTaskManager
            taskManagerEntityRef = AccessTools.FieldRefAccess<Entity>(taskMgrType, "entity");

            harmony.Patch(onGameTick,
                prefix: new HarmonyMethod(typeof(AiBrainThrottle), nameof(Prefix_OnGameTick)));

            api.Event.RegisterGameTickListener(_ => Interlocked.Increment(ref tickCounter), 1);

            api.Logger.Notification("[Synergy] AiBrainThrottle: AI brain tick throttle active (divisor={0}, maxFreq={1})",
                Divisor, MaxFreq);
        }

        public static bool Prefix_OnGameTick(object __instance, float dt)
        {
            if (disabled) return true;

            try
            {
                var entity = taskManagerEntityRef(__instance);
                if (entity == null) return true;

                // Respect AiRuntimeConfig.RunAiTasks — let vanilla handle task shutdown
                if (!Vintagestory.GameContent.AiRuntimeConfig.RunAiTasks) return true;

                // Players always get full AI
                if (entity is EntityPlayer) return true;

                // Mod entities with custom AI/pathfinding that breaks under throttling
                if (ModEntityExclusions.IsExcludedMod(entity)) return true;

                // Entities in combat or fleeing: full rate
                if (entity.Alive)
                {
                    var emStates = entity.GetBehavior<EntityBehaviorEmotionStates>();
                    if (emStates != null &&
                        (emStates.IsInEmotionState("aggressivearoundentities") ||
                         emStates.IsInEmotionState("aggressiveondamage") ||
                         emStates.IsInEmotionState("fleeondamage") ||
                         emStates.IsInEmotionState("fleealarmondamage")))
                    {
                        DiagAiBrainThrottle.OnCombatBypass();
                        return true;
                    }
                }

                // DEAR formula: freq = clamp(1, maxFreq, dist² / divisor)
                // Load-adaptive: scale divisor down under server pressure (more throttling)
                float dist = entity.NearestPlayerDistance;
                if (dist > 256f) return true; // Beyond any reasonable activation range — let vanilla run

                int effectiveDivisor = Divisor;
                if (!EntityActivationRange.disabled && EntityActivationRange.LastTickDurationMs > EntityActivationRange.adaptiveThresholdMs)
                {
                    float loadScale = Math.Max(1f, EntityActivationRange.LastTickDurationMs / 50f);
                    effectiveDivisor = Math.Max(32, (int)(Divisor / loadScale));
                }
                int freq = Math.Clamp((int)(dist * dist / effectiveDivisor), 1, MaxFreq);

                if (freq <= 1) return true; // Close enough — full rate

                int tick = Volatile.Read(ref tickCounter);
                // Offset by entity ID to distribute load across tick slots (prevents thundering herd)
                if ((tick + (int)((uint)entity.EntityId % (uint)freq)) % freq != 0)
                {
                    // Throttled: only run ProcessRunningTasks (active tasks continue)
                    processRunningTasks(__instance, dt);
                    DiagAiBrainThrottle.OnThrottled();
                    SynergyMetrics.RecordAiTick(true);
                    return false; // Skip original (which would also call StartNewTasks)
                }

                DiagAiBrainThrottle.OnFullTick();
                SynergyMetrics.RecordAiTick(false);
                return true; // Full tick — StartNewTasks + ProcessRunningTasks
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errorCount) >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] AiBrainThrottle: Auto-disabled after {0} errors: {1}",
                        errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
