using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Convert RemoveAttribute() full resyncs into delta updates.
    /// Patches SyncedTreeAttribute.RemoveAttribute to call base.RemoveAttribute(key)
    /// then add the key to attributePathsDirty directly (avoiding MarkPathDirty's listener trigger,
    /// matching vanilla's MarkAllDirty which also does NOT trigger listeners).
    /// Fallback: next full sync (every 5s) corrects any client-side inconsistency.
    /// Vanilla behavior preserved: same attribute data reaches client, just as delta instead of full dump.
    ///
    /// Uses Harmony reverse patch to call the unpatched base TreeAttribute.RemoveAttribute,
    /// avoiding infinite recursion when other mods trigger RemoveAttribute in their own callbacks.
    /// </summary>
    public static class AttributeResyncPrevention
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;

        private static FieldInfo pathsDirtyField;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            pathsDirtyField = AccessTools.Field(typeof(SyncedTreeAttribute), "attributePathsDirty");

            var removeAttr = AccessTools.Method(typeof(SyncedTreeAttribute), "RemoveAttribute",
                new[] { typeof(string) });
            if (removeAttr == null)
            {
                api.Logger.Warning("[Synergy] AttributeSync: Could not find SyncedTreeAttribute.RemoveAttribute, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(removeAttr, SynergyMod.HarmonyId, api.Logger))
                return;

            // Create reverse patch first — gives us a direct call to the unpatched base method
            var baseRemove = AccessTools.Method(typeof(TreeAttribute), "RemoveAttribute",
                new[] { typeof(string) });
            var reversePatchMethod = AccessTools.Method(typeof(AttributeResyncPrevention),
                nameof(BaseRemoveAttribute));

            try
            {
                harmony.CreateReversePatcher(baseRemove, new HarmonyMethod(reversePatchMethod)).Patch();
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[Synergy] AttributeSync: Reverse patch failed, skipping: {0}", ex.Message);
                return;
            }

            harmony.Patch(removeAttr,
                prefix: new HarmonyMethod(typeof(AttributeResyncPrevention), nameof(Prefix_RemoveAttribute)));

            api.Logger.Notification("[Synergy] AttributeSync: Attribute sync delta updates active");
        }

        /// <summary>
        /// Reverse patch stub — Harmony replaces this body with the original TreeAttribute.RemoveAttribute.
        /// Calls the unpatched base method directly, bypassing all Harmony patches on the virtual override.
        /// </summary>
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(TreeAttribute), "RemoveAttribute", new[] { typeof(string) })]
        public static void BaseRemoveAttribute(TreeAttribute instance, string key)
        {
            // Stub replaced by Harmony at runtime with the original method body
            throw new NotImplementedException("Reverse patch not applied");
        }

        public static bool Prefix_RemoveAttribute(SyncedTreeAttribute __instance, string key)
        {
            if (disabled) return true;

            try
            {
                // Call unpatched base TreeAttribute.RemoveAttribute via reverse patch
                BaseRemoveAttribute(__instance, key);

                // Add path to dirty set directly — avoids triggering modified listeners
                // (vanilla's MarkAllDirty also does NOT trigger listeners)
                if (pathsDirtyField != null)
                {
                    var dirtySet = pathsDirtyField.GetValue(__instance) as HashSet<string>;
                    dirtySet?.Add(key);
                }
                else
                {
                    __instance.MarkPathDirty(key);
                }

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
