using System.Collections.Generic;
using Synergy;
using Synergy.Client;
using Synergy.Network;
using Xunit;
using Track = Synergy.SynergyChannelManager.EntityTrack;

namespace Tests;

/// <summary>
/// Tests for the ack-based delta protocol (Quake 3 / Source / Gaffer model): the server delta-encodes
/// each entity against the value the client last ACKnowledged, keeps a ring of un-acked sends so an ack
/// can promote the exact acked value, and resends until acked. A lost UDP packet is therefore never a
/// permanent desync. These exercise the pure codec + EntityTrack logic — no game dependencies.
/// </summary>
public class DeltaEncodingFixTests
{
    // --- Codec roundtrip (with snapshot generation + per-entry base reference) ---

    [Fact]
    public void Codec_AbsoluteEntry_RoundtripsWithGeneration()
    {
        const int gen = 123456;
        var input = new[]
        {
            new DeltaEntry
            {
                EntityId = 12345, Flags = DeltaCodec.FlagAbsolute,
                DeltaX = -999999, DeltaY = 888888, DeltaZ = 0,
                DeltaMotionX = 100, DeltaMotionY = -50, DeltaMotionZ = 200,
                Yaw = 1024, Pitch = 512, Roll = 0, HeadYaw = 768, HeadPitch = 256, BodyYaw = 2048,
                Controls = 0x210, Tick = 999, PositionVersion = 3, MountControls = 0,
                TagsPart1 = 0xFF, TagsPart2 = 0, TagsPart3 = long.MaxValue, TagsPart4 = 1
            }
        };

        byte[] encoded = DeltaCodec.Encode(input, 0, 1, gen);
        var output = new DeltaEntry[1];
        int count = DeltaCodec.Decode(encoded, output, out int decodedGen);

        Assert.Equal(1, count);
        Assert.Equal(gen, decodedGen);
        var o = output[0];
        Assert.Equal(input[0].EntityId, o.EntityId);
        Assert.Equal(input[0].Flags, o.Flags);
        Assert.Equal(input[0].DeltaX, o.DeltaX);
        Assert.Equal(input[0].DeltaY, o.DeltaY);
        Assert.Equal(input[0].DeltaZ, o.DeltaZ);
        Assert.Equal(input[0].DeltaMotionX, o.DeltaMotionX);
        Assert.Equal(input[0].DeltaMotionZ, o.DeltaMotionZ);
        Assert.Equal(input[0].Yaw, o.Yaw);
        Assert.Equal(input[0].BodyYaw, o.BodyYaw);
        Assert.Equal(input[0].Controls, o.Controls);
        Assert.Equal(input[0].Tick, o.Tick);
        Assert.Equal(input[0].TagsPart3, o.TagsPart3);
        Assert.Equal(gen, o.BaseGen); // absolute => base is this generation
    }

    [Fact]
    public void Codec_RelativeEntry_PreservesBaseGen()
    {
        const int gen = 5000;
        var input = new[]
        {
            new DeltaEntry { EntityId = 7, Flags = 0, BaseGen = gen - 42, DeltaX = 13, DeltaY = -4, DeltaZ = 9 }
        };

        byte[] encoded = DeltaCodec.Encode(input, 0, 1, gen);
        var output = new DeltaEntry[1];
        DeltaCodec.Decode(encoded, output, out int decodedGen);

        Assert.Equal(gen, decodedGen);
        Assert.Equal(gen - 42, output[0].BaseGen);
        Assert.Equal(13, output[0].DeltaX);
        Assert.Equal(-4, output[0].DeltaY);
    }

    [Fact]
    public void Codec_UnknownWireVersion_DecodesNothing()
    {
        // A packet from an incompatible (e.g. v1) server must be skipped, not misparsed.
        var bogus = new byte[] { 99, 1, 2, 3, 4, 5 };
        int count = DeltaCodec.Decode(bogus, new DeltaEntry[4], out int gen);
        Assert.Equal(0, count);
        Assert.Equal(0, gen);
    }

    [Fact]
    public void Codec_MixedBatch_Roundtrips()
    {
        const int gen = 9;
        var input = new DeltaEntry[6];
        for (int i = 0; i < 6; i++)
            input[i] = new DeltaEntry
            {
                EntityId = 100 + i,
                Flags = i % 2 == 0 ? DeltaCodec.FlagAbsolute : (byte)0,
                BaseGen = i % 2 == 0 ? gen : gen - i,
                DeltaX = i * 1000, DeltaY = -i * 500, DeltaZ = i * 200
            };

        byte[] encoded = DeltaCodec.Encode(input, 0, 6, gen);
        var output = new DeltaEntry[6];
        int count = DeltaCodec.Decode(encoded, output, out _);

        Assert.Equal(6, count);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(input[i].EntityId, output[i].EntityId);
            Assert.Equal(input[i].Flags, output[i].Flags);
            Assert.Equal(input[i].DeltaX, output[i].DeltaX);
            Assert.Equal(i % 2 == 0 ? gen : gen - i, output[i].BaseGen);
        }
    }

    // --- EntityTrack: ack promotion (the heart of the zero-desync guarantee) ---

    [Fact]
    public void Track_Promote_AdvancesBaselineToAckedValue()
    {
        var t = new Track();
        t.RecordSend(1, 10, 0, 0, 0, 0, 0);
        t.RecordSend(2, 20, 0, 0, 0, 0, 0);
        t.RecordSend(3, 30, 0, 0, 0, 0, 0);

        t.Promote(2);

        Assert.True(t.HasAcked);
        Assert.Equal(2, t.AckedGen);
        Assert.Equal(20, t.X);           // baseline == value the client held at gen 2
        Assert.True(t.HasPending);       // gen 3 still un-acked

        t.Promote(3);
        Assert.Equal(3, t.AckedGen);
        Assert.Equal(30, t.X);
        Assert.False(t.HasPending);      // fully synced
    }

    [Fact]
    public void Track_Promote_StaleAck_IsNoOp()
    {
        var t = new Track();
        t.RecordSend(1, 10, 0, 0, 0, 0, 0);
        t.RecordSend(2, 20, 0, 0, 0, 0, 0);
        t.RecordSend(3, 30, 0, 0, 0, 0, 0);
        t.Promote(2);

        t.Promote(1); // older ack — cumulative, must not regress

        Assert.Equal(2, t.AckedGen);
        Assert.Equal(20, t.X);
    }

    [Fact]
    public void Track_AckedEquals_DrivesStationarySuppression()
    {
        var t = new Track();
        t.RecordSend(1, 42, 7, 3, 0, 0, 0);
        t.Promote(1);

        Assert.True(t.AckedEquals(42, 7, 3, 0, 0, 0));   // client has it => server sends nothing
        Assert.False(t.AckedEquals(42, 7, 4, 0, 0, 0));  // moved => must send
    }

    [Fact]
    public void Track_LatestEquals_PreventsResendRingGrowth()
    {
        var t = new Track();
        t.RecordSend(1, 5, 0, 0, 0, 0, 0);
        // A stationary resend carries the same value — must be recognised as already-latest.
        Assert.True(t.LatestEquals(5, 0, 0, 0, 0, 0));
        Assert.False(t.LatestEquals(6, 0, 0, 0, 0, 0));
    }

    // --- End-to-end: a dropped UDP packet must NOT cause permanent desync ---

    [Fact]
    public void LostPacket_SelfHeals_NoPermanentDesync()
    {
        var server = new Track();
        var client = new ClientSim();

        // gen 1: no acked base yet -> absolute. Client receives, applies, acks 1.
        var e1 = ServerBuild(server, gen: 1, x: 1000);
        client.Apply(e1, 1);
        server.Promote(1);

        // gen 2: relative vs acked base (gen 1). *** packet LOST — client never sees it ***
        ServerBuild(server, gen: 2, x: 2000);
        // (no client.Apply, no ack for gen 2)

        // gen 3: still acked at gen 1, so the server re-encodes cumulatively vs gen 1.
        var e3 = ServerBuild(server, gen: 3, x: 3000);
        client.Apply(e3, 3);
        server.Promote(3);

        // Despite losing gen 2, the client reconstructs the correct current position.
        Assert.Equal(3000, client.X);
        Assert.Equal(3, server.AckedGen);
        Assert.False(server.HasPending); // converged
    }

    [Fact]
    public void StationaryAfterLoss_ResendReachesClient()
    {
        // The reported bug: entity moves, its last delta is lost, then it stops (no more vanilla
        // packets). The resend path keeps re-sending the cumulative delta until the client acks.
        var server = new Track();
        var client = new ClientSim();

        var e1 = ServerBuild(server, 1, 1000);
        client.Apply(e1, 1);
        server.Promote(1);

        // Entity falls to its final spot at gen 2 — but that packet is LOST. Then it goes stationary.
        ServerBuild(server, 2, 1500); // lost

        // Server resends (gen 3) because the track is still pending; client receives this one.
        Assert.True(server.HasPending);
        var resend = ServerBuild(server, 3, 1500); // same value (stationary) — cumulative vs gen 1
        client.Apply(resend, 3);
        server.Promote(3);

        Assert.Equal(1500, client.X); // visible at the correct ground position
        Assert.False(server.HasPending);
    }

    [Fact]
    public void BurstLossThenMove_NoResidualDesync()
    {
        // Pathological trace: a value change is lost in a burst, the client recovers the value via a
        // later resend (against an OLD base it still holds), then the entity moves again. If Promote
        // advances AckedGen to the server's *sample* generation (which the client never received)
        // instead of the *acked* generation, the next delta reconstructs against the wrong base and
        // the entity stays permanently offset. AckedGen must be the generation the client acked.
        var server = new Track();
        var client = new ClientSim();

        var e100 = ServerBuild(server, 100, 100);
        client.Apply(e100, 100);
        server.Promote(100);

        // g101: E -> 200, packet LOST; then E goes stationary at 200.
        ServerBuild(server, 101, 200); // never reaches client, never acked

        // g106: a resend reaches the client; it reconstructs 200 via the still-held gen-100 base.
        var e106 = ServerBuild(server, 106, 200);
        client.Apply(e106, 106);
        Assert.Equal(200, client.X);   // recovered
        server.Promote(106);

        // g120: E moves to 300 — must reconstruct correctly against a base the client actually holds.
        var e120 = ServerBuild(server, 120, 300);
        client.Apply(e120, 120);

        Assert.Equal(300, client.X);
    }

    [Fact]
    public void ClientAck_DoesNotAdvance_WhenAnyEntityDeltaWasNotApplied()
    {
        var server = new Track();
        var client = new ClientSim();

        var e1 = ServerBuild(server, 1, 100);
        client.Apply(e1, 1);
        server.Promote(1);

        // gen 2 reaches the client transport, but this entity's delta is not safe to ack
        // because the client could not apply it (missing entity, missing baseline, or exception).
        ServerBuild(server, 2, 200);

        int ack = DeltaPositionHandler.ShouldAdvanceAck(decodedCount: 1, ackSafeCount: 0) ? 2 : 1;
        server.Promote(ack);

        Assert.Equal(1, server.AckedGen);
        Assert.True(server.HasPending);

        var resend = ServerBuild(server, 3, 200);
        client.Apply(resend, 3);
        server.Promote(3);

        Assert.Equal(200, client.X);
        Assert.False(server.HasPending);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(2, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 2, true)]
    public void ClientAck_AdvancesOnlyForFullyAckSafeBatch(int decodedCount, int ackSafeCount, bool expected)
    {
        Assert.Equal(expected, DeltaPositionHandler.ShouldAdvanceAck(decodedCount, ackSafeCount));
    }

    [Theory]
    [InlineData(-1, 40, 1)]
    [InlineData(40, 41, 1)]
    [InlineData(40, 43, 3)]
    [InlineData(40, 80, 5)]
    public void ClientInterpolation_UsesVanillaPacketTickDiff(int previousTick, int packetTick, int expected)
    {
        Assert.Equal(expected, DeltaPositionHandler.CalculateTickDiff(previousTick, packetTick));
    }

    // --- Helpers mirroring production server-build + client-reconstruct logic ---

    /// <summary>Mirrors DeltaPositionEncoding.BuildEntryLocked for the X axis (pure logic).</summary>
    private static DeltaEntry ServerBuild(Track t, int gen, long x)
    {
        bool absolute = !t.HasAcked || (gen - t.AckedGen) > SynergyChannelManager.MaxBaseAge;
        var e = new DeltaEntry { EntityId = 1, Flags = absolute ? DeltaCodec.FlagAbsolute : (byte)0 };
        if (absolute) { e.DeltaX = x; e.BaseGen = gen; e.Flags = DeltaCodec.FlagAbsolute; }
        else { e.DeltaX = x - t.X; e.BaseGen = t.AckedGen; }
        if (!t.LatestEquals(x, 0, 0, 0, 0, 0)) t.RecordSend(gen, x, 0, 0, 0, 0, 0);
        return e;
    }

    /// <summary>Mirrors DeltaPositionHandler reconstruction: ring lookup by BaseGen, apply delta.</summary>
    private sealed class ClientSim
    {
        private readonly Dictionary<int, long> ring = new();
        public long X;

        public void Apply(DeltaEntry e, int gen)
        {
            bool absolute = (e.Flags & DeltaCodec.FlagAbsolute) != 0 || (e.Flags & DeltaCodec.FlagTeleport) != 0;
            long abs;
            if (absolute) abs = e.DeltaX;
            else
            {
                // largest stored gen <= BaseGen
                long baseVal = 0; int bestGen = int.MinValue;
                foreach (var kv in ring)
                    if (kv.Key <= e.BaseGen && kv.Key > bestGen) { bestGen = kv.Key; baseVal = kv.Value; }
                Assert.True(bestGen != int.MinValue, "client must hold the baseline the server encoded against");
                abs = baseVal + e.DeltaX;
            }
            ring[gen] = abs;
            X = abs;
        }
    }
}
