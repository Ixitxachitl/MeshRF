// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Sweeping the compass for how far a station reaches, and whether the ground
/// or the link budget is what stopped each direction.
/// </summary>
public class CoverageMapTests
{
    private static readonly GeoPoint Centre = new(44.9778, -93.2650);

    private static CoverageOptions Options(
        PathLossFit? calibration = null, int bearings = 72, double txPowerDbm = 22) =>
        new(Centre,
            MyAntennaM: 10, PeerAntennaM: 2,
            MyGainDbi: 2.15, PeerGainDbi: 2.15,
            TxPowerDbm: txPowerDbm,
            FrequencyMhz: 906.875,
            BandwidthKhz: 250,
            SpreadingFactor: 9,
            Calibration: calibration,
            Bearings: bearings);

    /// <summary>Ground at a fixed height everywhere.</summary>
    private sealed class FlatGround(double metres) : IElevationSource
    {
        public double? ElevationAt(double lat, double lon) => metres;
    }

    /// <summary>Flat ground with a wall of a given height standing at a range
    /// from the centre, across a span of bearings.</summary>
    private sealed class WalledGround(
        double baseM, double wallM, double atRangeM, double thicknessM,
        double fromBearing, double toBearing) : IElevationSource
    {
        public double? ElevationAt(double lat, double lon)
        {
            var here = new GeoPoint(lat, lon);
            double range = Geodesy.DistanceM(Centre, here);
            if (Math.Abs(range - atRangeM) > thicknessM / 2) return baseM;

            double bearing = BearingTo(here);
            return bearing >= fromBearing && bearing <= toBearing ? baseM + wallM : baseM;
        }

        private static double BearingTo(GeoPoint to)
        {
            double lat1 = Centre.Lat * Math.PI / 180, lat2 = to.Lat * Math.PI / 180;
            double dLon = (to.Lon - Centre.Lon) * Math.PI / 180;
            double y = Math.Sin(dLon) * Math.Cos(lat2);
            double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
            return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        }
    }

    /// <summary>Nothing known anywhere, as an unfetched area would be.</summary>
    private sealed class NoGround : IElevationSource
    {
        public double? ElevationAt(double lat, double lon) => null;
    }

    [Fact]
    public void OpenGroundReachesTheSameWayInEveryDirection()
    {
        var ring = CoverageMap.Build(new FlatGround(200), Options())!;

        Assert.Equal(72, ring.Spokes.Count);
        Assert.All(ring.Spokes, s => Assert.Equal(CoverageQuality.Clear, s.Quality));

        double first = ring.Spokes[0].ReachM;
        Assert.All(ring.Spokes, s => Assert.Equal(first, s.ReachM, 1));
    }

    [Fact]
    public void OverOpenGroundEveryDirectionReachesTheReferenceRange()
    {
        var ring = CoverageMap.Build(new FlatGround(200), Options())!;

        // Level ground is the reference, so the sweep should land on it, give
        // or take one step of the walk.
        Assert.All(ring.Spokes, s =>
            Assert.InRange(s.ReachM, ring.UnobstructedRangeM * 0.95, ring.UnobstructedRangeM));
    }

    [Fact]
    public void TheHorizonArrivesLongBeforeTheLinkBudgetRunsOut()
    {
        // At LoRa sensitivity the budget alone reaches hundreds of kilometres.
        // Judging terrain against that number would call open ground blocked,
        // which is why the reference is a walk rather than the budget.
        var options = Options();
        var ring = CoverageMap.Build(new FlatGround(200), options)!;

        Assert.True(ring.UnobstructedRangeM < CoverageMap.BudgetRangeM(options) / 3,
            $"open ground {ring.UnobstructedRangeM:F0} m against a budget of " +
            $"{CoverageMap.BudgetRangeM(options):F0} m");
        Assert.True(ring.UnobstructedRangeM > 1000);
    }

    /// <summary>A station in ordinary clutter, which reaches a few kilometres.
    /// The terrain tests use this rather than free space: a free-space budget
    /// at LoRa sensitivity is so large that it simply absorbs the thirty-odd
    /// decibels a ridge costs, and nothing gets blocked at all.</summary>
    private static readonly PathLossFit Clutter = new(
        Exponent: 3.2, OffsetDb: 0, RmsResidualDb: 3, SampleCount: 8,
        ExponentFitted: true, OffsetFitted: true);

    [Fact]
    public void AWallStopsOnlyTheBearingsBehindIt()
    {
        // A 120 m wall 2 km out, across the eastern quadrant.
        var terrain = new WalledGround(
            baseM: 200, wallM: 120, atRangeM: 2000, thicknessM: 600,
            fromBearing: 60, toBearing: 120);

        var ring = CoverageMap.Build(terrain, Options(Clutter))!;

        var behind = ring.Spokes.Where(s => s.BearingDegrees is >= 70 and <= 110).ToList();
        var elsewhere = ring.Spokes.Where(s => s.BearingDegrees is < 50 or > 130).ToList();

        Assert.NotEmpty(behind);
        Assert.All(behind, s => Assert.NotEqual(CoverageQuality.Clear, s.Quality));
        Assert.All(elsewhere, s => Assert.Equal(CoverageQuality.Clear, s.Quality));
    }

    [Fact]
    public void ADirectionStoppedByTerrainReportsWhereItStopped()
    {
        // The wall spans 1.7 to 2.3 km. A receiver standing on top of it is
        // reachable; anything in the shadow behind it is not, so that far face
        // is where the direction ends.
        var terrain = new WalledGround(200, 120, 2000, 600, 60, 120);
        var ring = CoverageMap.Build(terrain, Options(Clutter))!;

        var stopped = ring.Spokes.First(s => s.BearingDegrees is >= 80 and <= 100);
        Assert.InRange(stopped.ReachM, 1000, 2400);
        Assert.True(ring.UnobstructedRangeM > 3000,
            $"the open-ground reach should be well past the wall, got {ring.UnobstructedRangeM:F0} m");
    }

    [Fact]
    public void AWallCloseInLeavesTheDirectionBlockedRatherThanShortened()
    {
        // Sixty metres of hill three hundred metres away takes the direction
        // out almost entirely, which is a different thing to report than a
        // ridge that merely trims the range.
        var terrain = new WalledGround(200, 60, 300, 120, 80, 100);
        var ring = CoverageMap.Build(terrain, Options(Clutter))!;

        var stopped = ring.Spokes.First(s => s.BearingDegrees is >= 85 and <= 95);
        Assert.Equal(CoverageQuality.Blocked, stopped.Quality);
    }

    [Fact]
    public void ReliefTheReceiverSeesOverCastsNoShadow()
    {
        // The failure to guard against in the other direction: a sweep that
        // treats every rise as an obstruction draws a ring far tighter than
        // the station has. A metre of bank, with a two-metre receiver behind
        // it, is not an obstruction.
        var ring = CoverageMap.Build(
            new WalledGround(200, 1, 1200, 400, 80, 100), Options(Clutter))!;

        Assert.All(ring.Spokes, s => Assert.Equal(CoverageQuality.Clear, s.Quality));
    }

    [Fact]
    public void RaisingAnObstructionNeverLeavesMoreReachBehindIt()
    {
        // Deliberately not "strictly less". Reach is where the shadow begins,
        // and for a receiver two metres up that is just behind a four-metre
        // bank as surely as behind a forty-metre one — both stop at the same
        // place. What must never happen is a taller ridge reading as further
        // reach, which is the shape a sign error in the sight line takes.
        double ReachBehind(double wallM)
        {
            var ring = CoverageMap.Build(
                new WalledGround(200, wallM, 1200, 400, 80, 100), Options(Clutter))!;
            return ring.Spokes.First(s => s.BearingDegrees is >= 85 and <= 95).ReachM;
        }

        double previous = double.MaxValue;
        foreach (double wall in new[] { 2.0, 4.0, 40.0, 120.0, 400.0 })
        {
            double reach = ReachBehind(wall);
            Assert.True(reach <= previous,
                $"a {wall:F0} m wall left {reach:F0} m, more than the shorter one's {previous:F0} m");
            previous = reach;
        }
    }

    [Fact]
    public void ACalibratedStationDrawsASmallerRingThanFreeSpaceDoes()
    {
        // The whole reason the ring is worth having: free space over anywhere
        // with clutter in it promises range the station does not have.
        var free = CoverageMap.Build(new FlatGround(200), Options())!;
        var calibrated = CoverageMap.Build(new FlatGround(200), Options(Clutter))!;

        Assert.True(calibrated.UnobstructedRangeM < free.UnobstructedRangeM / 2,
            $"free {free.UnobstructedRangeM:F0} m against calibrated {calibrated.UnobstructedRangeM:F0} m");
        Assert.True(calibrated.FurthestReachM < free.FurthestReachM);
    }

    [Fact]
    public void TheSweepStopsWhereTheCallerSaysTheEvidenceDoes()
    {
        // The failure this guards against: a model fitted over a mile and a
        // half, carried two hundred miles, drawing a ring across a continent.
        var uncapped = CoverageMap.Build(new FlatGround(200), Options())!;
        var capped = CoverageMap.Build(
            new FlatGround(200), Options() with { MaxCredibleRangeM = 4000 })!;

        Assert.False(uncapped.RangeWasCapped);
        Assert.True(capped.RangeWasCapped);
        Assert.InRange(capped.UnobstructedRangeM, 3800, 4000);
        Assert.True(capped.UnobstructedRangeM < uncapped.UnobstructedRangeM / 10);
    }

    [Fact]
    public void ACapWiderThanTheStationReachesChangesNothing()
    {
        var plain = CoverageMap.Build(new FlatGround(200), Options())!;
        var generous = CoverageMap.Build(
            new FlatGround(200), Options() with { MaxCredibleRangeM = 10_000_000 })!;

        Assert.False(generous.RangeWasCapped);
        Assert.Equal(plain.UnobstructedRangeM, generous.UnobstructedRangeM, 3);
    }

    [Fact]
    public void ACappedRingStillReadsAsClearWhereNothingStoppedIt()
    {
        // The cap moves the reference rather than clipping the drawing, so a
        // direction that ran to it met nothing and says so. Judging against the
        // uncapped reach instead would paint the whole ring as blocked.
        var capped = CoverageMap.Build(
            new FlatGround(200), Options() with { MaxCredibleRangeM = 4000 })!;

        Assert.All(capped.Spokes, s => Assert.Equal(CoverageQuality.Clear, s.Quality));
    }

    [Fact]
    public void AFitThatNeverMeasuredAFalloffIsCarriedLessFarThanOneThatDid()
    {
        var measured = new PathLossFit(3.2, 0, 3, 8, ExponentFitted: true,
                                       OffsetFitted: true, FurthestSampleM: 2000);
        var held = new PathLossFit(2.0, -29.7, 2.4, 4, ExponentFitted: false,
                                   OffsetFitted: true, FurthestSampleM: 2000);

        Assert.True(held.CredibleRangeM < measured.CredibleRangeM);
        Assert.Equal(6000, measured.CredibleRangeM, 3);
        Assert.Equal(3000, held.CredibleRangeM, 3);
    }

    [Fact]
    public void AFitFromNowhereKnownHasNoCredibleRange()
    {
        var unknown = new PathLossFit(3.0, 0, 3, 8, ExponentFitted: true, OffsetFitted: true);
        Assert.Equal(0, unknown.CredibleRangeM);
    }

    [Fact]
    public void MorePowerReachesFurther()
    {
        var low = CoverageMap.Build(new FlatGround(200), Options(txPowerDbm: 14))!;
        var high = CoverageMap.Build(new FlatGround(200), Options(txPowerDbm: 30))!;

        Assert.True(high.FurthestReachM > low.FurthestReachM);
    }

    [Fact]
    public void AStationWhoseOwnGroundIsUnknownGetsNoRing()
    {
        // Every sight line starts at the station, so without its elevation
        // there is nothing to draw rather than something to guess.
        Assert.Null(CoverageMap.Build(new NoGround(), Options()));
    }

    [Fact]
    public void ARingNeedsEnoughBearingsToBeAShape()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoverageMap.Build(new FlatGround(200), Options(bearings: 2)));
    }

    [Fact]
    public void SpokesGoRoundTheCompassOnceInOrder()
    {
        var ring = CoverageMap.Build(new FlatGround(200), Options(bearings: 8))!;

        Assert.Equal([0, 45, 90, 135, 180, 225, 270, 315],
                     ring.Spokes.Select(s => s.BearingDegrees));
    }

    [Fact]
    public void CountingByQualitySumsToEveryBearing()
    {
        var ring = CoverageMap.Build(
            new WalledGround(200, 120, 2000, 600, 60, 120), Options(Clutter))!;

        int total = ring.CountOf(CoverageQuality.Clear)
                  + ring.CountOf(CoverageQuality.Weakened)
                  + ring.CountOf(CoverageQuality.Blocked);
        Assert.Equal(ring.Spokes.Count, total);
    }

    [Fact]
    public void SteppingOutOnABearingCoversTheDistanceAsked()
    {
        foreach (double bearing in new[] { 0.0, 90.0, 180.0, 270.0, 33.0 })
        {
            var at = CoverageMap.Along(Centre, bearing, 5000);
            Assert.Equal(5000, Geodesy.DistanceM(Centre, at), 1);
        }
    }

    [Fact]
    public void NorthIsUpAndEastIsRight()
    {
        Assert.True(CoverageMap.Along(Centre, 0, 5000).Lat > Centre.Lat);
        Assert.True(CoverageMap.Along(Centre, 180, 5000).Lat < Centre.Lat);
        Assert.True(CoverageMap.Along(Centre, 90, 5000).Lon > Centre.Lon);
        Assert.True(CoverageMap.Along(Centre, 270, 5000).Lon < Centre.Lon);
    }
}
