using System;
using System.Runtime;
using HarmonyLib;
using Synergy.Client;
using Synergy.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy
{
    public class SynergyMod : ModSystem
    {
        public const string HarmonyId = "com.zaldaryon.synergy";

        public static SynergyConfig Config { get; internal set; }

        private Harmony harmony;
        private ICoreAPI api;
        private SynergyChannelManager channelManager;
        private DeltaPositionHandler deltaHandler;

        public override bool ShouldLoad(EnumAppSide forSide) => true;

        public override void StartClientSide(ICoreClientAPI capi)
        {
            api = capi;
            Config = SynergyConfig.Load(capi);

            harmony = new Harmony(HarmonyId);

            if (Config.EntityLoadBudgetingEnabled)
            {
                EntityLoadBudgeting.Initialize(capi, harmony);
            }

            deltaHandler = new DeltaPositionHandler(capi);
            deltaHandler.Initialize();

            capi.Logger.Notification("[Synergy] Client-side loaded");
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            api = sapi;
            Config ??= SynergyConfig.Load(sapi);
            harmony ??= new Harmony(HarmonyId);

            sapi.Logger.Notification("[Synergy] Server-side initializing...");

            // Metrics
            SynergyMetrics.Initialize(sapi);

            // Runtime tuning
            if (Config.GcDiagnosticsEnabled)
            {
                LogGcDiagnostics(sapi);
            }

            if (Config.GcSustainedLowLatencyEnabled)
            {
                ApplyGcLatencyMode(sapi);
            }

            int count = 0;

            if (Config.BlockTickPoolingEnabled)
            {
                BlockTickPooling.Initialize(sapi, harmony);
                count++;
            }

            if (Config.InventoryDirtyScanEnabled)
            {
                InventoryDirtyScan.Initialize(sapi, harmony);
                count++;
            }

            if (Config.NetworkFlushConsolidationEnabled)
            {
                NetworkFlushConsolidation.Initialize(sapi, harmony);
                count++;
            }

            if (Config.EntityTrackingHysteresisEnabled)
            {
                TrackingHysteresis.Initialize(sapi, harmony);
                count++;
            }

            if (Config.DistanceBasedSendFrequencyEnabled)
            {
                DistanceSendFrequency.Initialize(sapi, harmony);
                count++;
            }

            if (Config.AttributeSyncResyncPreventionEnabled)
            {
                AttributeResyncPrevention.Initialize(sapi, harmony);
                count++;
            }

            if (Config.EntitySpawnPriorityOrderingEnabled)
            {
                SpawnPriorityOrdering.Initialize(sapi, harmony);
                count++;
            }

            if (Config.CollisionFastPathEnabled)
            {
                CollisionFastPath.Initialize(sapi, harmony);
                count++;
            }

            if (Config.EntityActivationRangeEnabled)
            {
                EntityActivationRange.Initialize(sapi, harmony);
                count++;
            }

            if (Config.PathfindingOptimizationsEnabled)
            {
                PathfindingOptimizations.Initialize(sapi, harmony);
                count++;
            }

            if (Config.PathfindingThrottleEnabled)
            {
                PathfindingThrottle.Initialize(sapi, harmony);
                count++;
            }

            if (Config.RepulseAgentsThrottleEnabled)
            {
                RepulseAgentsThrottle.Initialize(sapi, harmony);
                count++;
            }

            if (Config.AiBrainThrottleEnabled)
            {
                AiBrainThrottle.Initialize(sapi, harmony);
                count++;
            }

            if (Config.SaveOptimizationEnabled)
            {
                SaveOptimization.Initialize(sapi, harmony);
                count++;
            }

            // Network channel (required for DeltaEncoding)
            channelManager = new SynergyChannelManager(sapi);
            channelManager.Initialize();

            if (Config.DeltaEncodingEnabled)
            {
                DeltaPositionEncoding.Initialize(sapi, harmony, channelManager);
                count++;
            }

            sapi.Logger.Notification("[Synergy] Server-side loaded ({0} optimizations active)", count);

            var command = new SynergyCommand(sapi, Config);
            command.Register();
        }

        private GCLatencyMode previousLatencyMode;

        private void ApplyGcLatencyMode(ICoreServerAPI sapi)
        {
            try
            {
                previousLatencyMode = GCSettings.LatencyMode;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                sapi.Logger.Notification("[Synergy] GC latency mode: {0} → SustainedLowLatency", previousLatencyMode);

                sapi.Event.RegisterGameTickListener(_ => MonitorHeap(sapi), 300000);
            }
            catch (Exception ex)
            {
                sapi.Logger.Warning("[Synergy] Could not set GC latency mode: {0}", ex.Message);
            }
        }

        private const long HeapWarnBytes = 4L * 1024 * 1024 * 1024;
        private const long HeapForceGcBytes = 8L * 1024 * 1024 * 1024;

        private void MonitorHeap(ICoreServerAPI sapi)
        {
            try
            {
                var info = GC.GetGCMemoryInfo();
                long heap = info.HeapSizeBytes;

                if (heap > HeapForceGcBytes)
                {
                    sapi.Logger.Warning("[Synergy] Heap {0:N0} MB exceeds threshold — triggering background Gen2 GC.",
                        heap / 1048576);
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
                }
                else if (heap > HeapWarnBytes)
                {
                    sapi.Logger.Notification("[Synergy] Heap {0:N0} MB (warning threshold). Fragmented: {1:N0} MB.",
                        heap / 1048576, info.FragmentedBytes / 1048576);
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Debug("[Synergy] Heap monitor error: {0}", ex.Message);
            }
        }

        private static void LogGcDiagnostics(ICoreServerAPI sapi)
        {
            try
            {
                var info = GC.GetGCMemoryInfo();
                sapi.Logger.Notification(
                    "[Synergy] GC diagnostics — Server GC: {0}, Latency: {1}, Heap: {2:N0} MB, Committed: {3:N0} MB, Fragmented: {4:N0} MB",
                    GCSettings.IsServerGC,
                    GCSettings.LatencyMode,
                    info.HeapSizeBytes / 1048576.0,
                    info.TotalCommittedBytes / 1048576.0,
                    info.FragmentedBytes / 1048576.0);
            }
            catch (Exception ex)
            {
                sapi.Logger.Debug("[Synergy] GC diagnostics unavailable: {0}", ex.Message);
            }
        }

        public override void Dispose()
        {
            try
            {
                if (Config?.GcSustainedLowLatencyEnabled == true)
                {
                    GCSettings.LatencyMode = previousLatencyMode;
                }

                NetworkFlushConsolidation.Cleanup();
                EntityLoadBudgeting.Cleanup();
                channelManager?.Dispose();
                deltaHandler?.Dispose();
                SynergyMetrics.Dispose();
                harmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                api?.Logger?.Warning("[Synergy] Error during dispose: {0}", ex.Message);
            }
            finally
            {
                harmony = null;
                Config = null;
                api = null;
                channelManager = null;
                deltaHandler = null;
            }
        }
    }
}
