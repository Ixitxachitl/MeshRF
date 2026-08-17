// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Waypoints;
using Xunit;

namespace MeshRF.Tests;

public class GeofenceTests
{
    private static WaypointRecord Circle(double lat, double lon, uint radiusM) => new()
    {
        Id = 1,
        Latitude = lat,
        Longitude = lon,
        GeofenceRadius = radiusM,
    };

    private static WaypointRecord Box(double west, double south, double east, double north) => new()
    {
        Id = 2,
        BboxWest = west,
        BboxSouth = south,
        BboxEast = east,
        BboxNorth = north,
    };

    [Fact]
    public void CircleContainsItsOwnCentre()
    {
        var wp = Circle(37.5, -122.0, 500);
        Assert.True(Geofence.Contains(wp, 37.5, -122.0));
    }

    [Fact]
    public void CircleExcludesAPointBeyondTheRadius()
    {
        // ~1 km north of centre, on a 500 m fence.
        var wp = Circle(37.5, -122.0, 500);
        Assert.False(Geofence.Contains(wp, 37.509, -122.0));
    }

    [Fact]
    public void CircleIncludesAPointInsideTheRadius()
    {
        // ~220 m north of centre, on a 500 m fence.
        var wp = Circle(37.5, -122.0, 500);
        Assert.True(Geofence.Contains(wp, 37.502, -122.0));
    }

    [Fact]
    public void BoundingBoxContainsAndExcludes()
    {
        var wp = Box(west: -122.5, south: 37.0, east: -122.0, north: 37.5);
        Assert.True(Geofence.Contains(wp, 37.25, -122.25));   // middle
        Assert.True(Geofence.Contains(wp, 37.0, -122.5));     // corner is inside
        Assert.False(Geofence.Contains(wp, 37.75, -122.25));  // north of it
        Assert.False(Geofence.Contains(wp, 37.25, -121.5));   // east of it
    }

    [Fact]
    public void EitherShapeCountsWhenBothAreSet()
    {
        // The proto's notify semantics are "the circular radius and/or the
        // bounding box", so a point in one but not the other is still inside.
        var wp = Circle(37.5, -122.0, 500);
        wp.BboxWest = -121.0; wp.BboxSouth = 38.0;
        wp.BboxEast = -120.5; wp.BboxNorth = 38.5;

        Assert.True(Geofence.Contains(wp, 37.5, -122.0));    // in the circle only
        Assert.True(Geofence.Contains(wp, 38.25, -120.75));  // in the box only
        Assert.False(Geofence.Contains(wp, 0, 0));           // in neither
    }

    [Fact]
    public void AWaypointWithNoGeofenceContainsNothing()
    {
        var wp = new WaypointRecord { Id = 3, Latitude = 37.5, Longitude = -122.0 };
        Assert.False(wp.HasGeofence);
        Assert.False(Geofence.Contains(wp, 37.5, -122.0));
    }

    [Fact]
    public void HaversineMatchesAKnownSeparation()
    {
        // One degree of latitude is ~111.19 km on a sphere of this radius.
        var m = Geofence.HaversineMetres(37.0, -122.0, 38.0, -122.0);
        Assert.InRange(m, 111_000, 111_400);

        // Distance to self is zero, not NaN — the Atan2/Sqrt form is chosen to
        // stay well-behaved there.
        Assert.Equal(0.0, Geofence.HaversineMetres(37.5, -122.0, 37.5, -122.0), precision: 6);
    }

    [Fact]
    public void HaversineIsSymmetric()
    {
        Assert.Equal(Geofence.HaversineMetres(37.0, -122.0, 37.6, -122.4),
                     Geofence.HaversineMetres(37.6, -122.4, 37.0, -122.0),
                     precision: 6);
    }
}
