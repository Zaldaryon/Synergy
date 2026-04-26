using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// S3: Convert RemoveAttribute() full resyncs into delta updates.
    /// Patches SyncedTreeAttribute.RemoveAttribute to call base.RemoveAttribute(key)
    /// then add the key to attributePathsDirty directly (avoiding MarkPathDirty's listener trigger,
    /// matching vanilla's MarkAllDirty which also does NOT trigger listeners).
    /// Fallback: next full sync (every 5s) corrects any client-side inconsistency.
    /// Vanilla behavior preserved: same attribute data reaches client, just as delta instead of full dump.
    /// </summary>
    public static class AttributeResyncPrevention
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;

        // Cached reflection
        private static MethodInfo baseRemoveMethod;
        private static FieldInfo pathsDirtyField;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            baseRemoveMethod = AccessTools.Method(typeof(TreeAttribute), "RemoveAttribute",
                new[] { typeof(string) });

            // Cache the attributePathsDirty field to add dirty paths directly
            // (avoids MarkPathDirty which triggers modified listeners — vanilla's MarkAllDirty doesn't)
            pathsDirtyField = AccessTools.Field(typeof(SyncedTreeAttribute), "attributePathsDirty");

            var removeAttr = AccessTools.Method(typeof(SyncedTreeAttribute), "RemoveAttribute",
                new[] { typeof(string) });
            if (removeAttr == null)
            {
                api.Logger.Warning("[Synergy] S3: Could not find SyncedTreeAttribute.RemoveAttribute, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(removeAttr, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(removeAttr,
                prefix: new HarmonyMethod(typeof(AttributeResyncPrevention), nameof(Prefix_RemoveAttribute)));

            api.Logger.Notification("[Synergy] S3: Attribute sync delta updates active");
        }

        public static bool Prefix_RemoveAttribute(SyncedTreeAttribute __instance, string key)
        {
            if (disabled) return true;

            try
            {
                // Call base TreeAttribute.RemoveAttribute (removes from dictionary)
                baseRemoveMethod.Invoke(__instance, new object[] { key });

                // Add path to dirty set directly — avoids triggering modified listeners
                // (vanilla's MarkAllDirty also does NOT trigger listeners)
                if (pathsDirtyField != null)
                {
                    var dirtySet = pathsDirtyField.GetValue(__instance) as System.Collections.Generic.HashSet<string>;
                    dirtySet?.Add(key);
                }
                else
                {
                    // Fallback: use MarkPathDirty (triggers listeners, but still better than MarkAllDirty)
                    __instance.MarkPathDirty(key);
                }

                return false;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] S3: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
