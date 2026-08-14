// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Port of firmware's RoutingModule::getHopLimitForResponse and NodeDB's
/// getHopsAway: how far an acknowledgement or other reply is allowed to travel.
/// </summary>
public class ReplyHopsTests
{
    private static MeshHeader Header(byte hopLimit, byte hopStart) => new()
    {
        From = 0x11223344u,
        To = 0xAABBCCDDu,
        PacketId = 0x1234u,
        Flags = (byte)((hopLimit & 0x07) | ((hopStart & 0x07) << 5)),
    };

    [Theory]
    // Ordinary traffic: hop_start minus what is left is what it spent.
    [InlineData(3, 3, true, 0)]
    [InlineData(1, 3, true, 2)]
    [InlineData(0, 5, true, 5)]
    // hop_start of 0 with the bitfield present is a genuine zero-hop request.
    [InlineData(0, 0, true, 0)]
    // Without the bitfield the sender is too old to be trusted to fill hop_start
    // in, so the distance is unknowable rather than zero.
    [InlineData(0, 0, false, ReplyHops.Unknown)]
    // More hops remaining than it started with cannot happen honestly.
    [InlineData(5, 3, true, ReplyHops.Unknown)]
    public void HopsUsedMatchesFirmware(byte hopLimit, byte hopStart, bool hasBitfield, int expected)
        => Assert.Equal(expected, ReplyHops.HopsUsed(Header(hopLimit, hopStart), hasBitfield));

    /// <summary>
    /// The gap this closes: a request that asked for zero hops is answered
    /// locally. Answering at the configured limit floods a reply across the
    /// whole mesh on behalf of a request that never left the neighbourhood.
    /// </summary>
    [Fact]
    public void ZeroHopRequestGetsZeroHopReply()
    {
        var direct = Header(hopLimit: 0, hopStart: 0);
        Assert.Equal(0, ReplyHops.ForResponse(direct, hasBitfield: true, configuredHopLimit: 3));
        Assert.Equal(0, ReplyHops.ForResponse(direct, hasBitfield: true, configuredHopLimit: 7));
    }

    /// <summary>Same header from a sender too old to populate hop_start: the
    /// distance is unknown, so the reply falls back to our configured limit
    /// rather than assuming the sender is adjacent.</summary>
    [Fact]
    public void ZeroHopWithoutBitfieldFallsBackToConfigured()
        => Assert.Equal(3, ReplyHops.ForResponse(Header(0, 0), hasBitfield: false, configuredHopLimit: 3));

    [Theory]
    // One hop used, plenty of headroom: spend it plus the two-hop margin.
    [InlineData(2, 3, 5, 3)]
    // Margin would meet or exceed the limit, so just use the limit.
    [InlineData(1, 3, 3, 3)]
    // The request outran our limit; the way back needs at least as much.
    [InlineData(0, 7, 3, 7)]
    public void ResponseHopLimitMatchesFirmware(byte hopLimit, byte hopStart, int configured, int expected)
        => Assert.Equal(expected, ReplyHops.ForResponse(Header(hopLimit, hopStart), hasBitfield: true, configured));

    [Fact]
    public void ConfiguredLimitIsClampedToTheProtocolRange()
    {
        var unknown = Header(hopLimit: 0, hopStart: 0);
        Assert.Equal(7, ReplyHops.ForResponse(unknown, hasBitfield: false, configuredHopLimit: 99));
        Assert.Equal(0, ReplyHops.ForResponse(unknown, hasBitfield: false, configuredHopLimit: -1));
    }
}
