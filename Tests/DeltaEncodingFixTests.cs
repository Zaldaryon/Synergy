using System.Collections.Concurrent;
using System.Collections.Generic;
using Synergy;
using Synergy.Network;
using Xunit;

namespace Tests;

/// <summary>
/// Automated tests for Issue #1 fix: delta encoding baseline desync causing invisible entities.
/// Tests the pure logic extracted from DeltaPositionHandler (client) and DeltaPositionEncoding (server).
/// No game dependencies — exercises codec, baseline management, and decision logic.
/// </summary>
public class DeltaEncodingFixTests
{
    // --- Fix A: Client skips relative delta when no baseline exists ---

    [Fact]
    public void RelativeDelta_NoBaseline_IsSkipped()
    {
        // Simulate client receiving a relative delta (not absolute, not teleport)
        // with no stored baseline. Fix A: must skip, not treat as absolute.
        var baselines = new Dictionary<long, SynergyChannelManager.EntityBaseline>();
        var delta = new DeltaEntry
        {
            EntityId = 42,
            Flags = 0, // relative (no FlagAbsolute, no FlagTeleport)
            DeltaX = 5, DeltaY = 3, DeltaZ = -2,
            Tick = 10
        };

        bool shouldApply = TryReconstructPosition(delta, baselines, out _, out _, out _);

        Assert.False(shouldApply, "Relative delta with no baseline must be skipped (Fix A)");
    }

    [Fact]
    public void AbsoluteDelta_NoBaseline_IsApplied()
    {
        var baselines = new Dictionary<long, SynergyChannelManager.EntityBaseline>();
        var delta = new DeltaEntry
        {
            EntityId = 42,
            Flags = DeltaCodec.FlagAbsolute,
            DeltaX = 1000000, DeltaY = 500000, DeltaZ = 2000000,
            Tick = 10
        };

        bool shouldApply = TryReconstructPosition(delta, baselines, out long x, out long y, out long z);

        Assert.True(shouldApply, "Absolute delta must always be applied");
        Assert.Equal(1000000, x);
        Assert.Equal(500000, y);
        Assert.Equal(2000000, z);
    }

    [Fact]
    public void AbsoluteDelta_SetsBaseline_ForSubsequentRelative()
    {
        var baselines = new Dictionary<long, SynergyChannelManager.EntityBaseline>();

        // First: absolute seeds baseline
        var abs = new DeltaEntry
        {
            EntityId = 42,
            Flags = DeltaCodec.FlagAbsolute,
            DeltaX = 1000000, DeltaY = 500000, DeltaZ = 2000000,
            Tick = 10
        };
        TryReconstructPosition(abs, baselines, out _, out _, out _);

        // Second: relative uses baseline
        var rel = new DeltaEntry
        {
            EntityId = 42,
            Flags = 0,
            DeltaX = 100, DeltaY = -50, DeltaZ = 200,
            Tick = 11
        };
        bool applied = TryReconstructPosition(rel, baselines, out long x, out long y, out long z);

        Assert.True(applied);
        Assert.Equal(1000100, x);  // 1000000 + 100
        Assert.Equal(499950, y);   // 500000 + (-50)
        Assert.Equal(2000200, z);  // 2000000 + 200
    }

    [Fact]
    public void TeleportDelta_AlwaysApplied_RegardlessOfBaseline()
    {
        var baselines = new Dictionary<long, SynergyChannelManager.EntityBaseline>();
        var delta = new DeltaEntry
        {
            EntityId = 42,
            Flags = DeltaCodec.FlagTeleport,
            DeltaX = 9999999, DeltaY = 8888888, DeltaZ = 7777777,
            Tick = 10
        };

        bool shouldApply = TryReconstructPosition(delta, baselines, out long x, out _, out _);

        Assert.True(shouldApply);
        Assert.Equal(9999999, x);
    }

    // --- Fix B: Server stagger slot forces absolute periodically ---

    [Fact]
    public void StaggerSlot_ForcesAbsolute_OnMatchingTick()
    {
        long entityId = 42;
        int generation = 42; // entityId % 30 == generation % 30 → slot match

        bool slotResync = (entityId % SynergyChannelManager.StaggerSlots)
                          == (generation % SynergyChannelManager.StaggerSlots);

        Assert.True(slotResync, "Entity should resync on its own slot tick");
    }

    [Fact]
    public void StaggerSlot_SkipsAbsolute_OnNonMatchingTick()
    {
        long entityId = 42; // 42 % 30 == 12
        int generation = 43; // 43 % 30 == 13 → no match

        bool slotResync = (entityId % SynergyChannelManager.StaggerSlots)
                          == (generation % SynergyChannelManager.StaggerSlots);

        Assert.False(slotResync, "Entity should NOT resync on a different slot");
    }

    [Fact]
    public void StaggerSlot_EverySingleEntity_ResyncsWithin30Ticks()
    {
        // Every entity (any ID) must hit its slot exactly once per 30-tick cycle
        for (long eid = 0; eid < 100; eid++)
        {
            int hits = 0;
            for (int gen = 0; gen < 30; gen++)
            {
                if ((eid % 30) == (gen % 30)) hits++;
            }
            Assert.Equal(1, hits);
        }
    }

    [Fact]
    public void StaggerSlot_MovingEntity_StillGetsAbsolute()
    {
        // The OLD bug: moving entities never got absolute because Generation was refreshed
        // every tick. The new slot logic is purely (entityId % slots == gen % slots) —
        // independent of whether the entity moved or not.
        long entityId = 7;
        int absoluteCount = 0;

        for (int gen = 0; gen < 60; gen++)
        {
            bool slotResync = (entityId % SynergyChannelManager.StaggerSlots)
                              == (gen % SynergyChannelManager.StaggerSlots);
            if (slotResync) absoluteCount++;
        }

        // 60 ticks = 2 full cycles → exactly 2 absolutes
        Assert.Equal(2, absoluteCount);
    }

    // --- Codec roundtrip (regression guard) ---

    [Fact]
    public void Codec_Roundtrip_PreservesAllFields()
    {
        var input = new DeltaEntry[]
        {
            new()
            {
                EntityId = 12345,
                Flags = DeltaCodec.FlagAbsolute,
                DeltaX = -999999, DeltaY = 888888, DeltaZ = 0,
                DeltaMotionX = 100, DeltaMotionY = -50, DeltaMotionZ = 200,
                Yaw = 1024, Pitch = 512, Roll = 0,
                HeadYaw = 768, HeadPitch = 256, BodyYaw = 2048,
                Controls = 0x210, Tick = 999, PositionVersion = 3, MountControls = 0,
                TagsPart1 = 0xFF, TagsPart2 = 0, TagsPart3 = long.MaxValue, TagsPart4 = 1
            }
        };

        byte[] encoded = DeltaCodec.Encode(input, 0, 1);
        var output = new DeltaEntry[1];
        int count = DeltaCodec.Decode(encoded, output);

        Assert.Equal(1, count);
        var o = output[0];
        Assert.Equal(input[0].EntityId, o.EntityId);
        Assert.Equal(input[0].Flags, o.Flags);
        Assert.Equal(input[0].DeltaX, o.DeltaX);
        Assert.Equal(input[0].DeltaY, o.DeltaY);
        Assert.Equal(input[0].DeltaZ, o.DeltaZ);
        Assert.Equal(input[0].DeltaMotionX, o.DeltaMotionX);
        Assert.Equal(input[0].DeltaMotionY, o.DeltaMotionY);
        Assert.Equal(input[0].DeltaMotionZ, o.DeltaMotionZ);
        Assert.Equal(input[0].Yaw, o.Yaw);
        Assert.Equal(input[0].Pitch, o.Pitch);
        Assert.Equal(input[0].Roll, o.Roll);
        Assert.Equal(input[0].HeadYaw, o.HeadYaw);
        Assert.Equal(input[0].HeadPitch, o.HeadPitch);
        Assert.Equal(input[0].BodyYaw, o.BodyYaw);
        Assert.Equal(input[0].Controls, o.Controls);
        Assert.Equal(input[0].Tick, o.Tick);
        Assert.Equal(input[0].PositionVersion, o.PositionVersion);
        Assert.Equal(input[0].MountControls, o.MountControls);
        Assert.Equal(input[0].TagsPart1, o.TagsPart1);
        Assert.Equal(input[0].TagsPart2, o.TagsPart2);
        Assert.Equal(input[0].TagsPart3, o.TagsPart3);
        Assert.Equal(input[0].TagsPart4, o.TagsPart4);
    }

    [Fact]
    public void Codec_MultipleDeltaEntities_Roundtrip()
    {
        var input = new DeltaEntry[8];
        for (int i = 0; i < 8; i++)
        {
            input[i] = new DeltaEntry
            {
                EntityId = 100 + i,
                Flags = i % 2 == 0 ? DeltaCodec.FlagAbsolute : (byte)0,
                DeltaX = i * 1000, DeltaY = -i * 500, DeltaZ = i * 200,
                Tick = 50 + i
            };
        }

        byte[] encoded = DeltaCodec.Encode(input, 0, 8);
        var output = new DeltaEntry[8];
        int count = DeltaCodec.Decode(encoded, output);

        Assert.Equal(8, count);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(input[i].EntityId, output[i].EntityId);
            Assert.Equal(input[i].Flags, output[i].Flags);
            Assert.Equal(input[i].DeltaX, output[i].DeltaX);
            Assert.Equal(input[i].DeltaY, output[i].DeltaY);
            Assert.Equal(input[i].DeltaZ, output[i].DeltaZ);
            Assert.Equal(input[i].Tick, output[i].Tick);
        }
    }

    // --- Pruning (Fix C) ---

    [Fact]
    public void PruneBaseline_RemovesFromAllClients()
    {
        var clients = new ConcurrentDictionary<string, SynergyChannelManager.ClientState>();
        var state1 = new SynergyChannelManager.ClientState();
        var state2 = new SynergyChannelManager.ClientState();
        state1.Baselines[42] = new SynergyChannelManager.EntityBaseline { X = 1 };
        state1.Baselines[99] = new SynergyChannelManager.EntityBaseline { X = 2 };
        state2.Baselines[42] = new SynergyChannelManager.EntityBaseline { X = 3 };
        clients["p1"] = state1;
        clients["p2"] = state2;

        // Simulate PruneBaseline(42)
        foreach (var kvp in clients)
            kvp.Value.Baselines.TryRemove(42, out _);

        Assert.False(state1.Baselines.ContainsKey(42));
        Assert.True(state1.Baselines.ContainsKey(99)); // unrelated entity untouched
        Assert.False(state2.Baselines.ContainsKey(42));
    }

    // --- Helper: mirrors client-side ApplyDelta logic (Fix A) ---

    private static bool TryReconstructPosition(
        DeltaEntry delta,
        Dictionary<long, SynergyChannelManager.EntityBaseline> baselines,
        out long absX, out long absY, out long absZ)
    {
        bool isAbsolute = (delta.Flags & DeltaCodec.FlagAbsolute) != 0;
        bool teleport = (delta.Flags & DeltaCodec.FlagTeleport) != 0;

        if (isAbsolute || teleport)
        {
            absX = delta.DeltaX;
            absY = delta.DeltaY;
            absZ = delta.DeltaZ;
        }
        else if (baselines.TryGetValue(delta.EntityId, out var baseline))
        {
            absX = baseline.X + delta.DeltaX;
            absY = baseline.Y + delta.DeltaY;
            absZ = baseline.Z + delta.DeltaZ;
        }
        else
        {
            // Fix A: skip — no baseline, relative delta cannot be reconstructed
            absX = absY = absZ = 0;
            return false;
        }

        // Store baseline
        baselines[delta.EntityId] = new SynergyChannelManager.EntityBaseline
        {
            X = absX, Y = absY, Z = absZ
        };
        return true;
    }
}
