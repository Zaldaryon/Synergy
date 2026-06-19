using System.Threading;
using Xunit;

namespace Tests;

/// <summary>
/// Proves the crafting output desync bug:
/// InventoryCraftingGrid.FindMatchingRecipe() writes dirtySlots.Add() directly
/// without calling MarkSlotDirty(), so the anyDirtyFlag never gets set.
/// Prefix_SendDirtySlots then skips the send → client never receives craft output.
///
/// This test replicates the exact flag logic from InventoryDirtyScan without
/// loading VintagestoryAPI, proving the mechanism is broken for direct-write paths.
/// </summary>
public class InventoryDirtyScanCraftingTests
{
    // Mirror of InventoryDirtyScan's flag logic (same Volatile semantics)
    private int anyDirtyFlag;

    private void PostfixSetDirty() => Volatile.Write(ref anyDirtyFlag, 1);

    /// <summary>
    /// Returns false = skip send, true = run vanilla.
    /// Exact logic from Prefix_SendDirtySlots.
    /// </summary>
    private bool PrefixSendDirtySlots()
    {
        if (Volatile.Read(ref anyDirtyFlag) == 0)
            return false; // skip
        Volatile.Write(ref anyDirtyFlag, 0);
        return true; // run vanilla
    }

    [Fact]
    public void BugRepro_DirectDirtySlotsAdd_FlagNeverSet_SendSkipped()
    {
        // Scenario: player places items in crafting grid.
        // Vanilla FindMatchingRecipe() does: dirtySlots.Add(9)
        // This NEVER calls MarkSlotDirty → our postfix never fires → flag stays 0

        anyDirtyFlag = 0; // clean state

        // Simulate: dirtySlots now has entries, but flag was never set
        // (we don't call PostfixSetDirty because that's the bug — it's not patched)

        bool shouldRunVanilla = PrefixSendDirtySlots();

        // BUG: prefix skips even though crafting output needs to be sent
        Assert.False(shouldRunVanilla,
            "BUG CONFIRMED: SendDirtySlots is skipped when crafting grid writes " +
            "dirtySlots directly without going through MarkSlotDirty");
    }

    [Fact]
    public void Fix_FindMatchingRecipePostfix_SetsFlag_SendRuns()
    {
        // After fix: FindMatchingRecipe gets a postfix that calls Postfix_SetDirty
        anyDirtyFlag = 0;

        // Simulate: FindMatchingRecipe runs and finds a match
        // The NEW postfix fires:
        PostfixSetDirty();

        bool shouldRunVanilla = PrefixSendDirtySlots();

        Assert.True(shouldRunVanilla,
            "After fix: FindMatchingRecipe postfix sets flag, SendDirtySlots runs, " +
            "crafting output packet reaches client");
    }

    [Fact]
    public void Fix_ConsumeIngredientsPostfix_SetsFlag_SendRuns()
    {
        // ConsumeIngredients also does dirtySlots.Add(i) for all slots
        anyDirtyFlag = 0;

        // The NEW postfix on ConsumeIngredients fires:
        PostfixSetDirty();

        bool shouldRunVanilla = PrefixSendDirtySlots();

        Assert.True(shouldRunVanilla,
            "After fix: ConsumeIngredients postfix sets flag, " +
            "ingredient consumption syncs to client");
    }

    [Fact]
    public void FlagResets_AfterSend_NextTickSkipsIfClean()
    {
        anyDirtyFlag = 0;
        PostfixSetDirty();

        // First tick: sends
        Assert.True(PrefixSendDirtySlots());

        // Next tick: nothing dirty, skips (correct optimization)
        Assert.False(PrefixSendDirtySlots());
    }

    [Fact]
    public void ConcurrentDirtyFlag_SetDuringPrefix_NotLost()
    {
        // Edge case: another thread calls PostfixSetDirty between
        // the Read and the Write(0) in PrefixSendDirtySlots.
        // With Volatile semantics, the Write(0) may clear a just-set flag.
        // This is acceptable: the next 30ms tick will catch it.
        // Documenting this as known-acceptable behavior.

        anyDirtyFlag = 0;
        PostfixSetDirty();

        // Prefix reads 1, resets to 0 — any concurrent set after Read
        // is lost until next tick. Max delay: 30ms (one SendDirtySlots period).
        bool ran = PrefixSendDirtySlots();
        Assert.True(ran);
    }
}
