// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

public class RelayPolicyTests
{
    private const uint Me = 0xAABBCCDDu;
    private const uint Peer = 0x11223344u;
    private const uint Other = 0x55667788u;

    private static MeshHeader Header(uint from = Peer, uint to = 0xFFFFFFFFu,
                                     byte hopLimit = 3, byte hopStart = 3,
                                     byte relayNode = 0, byte nextHop = 0) =>
        new()
        {
            From = from,
            To = to,
            PacketId = 0x1234u,
            Flags = (byte)((hopLimit & 0x07) | ((hopStart & 0x07) << 5)),
            RelayNode = relayNode,
            NextHop = nextHop,
        };

    private static RelayContext Context(string role = "Client", string mode = "All",
                                        bool isLicensed = false, params NodeRecord[] nodes)
    {
        var byNum = nodes.ToDictionary(n => n.NodeNum);
        return new RelayContext(
            role, mode, Me, LoraPreset.LongFast,
            num => byNum.TryGetValue(num, out var n) ? n : null,
            () => nodes,
            isLicensed);
    }

    private static NodeRecord Node(uint num, bool favorite = false, bool? licensed = null) =>
        new() { NodeNum = num, Favorite = favorite, IsLicensed = licensed };

    /// <summary>A stand-in for "we could decrypt this". Licensed operation
    /// coerces the rebroadcast mode to LOCAL_ONLY, which drops undecodable
    /// traffic outright — so the licence tests need a decoded packet to be
    /// testing the licence rule rather than that coercion.</summary>
    private static MeshDecodeResult Decoded() =>
        new() { Port = PortNum.TextMessage, Text = "hi" };

    // ---- Rebroadcast mode ----

    // The UI spells these "LocalOnly" while firmware spells them "LOCAL_ONLY".
    // Comparing them naively let every multi-word mode fall through to ALL.
    [Theory]
    [InlineData("LocalOnly", "LOCAL_ONLY")]
    [InlineData("LOCAL_ONLY", "LOCAL_ONLY")]
    [InlineData("KnownOnly", "KNOWN_ONLY")]
    [InlineData("CorePortnumsOnly", "CORE_PORTNUMS_ONLY")]
    [InlineData("All", "ALL")]
    [InlineData("None", "NONE")]
    public void RebroadcastModeSpellingsAreEquivalent(string configured, string expected) =>
        Assert.Equal(expected, RelayPolicy.EffectiveRebroadcastMode("Client", configured));

    [Fact]
    public void RoutersIgnoreRebroadcastNone()
    {
        Assert.Equal("ALL", RelayPolicy.EffectiveRebroadcastMode("Router", "None"));
        Assert.Equal("ALL", RelayPolicy.EffectiveRebroadcastMode("RouterLate", "None"));
        Assert.Equal("NONE", RelayPolicy.EffectiveRebroadcastMode("Client", "None"));
    }

    [Fact]
    public void SkipDecodingIsRepeaterOnly()
    {
        Assert.Equal("ALL_SKIP_DECODING", RelayPolicy.EffectiveRebroadcastMode("Repeater", "AllSkipDecoding"));
        Assert.Equal("ALL", RelayPolicy.EffectiveRebroadcastMode("Client", "AllSkipDecoding"));
    }

    [Fact]
    public void LicensedForcesLocalOnly() =>
        Assert.Equal("LOCAL_ONLY", RelayPolicy.EffectiveRebroadcastMode("Router", "All", isLicensed: true));

    // ---- Licensed relaying ----

    [Fact]
    public void LicensedNodeWillNotRelayForKnownUnlicensedSender()
    {
        var ctx = Context(isLicensed: true, nodes: Node(Peer, licensed: false));
        Assert.False(RelayPolicy.ShouldRelay(ctx, Header(), Decoded(), senderIgnored: false));
    }

    // Firmware distinguishes NotKnown from NotLicensed, and only refuses the
    // latter — otherwise a licensed node would relay nothing until every peer
    // had advertised itself.
    [Fact]
    public void LicensedNodeStillRelaysForNodesThatNeverSaid()
    {
        var ctx = Context(isLicensed: true, nodes: Node(Peer));
        Assert.True(RelayPolicy.ShouldRelay(ctx, Header(), Decoded(), senderIgnored: false));
    }

    [Fact]
    public void LicensedNodeWillNotRelayToKnownUnlicensedDestination()
    {
        var ctx = Context(isLicensed: true,
            nodes: new[] { Node(Peer, licensed: true), Node(Other, licensed: false) });
        Assert.False(RelayPolicy.ShouldRelay(ctx, Header(to: Other), Decoded(), senderIgnored: false));
    }

    [Fact]
    public void UnlicensedNodeIgnoresPeerLicenceStatus()
    {
        var ctx = Context(nodes: Node(Peer, licensed: false));
        Assert.True(RelayPolicy.ShouldRelay(ctx, Header(), Decoded(), senderIgnored: false));
    }

    // ---- Hop preservation ----

    [Fact]
    public void RouterPreservesHopsBehindAFavourite()
    {
        var favourite = Node(Other, favorite: true);
        var ctx = Context("Router", nodes: favourite);
        // Two hops in, last relayed by the favourite's low byte.
        var header = Header(hopLimit: 1, hopStart: 3, relayNode: (byte)(Other & 0xFF));
        Assert.False(RelayPolicy.ShouldDecrementHopLimit(ctx, header));
    }

    [Fact]
    public void FirstHopAlwaysDecrementsEvenBehindAFavourite()
    {
        var ctx = Context("Router", nodes: Node(Other, favorite: true));
        var header = Header(hopLimit: 3, hopStart: 3, relayNode: (byte)(Other & 0xFF));
        Assert.True(RelayPolicy.ShouldDecrementHopLimit(ctx, header));
    }

    [Fact]
    public void ClientAlwaysDecrements()
    {
        var ctx = Context("Client", nodes: Node(Other, favorite: true));
        var header = Header(hopLimit: 1, hopStart: 3, relayNode: (byte)(Other & 0xFF));
        Assert.True(RelayPolicy.ShouldDecrementHopLimit(ctx, header));
    }

    // ---- Late rebroadcast window ----

    [Fact]
    public void RouterLateClampsButRouterDoesNot()
    {
        Assert.True(RelayPolicy.ShouldClampToLateWindow(Context("RouterLate"), Header()));
        Assert.False(RelayPolicy.ShouldClampToLateWindow(Context("Router"), Header()));
        Assert.False(RelayPolicy.ShouldClampToLateWindow(Context("Client"), Header()));
    }

    [Fact]
    public void ClientBaseClampsOnlyForFavouritedTraffic()
    {
        Assert.True(RelayPolicy.ShouldClampToLateWindow(
            Context("ClientBase", nodes: Node(Peer, favorite: true)), Header()));
        Assert.False(RelayPolicy.ShouldClampToLateWindow(
            Context("ClientBase", nodes: Node(Peer)), Header()));
    }

    [Theory]
    [InlineData(LoraPreset.LongFast)]
    [InlineData(LoraPreset.MediumFast)]
    [InlineData(LoraPreset.ShortFast)]
    public void WorstCaseDelayIsNeverShorterThanTheWeightedOne(LoraPreset preset)
    {
        int worst = RelayPolicy.GetTxDelayMsecWeightedWorst(preset, 0f);
        for (int i = 0; i < 50; i++)
            Assert.True(RelayPolicy.GetTxDelayMsecWeighted(preset, 0f, isRouterRole: false) <= worst);
    }
}
