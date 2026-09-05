// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Persisting the best path a node has been heard over, and the display that
/// keeps it beside the protocol's own figure rather than replacing it.
/// </summary>
public class NodeDirectnessStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly NodeStore _store;

    private static readonly GeoPoint Home = new(44.9778, -93.2650);
    private static readonly GeoPoint Peer = new(45.0100, -93.2650);

    private static GeoPoint North(GeoPoint start, double metres) =>
        start with { Lat = start.Lat + metres / 111_320.0 };

    public NodeDirectnessStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-directness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new NodeStore(Path.Combine(_dir, "nodes.db"));
        _store.RecordSighting(7, hopsAway: 0);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ADirectHearingSurvivesALaterRelayedOne()
    {
        _store.RecordDirectness(7, 0, -9f, null, Home, Peer);
        _store.RecordDirectness(7, 3, -18f, null, Home, Peer);

        var node = _store.Get(7)!;

        Assert.Equal(0, node.BestPath!.Value.HopsAway);
        Assert.Equal(-9f, node.BestPath!.Value.SnrDb);
        Assert.True(node.HeardDirect(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TheReadingStoredIsTheOneMeasuredOverThatPath()
    {
        // The trap worth a test of its own: crediting the node as direct but
        // then reading the relayed SNR would measure somebody else's hop.
        _store.RecordDirectness(7, 0, -9f, null, Home, Peer);
        _store.RecordDirectness(7, 2, -21f, null, Home, Peer);

        Assert.Equal(-9f, _store.Get(7)!.BestPath!.Value.SnrDb);
    }

    [Fact]
    public void APeerThatMovedStartsAgainFromWhatTheProtocolSays()
    {
        _store.RecordDirectness(7, 0, -9f, null, Home, Peer);
        _store.RecordDirectness(7, 5, -20f, null, Home, North(Peer, 4_000));

        var node = _store.Get(7)!;

        Assert.Equal(5, node.BestPath!.Value.HopsAway);
        Assert.False(node.HeardDirect(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MovingThisStationAlsoStartsAgain()
    {
        _store.RecordDirectness(7, 0, -9f, null, Home, Peer);
        _store.RecordDirectness(7, 6, -20f, null, North(Home, 4_000), Peer);

        Assert.Equal(6, _store.Get(7)!.BestPath!.Value.HopsAway);
    }

    [Fact]
    public void ASightingWithoutBothPositionsChangesNothing()
    {
        _store.RecordDirectness(7, 0, -9f, null, Home, Peer);
        _store.RecordDirectness(7, 4, -20f, null, Home, null);
        _store.RecordDirectness(7, 4, -20f, null, null, Peer);

        // Whether the old hearing still applies is a question about geometry,
        // and a sighting that cannot answer it must not overwrite one that can.
        Assert.Equal(0, _store.Get(7)!.BestPath!.Value.HopsAway);
    }

    [Fact]
    public void ANodeNeverHeardWithPositionsHasNoStoredPath()
    {
        _store.RecordSighting(8, hopsAway: 2);
        Assert.Null(_store.Get(8)!.BestPath);
        Assert.False(_store.Get(8)!.HeardDirect(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TheCellShowsBothFiguresOnlyWhenTheyDisagree()
    {
        _store.RecordDirectness(7, 0, -9f, null, Home, Peer);
        _store.RecordSighting(7, hopsAway: 3);

        var disagreeing = _store.Get(7)!;
        Assert.Equal("3 (0)", disagreeing.HopsDisplay);
        Assert.Contains("has been heard at 0", disagreeing.HopsTip, StringComparison.Ordinal);

        _store.RecordSighting(7, hopsAway: 0);
        Assert.Equal("0", _store.Get(7)!.HopsDisplay);
    }

    [Fact]
    public void ARememberedPathLongerThanTheLastPacketIsNotShown()
    {
        // The bracket reveals a shorter path the protocol's figure is hiding.
        // A longer remembered one reveals nothing — the protocol's figure is
        // already the better of the two, and is what every RF tool uses — so
        // "4 (6)" claimed knowledge of six hops when all it meant was that the
        // shorter path had not been recorded yet.
        _store.RecordDirectness(7, 6, -27f, null, Home, Peer);
        _store.RecordSighting(7, hopsAway: 4);

        var node = _store.Get(7)!;

        Assert.Equal("4", node.HopsDisplay);
        Assert.DoesNotContain("(", node.HopsTip, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEqualRememberedPathIsNotShownEither()
    {
        _store.RecordDirectness(7, 3, -19f, null, Home, Peer);
        _store.RecordSighting(7, hopsAway: 3);

        Assert.Equal("3", _store.Get(7)!.HopsDisplay);
    }

    [Fact]
    public void TheProtocolFigureIsNeverOverwritten()
    {
        // MeshRF must keep agreeing with the radio's own node list.
        _store.RecordDirectness(7, 0, -9f, null, Home, Peer);
        _store.RecordSighting(7, hopsAway: 4);

        Assert.Equal<byte?>(4, _store.Get(7)!.HopsAway);
    }
}
