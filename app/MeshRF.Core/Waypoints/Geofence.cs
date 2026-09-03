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
    /// How far past a fence a node has to be before it counts as having left.
    /// </summary>
    /// <remarks>
    /// A node parked on the boundary reports positions that fall either side of
    /// it on GPS noise alone, and every one of those would be a crossing. That
    /// was already worth a chime too many; now that a script can answer a
    /// crossing on the air, it is worth a transmission too many. Applied only
    /// on the way out, so arriving is still reported the moment it happens.
    /// </remarks>
    public const double ExitMarginMetres = 25.0;

    /// <summary>Metres per degree of latitude on the sphere below. Longitude
    /// narrows with latitude and is worked out per point.</summary>
    private const double MetresPerDegreeLatitude = 111_320.0;

    /// <summary>
    /// True when the position falls inside the waypoint's circular radius
    /// and/or its rectangular bounding box. Either shape counts when both are
    /// set, matching the Waypoint proto's "the circular radius and/or the
    /// bounding box" notify semantics.
    /// </summary>
    /// <param name="marginMetres">Grows the fence by this much before testing,
    /// for the hysteresis described on <see cref="ExitMarginMetres"/>. Zero
    /// tests the fence as drawn.</param>
    /// <remarks>
    /// Bounding boxes spanning the antimeridian are not handled: west is taken
    /// as less than east, which holds for any geofence that does not wrap the
    /// globe. A local fence never does.
    /// </remarks>
    public static bool Contains(WaypointRecord wp, double lat, double lon, double marginMetres = 0)
    {
        if (wp.HasCircularGeofence &&
            HaversineMetres(wp.Latitude, wp.Longitude, lat, lon) <= wp.GeofenceRadius + marginMetres)
            return true;

        if (wp.HasBoundingBoxGeofence)
        {
            // Guarded rather than always computed: at a pole the longitude
            // conversion divides by a cosine of zero, and a zero margin has no
            // business going near it.
            double dLat = 0, dLon = 0;
            if (marginMetres > 0)
            {
                dLat = marginMetres / MetresPerDegreeLatitude;
                double metresPerDegreeLon =
                    MetresPerDegreeLatitude * Math.Max(Math.Cos(lat * Math.PI / 180.0), 1e-6);
                dLon = marginMetres / metresPerDegreeLon;
            }

            if (lat >= wp.BboxSouth!.Value - dLat && lat <= wp.BboxNorth!.Value + dLat &&
                lon >= wp.BboxWest!.Value - dLon && lon <= wp.BboxEast!.Value + dLon)
                return true;
        }

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
