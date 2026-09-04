// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The 360° skyline from one antenna, and placing nodes against it.
/// </summary>
public class HorizonPanoramaTests
{
    private static readonly GeoPoint Centre = new(44.9778, -93.2650);

    private static HorizonOptions Options(double antennaM = 10, double radiusM = 15_000) =>
        new(Centre, antennaM, radiusM, Bearings: 72, SamplesPerBearing: 300);

    private sealed class FlatGround(double metres) : IElevationSource
    {
        public double? ElevationAt(double lat, double lon) => metres;
    }

    private sealed class NoGround : IElevationSource
    {
        public double? ElevationAt(double lat, double lon) => null;
    }

    /// <summary>Flat ground with a ridge of a given height at a given range,
    /// across a span of bearings.</summary>
    private sealed class RidgedGround(
        double baseM, double ridgeM, double atRangeM, double thicknessM,
        double fromBearing, double toBearing) : IElevationSource
    {
        public double? ElevationAt(double lat, double lon)
        {
            var here = new GeoPoint(lat, lon);
            if (Math.Abs(Geodesy.DistanceM(Centre, here) - atRangeM) > thicknessM / 2) return baseM;

            double bearing = HorizonPanorama.BearingDeg(Centre, here);
            return bearing >= fromBearing && bearing <= toBearing ? baseM + ridgeM : baseM;
        }
    }

    [Fact]
    public void OverLevelGroundTheSkylineIsTheHorizonAllTheWayRound()
    {
        var profile = HorizonPanorama.Build(new FlatGround(200), Options())!;

        Assert.Equal(72, profile.Points.Count);
        Assert.All(profile.Points, p => Assert.True(p.ElevationAngleDeg < 0,
            $"level ground should sit below horizontal, got {p.ElevationAngleDeg:F3}°"));
        Assert.Equal(0, profile.FractionObstructed);
    }

    [Fact]
    public void TheObserverStandsAtTheGroundPlusTheAntenna()
    {
        var profile = HorizonPanorama.Build(new FlatGround(200), Options(antennaM: 12))!;
        Assert.Equal(212, profile.ObserverElevationM, 6);
    }

    [Fact]
    public void ARidgeStandsAboveHorizontalOnlyWhereItIs()
    {
        // A 200 m ridge a kilometre out, across the eastern quadrant.
        var terrain = new RidgedGround(200, 200, 1000, 300, 60, 120);
        var profile = HorizonPanorama.Build(terrain, Options())!;

        var facing = profile.Points.Where(p => p.BearingDegrees is >= 70 and <= 110);
        var away = profile.Points.Where(p => p.BearingDegrees is < 50 or > 130);

        Assert.All(facing, p => Assert.True(p.ElevationAngleDeg > 0,
            $"the ridge should stand above horizontal at {p.BearingDegrees}°, got {p.ElevationAngleDeg:F2}°"));
        Assert.All(away, p => Assert.True(p.ElevationAngleDeg < 0));
    }

    [Fact]
    public void TheSkylineReportsWhichGroundDefinesIt()
    {
        var terrain = new RidgedGround(200, 200, 1000, 300, 60, 120);
        var profile = HorizonPanorama.Build(terrain, Options())!;

        var highest = profile.Highest;
        Assert.Equal(400, highest.GroundM, 0);
        Assert.InRange(highest.DistanceM, 850, 1150);
        Assert.InRange(highest.BearingDegrees, 60, 120);
    }

    [Fact]
    public void RaisingTheAntennaLowersTheSkyline()
    {
        // The whole point of the tool: what a taller mast buys, in degrees.
        var terrain = new RidgedGround(200, 200, 1000, 300, 60, 120);

        var low = HorizonPanorama.Build(terrain, Options(antennaM: 2))!;
        var high = HorizonPanorama.Build(terrain, Options(antennaM: 40))!;

        Assert.True(high.Highest.ElevationAngleDeg < low.Highest.ElevationAngleDeg,
            $"40 m gave {high.Highest.ElevationAngleDeg:F2}°, 2 m gave {low.Highest.ElevationAngleDeg:F2}°");
    }

    [Fact]
    public void HowMuchOfTheTurnIsObstructedIsReported()
    {
        // A ridge across a sixty-degree span of a full turn: a sixth of it.
        var terrain = new RidgedGround(200, 200, 1000, 300, 60, 120);
        var profile = HorizonPanorama.Build(terrain, Options())!;

        Assert.InRange(profile.FractionObstructed, 0.1, 0.25);
    }

    [Fact]
    public void TheEarthCurvesAwayFasterThanDistanceFlattensARidge()
    {
        // Same ridge, twice as far: further off is lower, and not merely by the
        // ratio of the distances — the curvature drop grows with the square.
        double near = HorizonPanorama.Build(
            new RidgedGround(200, 200, 5000, 600, 60, 120), Options())!.Highest.ElevationAngleDeg;
        double far = HorizonPanorama.Build(
            new RidgedGround(200, 200, 10_000, 600, 60, 120), Options())!.Highest.ElevationAngleDeg;

        Assert.True(far < near / 2,
            $"at 5 km {near:F3}°, at 10 km {far:F3}° — curvature should take more than half");
    }

    [Fact]
    public void GroundLevelWithTheAntennaSitsExactlyOnTheHorizontalBeforeCurvature()
    {
        double angle = HorizonPanorama.ElevationAngleDeg(
            observerM: 200, targetGroundM: 200, distanceM: 1000,
            effectiveEarthRadiusM: double.PositiveInfinity);

        Assert.Equal(0, angle, 6);
    }

    [Fact]
    public void ADistanceOfZeroHasNoDirectionToMeasure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HorizonPanorama.ElevationAngleDeg(200, 300, 0, 8.5e6));
    }

    [Fact]
    public void AnAntennaWhoseOwnGroundIsUnknownGetsNoPanorama()
    {
        Assert.Null(HorizonPanorama.Build(new NoGround(), Options()));
    }

    [Fact]
    public void APanoramaNeedsEnoughBearingsAndSomeRadius()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HorizonPanorama.Build(new FlatGround(200), Options() with { Bearings = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HorizonPanorama.Build(new FlatGround(200), Options() with { RadiusM = 0 }));
    }

    // -- Placing nodes against the skyline ----------------------------------

    private static GeoPoint At(double bearing, double metres) =>
        CoverageMap.Along(Centre, bearing, metres);

    [Fact]
    public void NothingBetweenMeansANodeIsVisible()
    {
        var terrain = new FlatGround(200);
        var profile = HorizonPanorama.Build(terrain, Options())!;

        var placed = HorizonPanorama.Place(
            profile, terrain, [("Open Node", At(270, 3000))], targetAntennaM: 2);

        Assert.Single(placed);
        Assert.True(placed[0].IsVisible);
        Assert.InRange(placed[0].BearingDegrees, 269, 271);
        Assert.Equal(3000, placed[0].DistanceM, 0);
    }

    [Fact]
    public void ANodeBehindARidgeIsHidden()
    {
        // The reading the panorama exists for: this node is not hidden by
        // distance or by power, it is hidden by one ridge.
        var terrain = new RidgedGround(200, 200, 1000, 300, 60, 120);
        var profile = HorizonPanorama.Build(terrain, Options())!;

        var placed = HorizonPanorama.Place(
            profile, terrain, [("Shadowed", At(90, 4000))], targetAntennaM: 2);

        Assert.Single(placed);
        Assert.False(placed[0].IsVisible);
        Assert.True(placed[0].ClearanceDeg < 0);
    }

    [Fact]
    public void ClearanceSaysHowFarShortANodeIs()
    {
        var terrain = new RidgedGround(200, 200, 1000, 300, 60, 120);
        var profile = HorizonPanorama.Build(terrain, Options())!;

        var hidden = HorizonPanorama.Place(
            profile, terrain, [("Shadowed", At(90, 4000))], targetAntennaM: 2)[0];
        var raised = HorizonPanorama.Place(
            profile, terrain, [("Shadowed", At(90, 4000))], targetAntennaM: 200)[0];

        Assert.True(raised.ClearanceDeg > hidden.ClearanceDeg,
            "a taller mast at the far end should close the gap");
    }

    [Fact]
    public void ANodeBeyondTheSweepIsLeftOutRatherThanPlacedAgainstNothing()
    {
        var terrain = new FlatGround(200);
        var profile = HorizonPanorama.Build(terrain, Options(radiusM: 5000))!;

        var placed = HorizonPanorama.Place(
            profile, terrain, [("Distant", At(45, 9000))], targetAntennaM: 2);

        Assert.Empty(placed);
    }

    [Fact]
    public void GroundBeyondANodeDoesNotHideIt()
    {
        // The distinction the whole visibility test turns on. Over open ground
        // the horizon sits higher in the view than a node halfway to it, so
        // comparing against the drawn skyline would call every near neighbour
        // blocked. Only what stands between can hide anything.
        var terrain = new RidgedGround(200, 300, 8000, 800, 60, 120);
        var profile = HorizonPanorama.Build(terrain, Options())!;

        // The ridge at 8 km dominates that direction's skyline...
        Assert.True(profile.Highest.ElevationAngleDeg > 0);

        // ...and a node at 3 km, well inside it, is still in plain sight.
        var placed = HorizonPanorama.Place(
            profile, terrain, [("Nearer Than The Ridge", At(90, 3000))], targetAntennaM: 2);

        Assert.Single(placed);
        Assert.True(placed[0].IsVisible);
    }

    [Fact]
    public void BearingsRunClockwiseFromNorth()
    {
        Assert.Equal(0, HorizonPanorama.BearingDeg(Centre, At(0, 1000)), 1);
        Assert.Equal(90, HorizonPanorama.BearingDeg(Centre, At(90, 1000)), 1);
        Assert.Equal(180, HorizonPanorama.BearingDeg(Centre, At(180, 1000)), 1);
        Assert.Equal(270, HorizonPanorama.BearingDeg(Centre, At(270, 1000)), 1);
    }
}
