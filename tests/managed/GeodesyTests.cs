// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Distance and great-circle interpolation, which decide where along a link the
/// terrain is read.
/// </summary>
public class GeodesyTests
{
    [Fact]
    public void ADegreeOfLatitudeIsAboutOneHundredAndElevenKilometres()
    {
        double m = Geodesy.DistanceM(new GeoPoint(45, 0), new GeoPoint(46, 0));
        Assert.Equal(111_195.0, m, tolerance: 200.0);
    }

    [Fact]
    public void LongitudeNarrowsTowardsThePoles()
    {
        double atEquator = Geodesy.DistanceM(new GeoPoint(0, 0), new GeoPoint(0, 1));
        double atSixty = Geodesy.DistanceM(new GeoPoint(60, 0), new GeoPoint(60, 1));
        Assert.Equal(atEquator / 2, atSixty, tolerance: 200.0);
    }

    [Fact]
    public void APointIsNoDistanceFromItself()
    {
        Assert.Equal(0, Geodesy.DistanceM(new GeoPoint(41.9, -87.6), new GeoPoint(41.9, -87.6)), 6);
    }

    [Fact]
    public void TheEndsOfTheInterpolationAreTheEndpoints()
    {
        var a = new GeoPoint(37.77, -122.42);
        var b = new GeoPoint(34.05, -118.24);

        Assert.Equal(a, Geodesy.Interpolate(a, b, 0));
        Assert.Equal(b, Geodesy.Interpolate(a, b, 1));
        Assert.Equal(a, Geodesy.Interpolate(a, b, -0.5));
        Assert.Equal(b, Geodesy.Interpolate(a, b, 2));
    }

    [Fact]
    public void TheHalfwayPointIsEquallyFarFromBothEnds()
    {
        var a = new GeoPoint(37.77, -122.42);
        var b = new GeoPoint(34.05, -118.24);
        var mid = Geodesy.Interpolate(a, b, 0.5);

        Assert.Equal(Geodesy.DistanceM(a, mid), Geodesy.DistanceM(mid, b), 3);
    }

    [Fact]
    public void SamplesAreEvenlySpacedAlongThePath()
    {
        var a = new GeoPoint(44.0, -93.0);
        var b = new GeoPoint(44.2, -92.6);
        double whole = Geodesy.DistanceM(a, b);

        for (int i = 1; i < 10; i++)
        {
            var p = Geodesy.Interpolate(a, b, i / 10.0);
            Assert.Equal(whole * i / 10.0, Geodesy.DistanceM(a, p), 1);
        }
    }

    [Fact]
    public void InterpolatingBetweenTwoOfTheSamePointStaysThere()
    {
        var a = new GeoPoint(51.5, -0.12);
        Assert.Equal(a, Geodesy.Interpolate(a, a, 0.5));
    }

    [Fact]
    public void ThePathFollowsTheGreatCircleRatherThanTheCoordinates()
    {
        // Halfway along a high-latitude east-west leg, the great circle runs
        // poleward of the straight line through the coordinates. Averaging the
        // latitudes instead would sample terrain off the path the radio takes.
        var a = new GeoPoint(60.0, -10.0);
        var b = new GeoPoint(60.0, 10.0);
        var mid = Geodesy.Interpolate(a, b, 0.5);

        Assert.True(mid.Lat > 60.0, $"expected the arc to bow poleward, got {mid.Lat}");
        Assert.Equal(0.0, mid.Lon, 6);
    }
}
