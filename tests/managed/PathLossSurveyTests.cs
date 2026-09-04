// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using MeshRF.Mesh;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Which neighbours a path-loss calibration is allowed to learn from. Every
/// exclusion here is a reading that was taken somewhere other than the path it
/// would be credited to.
/// </summary>
public class PathLossSurveyTests
{
    private static readonly GeoPoint Home = new(44.9778, -93.2650);

    private static readonly PathLossSurveyOptions Options = new(
        Home,
        MyAntennaM: 6, PeerAntennaM: 3,
        MyGainDbi: 2.15, PeerGainDbi: 2.15,
        AssumedPeerTxPowerDbm: 22,
        FrequencyMhz: 906.875,
        BandwidthKhz: 250);

    /// <summary>A neighbour a given number of metres due east, heard directly.
    /// </summary>
    private static NodeRecord Neighbour(uint nodeNum, double metresEast, float snr = -5)
    {
        // A degree of longitude at this latitude, near enough for a fixture.
        double perDegree = Geodesy.DistanceM(Home, Home with { Lon = Home.Lon + 1 });
        return new NodeRecord
        {
            NodeNum = nodeNum,
            LongName = $"Node {nodeNum}",
            HopsAway = 0,
            SnrDb = snr,
            Latitude = Home.Lat,
            Longitude = Home.Lon + metresEast / perDegree,
        };
    }

    private static IReadOnlyList<NodeRecord> Chosen(params NodeRecord[] nodes) =>
        PathLossSurvey.Candidates(nodes, Options, myNodeNum: 0xDEADBEEF);

    [Fact]
    public void ADirectNeighbourWithAPositionAndAReadingCounts()
    {
        Assert.Single(Chosen(Neighbour(1, 2000)));
    }

    [Fact]
    public void ARelayedNodeIsExcluded()
    {
        // Its SNR was measured by whichever radio relayed it, on a path between
        // two other stations.
        var relayed = Neighbour(1, 2000);
        relayed.HopsAway = 2;
        Assert.Empty(Chosen(relayed));
    }

    [Fact]
    public void ANodeWhoseHopCountIsUnknownIsExcluded()
    {
        var unknown = Neighbour(1, 2000);
        unknown.HopsAway = null;
        Assert.Empty(Chosen(unknown));
    }

    [Fact]
    public void ANodeLastHeardOverMqttIsExcluded()
    {
        // Nothing about that reading came from this station's receiver.
        var viaMqtt = Neighbour(1, 2000);
        viaMqtt.SeenViaMqtt = true;
        Assert.Empty(Chosen(viaMqtt));
    }

    [Fact]
    public void ANodeHeardOverTheAirAfterMqttCountsAgain()
    {
        var overTheAir = Neighbour(1, 2000);
        overTheAir.SeenViaMqtt = false;
        Assert.Single(Chosen(overTheAir));
    }

    [Fact]
    public void ANodeWithNoReadingOrNoPositionIsExcluded()
    {
        var noSnr = Neighbour(1, 2000);
        noSnr.SnrDb = null;
        var noPosition = Neighbour(2, 2000);
        noPosition.Latitude = null;

        Assert.Empty(Chosen(noSnr, noPosition));
    }

    [Fact]
    public void OurOwnNodeIsNotOneOfOurNeighbours()
    {
        var self = Neighbour(0xDEADBEEF, 2000);
        Assert.Empty(Chosen(self));
    }

    [Fact]
    public void ANodeInsideThePositionFuzzingRadiusIsExcluded()
    {
        // At 40 m the reported position is mostly rounding, so the range the
        // reading would be credited to is largely invented.
        Assert.Empty(Chosen(Neighbour(1, 40)));
        Assert.Single(Chosen(Neighbour(2, 400)));
    }

    [Fact]
    public void ANodeReportingAnImpossibleRangeIsExcluded()
    {
        Assert.Empty(Chosen(Neighbour(1, 400_000)));
    }

    [Fact]
    public void CandidatesComeBackNearestFirst()
    {
        var chosen = Chosen(Neighbour(1, 9000), Neighbour(2, 500), Neighbour(3, 3000));

        Assert.Equal([2u, 3u, 1u], chosen.Select(n => n.NodeNum));
    }

    [Fact]
    public void AStrongerReadingMeansLessLossOverTheSamePath()
    {
        // The direction of the whole calculation: SNR up, implied loss down,
        // decibel for decibel.
        double noiseFloor = LinkBudget.NoiseFloorDbm(Options.BandwidthKhz);
        double gains = Options.AssumedPeerTxPowerDbm + Options.PeerGainDbi + Options.MyGainDbi;

        double lossAt(double snr) => gains - (snr + noiseFloor);

        Assert.Equal(6.0, lossAt(-12) - lossAt(-6), 6);
    }
}
