// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Remembering the best path a node has actually been heard over, and
/// forgetting it the moment either end moves.
/// </summary>
public class DirectnessTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly GeoPoint Home = new(44.9778, -93.2650);
    private static readonly GeoPoint Peer = new(45.0100, -93.2650);

    /// <summary>A point a given number of metres due north.</summary>
    private static GeoPoint North(GeoPoint start, double metres) =>
        start with { Lat = start.Lat + metres / 111_320.0 };

    private static DirectSighting Heard(
        byte hops, DateTimeOffset when, GeoPoint? mine = null, GeoPoint? theirs = null,
        float snr = -8) =>
        new(hops, when, snr, null, mine ?? Home, theirs ?? Peer);

    [Fact]
    public void TheFirstHearingIsTheBestOneSoFar()
    {
        var fresh = Heard(2, Noon);
        Assert.Equal(fresh, Directness.Reconcile(null, fresh));
    }

    [Fact]
    public void ARelayedHearingDoesNotUndoADirectOne()
    {
        // The whole point. A direct path that faded for one transmission is
        // still a direct path, and the protocol's own field would have been
        // overwritten by the relayed copy that did land.
        var direct = Heard(0, Noon);
        var relayed = Heard(3, Noon.AddMinutes(5));

        Assert.Equal(0, Directness.Reconcile(direct, relayed).HopsAway);
    }

    [Fact]
    public void ABetterPathReplacesAWorseOne()
    {
        var relayed = Heard(3, Noon);
        var direct = Heard(0, Noon.AddMinutes(5));

        Assert.Equal(0, Directness.Reconcile(relayed, direct).HopsAway);
    }

    [Fact]
    public void HearingTheSamePathAgainTakesTheNewerReading()
    {
        // Equal hops keeps the fresh one: its SNR describes the same path more
        // recently, and it restarts the horizon so a daily neighbour never
        // ages out.
        var older = Heard(0, Noon, snr: -14);
        var newer = Heard(0, Noon.AddHours(1), snr: -6);

        var kept = Directness.Reconcile(older, newer);

        Assert.Equal(newer.When, kept.When);
        Assert.Equal(-6, kept.SnrDb);
    }

    [Fact]
    public void APeerThatDroveAwayLosesItsDirectCredit()
    {
        // Seven hops away means seven hops away once it has moved, until we
        // hear otherwise from where it is now.
        var direct = Heard(0, Noon);
        var elsewhere = Heard(7, Noon.AddMinutes(10), theirs: North(Peer, 5_000));

        Assert.Equal(7, Directness.Reconcile(direct, elsewhere).HopsAway);
    }

    [Fact]
    public void MovingOurselvesAlsoLosesIt()
    {
        // The geometry is the pair, not the peer. A survey drive moves this
        // end, and a path heard from the last town says nothing here.
        var direct = Heard(0, Noon);
        var drivenAway = Heard(4, Noon.AddMinutes(10), mine: North(Home, 5_000));

        Assert.Equal(4, Directness.Reconcile(direct, drivenAway).HopsAway);
    }

    [Fact]
    public void ShufflingAboutWithinThePositionNoiseIsNotMoving()
    {
        // Reported positions are quantised and jitter a little. A node that
        // has not actually gone anywhere must keep its credit.
        var direct = Heard(0, Noon);
        var nudged = Heard(5, Noon.AddMinutes(10), theirs: North(Peer, 40));

        Assert.Equal(0, Directness.Reconcile(direct, nudged).HopsAway);
    }

    [Fact]
    public void TheToleranceErrsTowardsForgetting()
    {
        // A reset that fires needlessly costs only a fall back to the honest
        // protocol value; one that fails to fire credits a path that is gone.
        Assert.True(Directness.GeometryToleranceM <= 250,
            "a loose tolerance keeps crediting nodes that have genuinely moved");
    }

    [Fact]
    public void AHearingOlderThanTheHorizonStopsCounting()
    {
        var ancient = Heard(0, Noon);
        var now = Heard(4, Noon + Directness.Horizon + TimeSpan.FromHours(1));

        Assert.Equal(4, Directness.Reconcile(ancient, now).HopsAway);
    }

    [Fact]
    public void AHearingInsideTheHorizonStillCounts()
    {
        var recent = Heard(0, Noon);
        var now = Heard(4, Noon + Directness.Horizon - TimeSpan.FromHours(1));

        Assert.Equal(0, Directness.Reconcile(recent, now).HopsAway);
    }

    [Fact]
    public void DirectnessExpiresOnItsOwnWithoutAFurtherHearing()
    {
        // Asked at read time. A node heard direct once and then silent for a
        // month must not still read as a direct neighbour.
        var direct = Heard(0, Noon);

        Assert.True(Directness.HeardDirect(direct, Noon.AddHours(2)));
        Assert.False(Directness.HeardDirect(direct, Noon.AddDays(30)));
    }

    [Fact]
    public void ANodeOnlyEverRelayedIsNotDirectHoweverRecently()
    {
        Assert.False(Directness.HeardDirect(Heard(1, Noon), Noon.AddMinutes(1)));
        Assert.False(Directness.HeardDirect(null, Noon));
    }

    [Fact]
    public void TheHorizonOutlastsTheFirmwaresRoutingWindow()
    {
        // The firmware calls a neighbour stale after two hours, which is right
        // for choosing a next hop and far too short for describing terrain.
        Assert.True(Directness.Horizon > TimeSpan.FromHours(2));
    }
}
