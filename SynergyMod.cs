using System;
using System.Runtime;
using HarmonyLib;
using Synergy.Client;
using Synergy.Diagnostics;
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

            // Network channel (required for DeltaEncoding)
            channelManager = new SynergyChannelManager(sapi);
            channelManager.Initialize();

            if (Config.DeltaEncodingEnabled)
            {
                DeltaPositionEncoding.Initialize(sapi, harmony, channelManager);
                count++;
            }

            sapi.Logger.Notification("[Synergy] Server-side loaded ({0} optimizations active)", count);

            RegisterDiagModules(sapi);

            var command = new SynergyCommand(sapi, Config);
            command.Register();
        }

        private static void RegisterDiagModules(ICoreServerAPI api)
        {
            DiagRegistry.Clear();
            DiagRegistry.Initialize(api);
            DiagRegistry.Register(new DiagActivationRange());
            DiagRegistry.Register(new DiagCollisionFastPath());
            DiagRegistry.Register(new DiagNetworkFlush());
            DiagRegistry.Register(new DiagBlockTickPooling());
            DiagRegistry.Register(new DiagInventoryDirtyScan());
            DiagRegistry.Register(new DiagPathfindingPool());
            DiagRegistry.Register(new DiagPathfindingThrottle());
            DiagRegistry.Register(new DiagRepulseThrottle());
            DiagRegistry.Register(new DiagAiBrainThrottle());
            DiagRegistry.Register(new DiagTrackingHysteresis());
            DiagRegistry.Register(new DiagSendFrequency());
            DiagRegistry.Register(new DiagAttributeSync());
            DiagRegistry.Register(new DiagSpawnPriority());
            DiagRegistry.Register(new DiagEntityLoadBudget());
            DiagRegistry.Register(new DiagDeltaEncoding());
            DiagRegistry.Register(new DiagGc());
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
                NetworkFlushConsolidation.Cleanup();
                EntityLoadBudgeting.Cleanup();
                channelManager?.Dispose();
                deltaHandler?.Dispose();
                SynergyMetrics.Dispose();
                DiagRegistry.Clear();
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
