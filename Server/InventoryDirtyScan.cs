using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Skip full inventory iteration when nothing is dirty.
    /// Uses Volatile.Read/Write for thread-safe dirty flag.
    /// Vanilla behavior preserved: dirty slots still sent at same rate.
    /// </summary>
    public static class InventoryDirtyScan
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;
        private static int anyDirtyFlag;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;
            Volatile.Write(ref anyDirtyFlag, 0);

            var targetType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemInventory");
            if (targetType == null)
            {
                api.Logger.Warning("[Synergy] InventoryScan: Could not find ServerSystemInventory, skipping");
                return;
            }

            var sendDirtySlots = AccessTools.Method(targetType, "SendDirtySlots", new[] { typeof(float) });
            if (sendDirtySlots == null)
            {
                api.Logger.Warning("[Synergy] InventoryScan: Could not find SendDirtySlots method, skipping");
                return;
            }

            if (!ConflictDetector.IsSafeToPatch(sendDirtySlots, SynergyMod.HarmonyId, api.Logger))
                return;

            harmony.Patch(sendDirtySlots,
                prefix: new HarmonyMethod(typeof(InventoryDirtyScan), nameof(Prefix_SendDirtySlots)));

            var markDirty = AccessTools.Method(typeof(InventoryBase), "DidModifyItemSlot",
                new[] { typeof(ItemSlot), typeof(ItemStack) });
            if (markDirty != null)
            {
                harmony.Patch(markDirty,
                    postfix: new HarmonyMethod(typeof(InventoryDirtyScan), nameof(Postfix_DidModifyItemSlot)));
            }

            api.Logger.Notification("[Synergy] InventoryScan: Inventory dirty scan optimization active");
        }

        public static void Postfix_DidModifyItemSlot()
        {
            Volatile.Write(ref anyDirtyFlag, 1);
        }

        public static bool Prefix_SendDirtySlots()
        {
            if (disabled) return true;

            try
            {
                if (Volatile.Read(ref anyDirtyFlag) == 0)
                {
                    return false;
                }
                Volatile.Write(ref anyDirtyFlag, 0);
                return true;
            }
            catch (Exception ex)
            {
                if (++errorCount >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] InventoryScan: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }
    }
}
