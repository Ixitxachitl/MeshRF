// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Waypoints;

namespace MeshRF.Location;

/// <summary>
/// Decides which fixes from a continuously reporting GPS are worth acting on.
/// </summary>
/// <remarks>
/// An NMEA receiver publishes a fix every second or two whether or not it has
/// moved, and consumer receivers wander several metres while sitting still. A
/// fix taken at face value rewrites the stored position and re-renders the map
/// on every one of them, so a stationary dongle produces a marker that twitches
/// in place and a settings file rewritten all day.
///
/// A fix is taken when it is both far enough from the last one taken and long
/// enough after it. The two conditions are deliberately both required: distance
/// alone still fires as fast as the receiver reports once you are moving, and
/// time alone lets jitter through. Distance accumulates against the last fix
/// taken rather than the last one seen, so a slow walk that never clears the
/// threshold in one step still clears it eventually.
/// </remarks>
public sealed class SmartPositionFilter
{
    private double _latitude;
    private double _longitude;
    private DateTime _takenUtc;
    private bool _hasReference;

    /// <summary>Forgets the reference fix, so the next one is taken as-is.
    /// Used when the filter is switched on or off and when the GPS is
    /// restarted — whatever is on screen then is not something the new
    /// settings were ever measured against.</summary>
    public void Reset() => _hasReference = false;

    /// <summary>
    /// Whether to act on this fix, and how far it is from the last one taken
    /// (zero when there is none yet). A taken fix becomes the new reference.
    /// </summary>
    public bool ShouldTake(double latitude, double longitude, DateTime utcNow,
                           double minimumMoveMeters, TimeSpan minimumInterval,
                           out double movedMeters)
    {
        movedMeters = _hasReference
            ? Geofence.HaversineMetres(_latitude, _longitude, latitude, longitude)
            : 0.0;

        if (_hasReference &&
            (utcNow - _takenUtc < minimumInterval || movedMeters < minimumMoveMeters))
            return false;

        _latitude = latitude;
        _longitude = longitude;
        _takenUtc = utcNow;
        _hasReference = true;
        return true;
    }
}
