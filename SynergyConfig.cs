using System;
using Vintagestory.API.Common;

namespace Synergy
{
    public class SynergyConfig
    {
        // Server-side performance
        public bool BlockTickPoolingEnabled { get; set; } = true;
        public bool NetworkFlushConsolidationEnabled { get; set; } = true;
        public bool InventoryDirtyScanEnabled { get; set; } = true;
        public bool EntityActivationRangeEnabled { get; set; } = true;
        public float EntityActivationRangeBlocks { get; set; } = 48f;
        public bool CollisionFastPathEnabled { get; set; } = true;
        public bool PathfindingOptimizationsEnabled { get; set; } = true;
        public bool PathfindingThrottleEnabled { get; set; } = true;
        public bool RepulseAgentsThrottleEnabled { get; set; } = true;

        // Server-client fluidity
        public bool EntityTrackingHysteresisEnabled { get; set; } = true;
        public bool DistanceBasedSendFrequencyEnabled { get; set; } = true;
        public bool AttributeSyncResyncPreventionEnabled { get; set; } = true;
        public bool EntitySpawnPriorityOrderingEnabled { get; set; } = true;

        public static SynergyConfig Load(ICoreAPI api)
        {
            try
            {
                var config = api.LoadModConfig<SynergyConfig>("Synergy.json");
                if (config != null)
                {
                    api.Logger.Notification("[Synergy] Configuration loaded");
                    return config;
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error("[Synergy] Error loading configuration: {0}", ex.Message);
            }

            var defaultConfig = new SynergyConfig();
            defaultConfig.Save(api);
            api.Logger.Notification("[Synergy] Created default configuration");
            return defaultConfig;
        }

        public void Save(ICoreAPI api)
        {
            try
            {
                api.StoreModConfig(this, "Synergy.json");
            }
            catch (Exception ex)
            {
                api.Logger.Error("[Synergy] Error saving configuration: {0}", ex.Message);
            }
        }
    }
}
