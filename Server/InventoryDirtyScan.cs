using System;
using System.Threading;
using HarmonyLib;
using Synergy.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Skip full inventory iteration when nothing is dirty.
    /// Patches MarkSlotDirty (the actual point where dirtySlots.Add occurs) plus
    /// fallback paths that write dirtySlots directly without calling MarkSlotDirty.
    /// Uses Volatile.Read/Write for thread-safe dirty flag.
    /// </summary>
    public static class InventoryDirtyScan
    {
        private static ICoreServerAPI sapi;
        internal static int errorCount;
        internal static bool disabled;
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

            // Primary: MarkSlotDirty(int) — covers most paths that add to dirtySlots
            var markSlotDirty = AccessTools.Method(typeof(InventoryBase), nameof(InventoryBase.MarkSlotDirty),
                new[] { typeof(int) });
            if (markSlotDirty != null)
            {
                harmony.Patch(markSlotDirty,
                    postfix: new HarmonyMethod(typeof(InventoryDirtyScan), nameof(Postfix_SetDirty)));
            }

            // Safety: DidModifyItemSlot paths (some mods call this directly)
            PatchPostfix(harmony, typeof(InventoryBase), "DidModifyItemSlot",
                new[] { typeof(ItemSlot), typeof(ItemStack) });
            PatchPostfix(harmony, typeof(InventoryBase), "DidModifyItemSlot",
                new[] { typeof(ItemSlot) });

            // Safety: DiscardAll writes dirtySlots directly without MarkSlotDirty
            PatchPostfix(harmony, typeof(InventoryBase), "DiscardAll", Type.EmptyTypes);
            PatchPostfix(harmony, AccessTools.TypeByName("Vintagestory.Common.InventoryPlayerBackpacks"),
                "DiscardAll", Type.EmptyTypes);
            PatchPostfix(harmony, AccessTools.TypeByName("Vintagestory.Common.InventoryPlayerBackpacks"),
                "DropAll", null);

            // Safety: CraftingGrid writes dirtySlots.Add() directly without MarkSlotDirty
            var craftGridType = AccessTools.TypeByName("Vintagestory.Common.InventoryCraftingGrid");
            PatchPostfix(harmony, craftGridType, "FindMatchingRecipe", Type.EmptyTypes);
            PatchPostfix(harmony, craftGridType, "ConsumeIngredients", new[] { typeof(ItemSlot) });

            api.Logger.Notification("[Synergy] InventoryScan: Inventory dirty scan optimization active (MarkSlotDirty + fallbacks)");
        }

        private static void PatchPostfix(Harmony harmony, Type type, string method, Type[] args)
        {
            if (type == null) return;
            var m = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            if (m == null) return;
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(InventoryDirtyScan), nameof(Postfix_SetDirty)));
        }

        public static void Postfix_SetDirty() => Volatile.Write(ref anyDirtyFlag, 1);

        public static bool Prefix_SendDirtySlots()
        {
            if (disabled) return true;

            try
            {
                if (Volatile.Read(ref anyDirtyFlag) == 0)
                {
                    DiagInventoryDirtyScan.OnSkipped();
                    return false;
                }
                Volatile.Write(ref anyDirtyFlag, 0);
                DiagInventoryDirtyScan.OnScanned();
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
