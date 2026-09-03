// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Waypoints;

/// <summary>
/// Containment test for a waypoint's geofence. Lives in Core rather than beside
/// the crossing detector in the app so the geometry can be tested without a
/// node store, a channel tab or a ringtone.
/// </summary>
public static class Geofence
{
    /// <summary>
    /// True when the position falls inside the waypoint's circular radius
    /// and/or its rectangular bounding box. Either shape counts when both are
    /// set, matching the Waypoint proto's "the circular radius and/or the
    /// bounding box" notify semantics.
    /// </summary>
    /// <remarks>
    /// <para>Bounding boxes spanning the antimeridian are not handled: west is
    /// taken as less than east, which holds for any geofence that does not wrap
    /// the globe. A local fence never does.</para>
    /// <para>The fence is tested exactly as drawn, with no margin either way.
    /// Boundary jitter is already dealt with upstream and cannot be dealt with
    /// well here: a node only reports a position once it has moved
    /// (firmware's smart broadcast defaults to 100 m, and this app's own
    /// <c>SmartPositionFilter</c> does the same for the local GPS), so
    /// consecutive fixes are far apart by the time they arrive. A margin on top
    /// of that would not remove a flap that never happens, and would report a
    /// real departure late from a line somebody drew deliberately.</para>
    /// </remarks>
    public static bool Contains(WaypointRecord wp, double lat, double lon)
    {
        if (wp.HasCircularGeofence &&
            HaversineMetres(wp.Latitude, wp.Longitude, lat, lon) <= wp.GeofenceRadius)
            return true;

        if (wp.HasBoundingBoxGeofence &&
            lat >= wp.BboxSouth!.Value && lat <= wp.BboxNorth!.Value &&
            lon >= wp.BboxWest!.Value && lon <= wp.BboxEast!.Value)
            return true;

        return false;
    }

    /// <summary>Great-circle distance in metres. The same spherical
    /// approximation the node-distance filter uses; at mesh ranges the error
    /// against a true ellipsoid is far smaller than the positions' own.</summary>
    public static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6_371_000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusM * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
