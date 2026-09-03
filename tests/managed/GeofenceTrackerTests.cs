// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Waypoints;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// When a position report counts as a crossing. The geometry is settled in
/// <see cref="GeofenceTests"/>; what is decided here is what the previous state
/// was taken to be, which is the whole of the difference between reporting an
/// arrival and reporting nothing.
/// </summary>
public class GeofenceTrackerTests
{
    private const uint Peer = 0xa1b2c3d4;

    /// <summary>A 500 m fence at the origin of the examples below.</summary>
    private static WaypointRecord Fence() => new()
    {
        Id = 1,
        Name = "North Gate",
        Latitude = 37.5,
        Longitude = -122.0,
        GeofenceRadius = 500,
        NotifyOnEnter = true,
        NotifyOnExit = true,
    };

    // ~220 m from the centre, and ~1 km from it.
    private const double InsideLat = 37.502, OutsideLat = 37.509;
    private const double Lon = -122.0;

    /// <summary>
    /// The case a greeting depends on: a node nobody here has ever had a
    /// position for, whose first one puts it inside the fence. It cannot be
    /// known whether it crossed anything, but from here it has just appeared
    /// inside, and that is the event worth reporting.
    /// </summary>
    [Fact]
    public void AFirstEverPositionInsideCountsAsEntering()
    {
        var tracker = new GeofenceTracker();

        Assert.Equal(GeofenceCrossing.Entered,
            tracker.Evaluate(Fence(), Peer, InsideLat, Lon, lastKnownLat: null, lastKnownLon: null));

        // And once, not on every report from the same place afterwards.
        Assert.Equal(GeofenceCrossing.None,
            tracker.Evaluate(Fence(), Peer, InsideLat, Lon, InsideLat, Lon));
    }

    [Fact]
    public void AFirstEverPositionOutsideIsNotACrossing()
    {
        var tracker = new GeofenceTracker();

        Assert.Equal(GeofenceCrossing.None,
            tracker.Evaluate(Fence(), Peer, OutsideLat, Lon, lastKnownLat: null, lastKnownLon: null));
    }

    /// <summary>
    /// A node the app has heard from before, sitting inside the fence when the
    /// app starts. Its stored position says it was already there, so the first
    /// report after the restart is not an arrival — this is what stops a
    /// restart greeting everybody currently inside.
    /// </summary>
    [Fact]
    public void ARestartDoesNotReAnnounceNodesAlreadyInside()
    {
        var tracker = new GeofenceTracker();

        Assert.Equal(GeofenceCrossing.None,
            tracker.Evaluate(Fence(), Peer, InsideLat, Lon,
                             lastKnownLat: InsideLat, lastKnownLon: Lon));
    }

    /// <summary>
    /// The other half of that: a node last known to be outside whose first
    /// report after a restart is inside really did enter, and used to be
    /// swallowed along with the forgotten state.
    /// </summary>
    [Fact]
    public void ARestartStillCatchesACrossingItMissedTheStartOf()
    {
        var tracker = new GeofenceTracker();

        Assert.Equal(GeofenceCrossing.Entered,
            tracker.Evaluate(Fence(), Peer, InsideLat, Lon,
                             lastKnownLat: OutsideLat, lastKnownLon: Lon));

        // Symmetrically, for somebody who left while nothing was listening.
        var leaving = new GeofenceTracker();
        Assert.Equal(GeofenceCrossing.Exited,
            leaving.Evaluate(Fence(), Peer, OutsideLat, Lon,
                             lastKnownLat: InsideLat, lastKnownLon: Lon));
    }

    /// <summary>Once a pair is being tracked, the remembered state wins: the
    /// node table is only consulted to seed it.</summary>
    [Fact]
    public void TheRememberedStateOutranksTheNodeTable()
    {
        var tracker = new GeofenceTracker();
        tracker.Evaluate(Fence(), Peer, InsideLat, Lon, null, null);   // now inside

        // A stale "last known outside" does not re-fire the arrival.
        Assert.Equal(GeofenceCrossing.None,
            tracker.Evaluate(Fence(), Peer, InsideLat, Lon,
                             lastKnownLat: OutsideLat, lastKnownLon: Lon));
    }

    [Fact]
    public void AFullRoundTripReportsBothCrossingsOnce()
    {
        var tracker = new GeofenceTracker();
        var fence = Fence();

        Assert.Equal(GeofenceCrossing.Entered, tracker.Evaluate(fence, Peer, InsideLat, Lon, OutsideLat, Lon));
        Assert.Equal(GeofenceCrossing.None, tracker.Evaluate(fence, Peer, 37.5, Lon, InsideLat, Lon));
        Assert.Equal(GeofenceCrossing.Exited, tracker.Evaluate(fence, Peer, OutsideLat, Lon, 37.5, Lon));
        Assert.Equal(GeofenceCrossing.None, tracker.Evaluate(fence, Peer, 37.52, Lon, OutsideLat, Lon));
        Assert.Equal(GeofenceCrossing.Entered, tracker.Evaluate(fence, Peer, InsideLat, Lon, 37.52, Lon));
    }

    /// <summary>Two nodes in one fence, and one node in two fences, are tracked
    /// apart — the key is the pair.</summary>
    [Fact]
    public void EachFenceAndNodePairIsTrackedSeparately()
    {
        var tracker = new GeofenceTracker();
        var gate = Fence();
        var field = new WaypointRecord
        {
            Id = 2, Name = "Back Field", Latitude = 37.5, Longitude = -122.0, GeofenceRadius = 100,
        };

        // Inside the 500 m fence but outside the 100 m one, from one report.
        Assert.Equal(GeofenceCrossing.Entered, tracker.Evaluate(gate, Peer, InsideLat, Lon, null, null));
        Assert.Equal(GeofenceCrossing.None, tracker.Evaluate(field, Peer, InsideLat, Lon, null, null));

        // A second node arriving is its own arrival, not a repeat of the first.
        Assert.Equal(GeofenceCrossing.Entered, tracker.Evaluate(gate, 0x00000042, InsideLat, Lon, null, null));
    }

    /// <summary>A box fence goes through the same state machine as a circle;
    /// nothing here reads a radius.</summary>
    [Fact]
    public void ABoxFenceCrossesTheSameWay()
    {
        var box = new WaypointRecord
        {
            Id = 3, Name = "Yard",
            BboxWest = -122.5, BboxSouth = 37.0, BboxEast = -122.0, BboxNorth = 37.5,
        };
        var tracker = new GeofenceTracker();

        Assert.Equal(GeofenceCrossing.Entered, tracker.Evaluate(box, Peer, 37.25, -122.25, null, null));
        Assert.Equal(GeofenceCrossing.Exited, tracker.Evaluate(box, Peer, 37.75, -122.25, 37.25, -122.25));
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var tracker = new GeofenceTracker();
        tracker.Evaluate(Fence(), Peer, InsideLat, Lon, null, null);

        tracker.Clear();

        // With nothing remembered and nothing on file, it arrives again.
        Assert.Equal(GeofenceCrossing.Entered,
            tracker.Evaluate(Fence(), Peer, InsideLat, Lon, null, null));
    }
}
