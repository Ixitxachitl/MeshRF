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

    /// <summary>
    /// The margin the crossing detector applies on the way out. A node parked
    /// on the boundary reports positions either side of it on GPS noise alone,
    /// and each of those would be a crossing — a chime, and a transmission when
    /// a script watches the fence.
    /// </summary>
    [Fact]
    public void AMarginKeepsAPointJustOutsideTheFenceInside()
    {
        // ~13 m north of a 500 m fence's edge: outside as drawn, inside once
        // the exit margin is applied.
        var wp = Circle(37.5, -122.0, 500);
        Assert.False(Geofence.Contains(wp, 37.50461, -122.0));
        Assert.True(Geofence.Contains(wp, 37.50461, -122.0, Geofence.ExitMarginMetres));

        // Far enough out that the margin does not save it either.
        Assert.False(Geofence.Contains(wp, 37.509, -122.0, Geofence.ExitMarginMetres));
    }

    [Fact]
    public void AMarginGrowsABoundingBoxInBothAxes()
    {
        var wp = Box(west: -122.5, south: 37.0, east: -122.0, north: 37.5);

        // ~11 m north of the top edge, and ~9 m east of the right one.
        Assert.False(Geofence.Contains(wp, 37.5001, -122.25));
        Assert.True(Geofence.Contains(wp, 37.5001, -122.25, Geofence.ExitMarginMetres));
        Assert.False(Geofence.Contains(wp, 37.25, -121.9999));
        Assert.True(Geofence.Contains(wp, 37.25, -121.9999, Geofence.ExitMarginMetres));

        // A degree out is beyond any margin.
        Assert.False(Geofence.Contains(wp, 38.5, -122.25, Geofence.ExitMarginMetres));
    }

    /// <summary>The longitude conversion divides by a cosine that goes to zero
    /// at the poles, so the margin path has to stay finite there.</summary>
    [Fact]
    public void AMarginAtThePoleDoesNotBlowUp()
    {
        var wp = Box(west: -1, south: 89.9, east: 1, north: 90);
        Assert.True(Geofence.Contains(wp, 89.95, 0, Geofence.ExitMarginMetres));
        Assert.False(Geofence.Contains(wp, 0, 0, Geofence.ExitMarginMetres));
    }

    /// <summary>
    /// A rectangular fence drives a crossing exactly like a circular one. The
    /// detector's rule is expressed here against the geometry — enter on the
    /// fence as drawn, leave only once clear of it by the margin — because a
    /// box converts that margin from metres into degrees per axis, which is
    /// the one part of it that is shape-specific.
    /// </summary>
    [Fact]
    public void ABoxFenceEntersAndLeavesUnderTheSameRuleACircleDoes()
    {
        var wp = Box(west: -122.5, south: 37.0, east: -122.0, north: 37.5);

        bool inside = false;
        bool Step(double lat, double lon) =>
            inside = Geofence.Contains(wp, lat, lon, inside ? Geofence.ExitMarginMetres : 0);

        Assert.False(Step(37.6, -122.25));      // well north of it
        Assert.True(Step(37.4, -122.25));       // arrived
        Assert.True(Step(37.5001, -122.25));    // ~11 m past the edge: still in
        Assert.True(Step(37.50015, -122.25));   // ~17 m past: still in
        Assert.False(Step(37.5005, -122.25));   // ~56 m past: gone

        // And the same on the longitude axis, where the conversion depends on
        // latitude rather than being a constant.
        Assert.True(Step(37.25, -122.25));
        Assert.True(Step(37.25, -121.9999));    // ~9 m past the eastern edge
        Assert.False(Step(37.25, -121.9995));   // ~44 m past it
    }

    /// <summary>A box-only waypoint is a fence as far as everything that reads
    /// one is concerned — the crossing detector and the script editor both gate
    /// on HasGeofence, not on there being a radius.</summary>
    [Fact]
    public void ABoxOnlyWaypointCountsAsFenced()
    {
        var wp = Box(west: -122.5, south: 37.0, east: -122.0, north: 37.5);

        Assert.True(wp.HasGeofence);
        Assert.False(wp.HasCircularGeofence);
        Assert.Equal("Box", wp.GeofenceKindText);
    }
}
