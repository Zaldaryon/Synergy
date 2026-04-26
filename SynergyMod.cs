using System;
using HarmonyLib;
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

        public override bool ShouldLoad(EnumAppSide forSide) => true;

        public override void StartClientSide(ICoreClientAPI capi)
        {
            api = capi;
            Config = SynergyConfig.Load(capi);
            capi.Logger.Notification("[Synergy] Client-side loaded");
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            api = sapi;
            Config ??= SynergyConfig.Load(sapi);
            harmony = new Harmony(HarmonyId);

            sapi.Logger.Notification("[Synergy] Server-side initializing...");

            int count = 0;

            if (Config.InventoryDirtyScanEnabled)
            {
                InventoryDirtyScan.Initialize(sapi, harmony);
                count++;
            }

            if (Config.BlockTickPoolingEnabled)
            {
                BlockTickPooling.Initialize(sapi, harmony);
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

            sapi.Logger.Notification("[Synergy] Server-side loaded ({0} optimizations active)", count);

            var command = new SynergyCommand(sapi, Config);
            command.Register();
        }

        public override void Dispose()
        {
            try
            {
                NetworkFlushConsolidation.Cleanup();
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
            }
        }
    }
}
