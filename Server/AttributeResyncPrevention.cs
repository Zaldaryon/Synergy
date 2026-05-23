using System;
using HarmonyLib;
using Synergy.Diagnostics;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Prevents unnecessary full resyncs when RemoveAttribute is called on a key that doesn't exist.
    /// Vanilla calls MarkAllDirty() even for no-op removals — this skips that overhead.
    /// When the attribute DOES exist, vanilla must run (full resync via FromBytes triggers
    /// OnModified listeners on the client, e.g. updateMountedState for dismounting).
    /// </summary>
    public static class AttributeResyncPrevention
    {
        private static ICoreServerAPI sapi;
        internal static int errorCount;
        internal static bool disabled;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var removeAttr = AccessTools.Method(typeof(SyncedTreeAttribute), "RemoveAttribute",
                new[] { typeof(string) });
            if (removeAttr == null)
            {
                api.Logger.Warning("[Synergy] AttributeSync: Could not find SyncedTreeAttribute.RemoveAttribute, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(removeAttr, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(removeAttr,
                prefix: new HarmonyMethod(typeof(AttributeResyncPrevention), nameof(Prefix_RemoveAttribute)));

            api.Logger.Notification("[Synergy] AttributeSync: No-op RemoveAttribute skip active");
        }

        public static bool Prefix_RemoveAttribute(SyncedTreeAttribute __instance, string key)
        {
            if (disabled) return true;

            try
            {
                // If the attribute exists, let vanilla handle it — full resync is required.
                // PartialUpdate(path, null) on the client does NOT fire OnModified listeners,
                // but FromBytes() (from full resync) does. Listeners like updateMountedState
                // rely on this to react to attribute removal (e.g. dismounting).
                if (__instance.HasAttribute(key))
                    return true;

                // Attribute doesn't exist — RemoveAttribute is a no-op.
                // Skip vanilla's unnecessary MarkAllDirty() call.
                DiagAttributeSync.OnDeltaUpdate(1000);
                return false;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] AttributeSync: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
