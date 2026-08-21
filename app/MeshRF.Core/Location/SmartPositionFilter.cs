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

    /// <summary>Whether a reference fix has been set. The transmit side asks:
    /// with nothing sent yet there is nothing to have moved from, and the
    /// regular interval owns the first send.</summary>
    public bool HasReference => _hasReference;

    /// <summary>Sets the reference directly, for a caller whose "last one
    /// taken" is decided elsewhere — a position that went on the air, say,
    /// which may have been sent on a schedule rather than because it moved.</summary>
    public void Mark(double latitude, double longitude, DateTime utcNow)
    {
        _latitude = latitude;
        _longitude = longitude;
        _takenUtc = utcNow;
        _hasReference = true;
    }

    /// <summary>Forgets the reference fix, so the next one is taken as-is.
    /// Used when the filter is switched on or off and when the GPS is
    /// restarted — whatever is on screen then is not something the new
    /// settings were ever measured against.</summary>
    public void Reset() => _hasReference = false;

    /// <summary>
    /// Whether this fix clears both thresholds, and how far it is from the
    /// reference (zero when there is none yet). Asks without answering for it:
    /// the reference is left alone, for a caller that has something else to do
    /// before it counts as taken — a transmit that might fail, say.
    /// </summary>
    public bool WouldTake(double latitude, double longitude, DateTime utcNow,
                          double minimumMoveMeters, TimeSpan minimumInterval,
                          out double movedMeters)
    {
        movedMeters = _hasReference
            ? Geofence.HaversineMetres(_latitude, _longitude, latitude, longitude)
            : 0.0;

        if (!_hasReference) return true;
        return utcNow - _takenUtc >= minimumInterval && movedMeters >= minimumMoveMeters;
    }

    /// <summary>
    /// The same question, with a taken fix becoming the new reference.
    /// </summary>
    public bool ShouldTake(double latitude, double longitude, DateTime utcNow,
                           double minimumMoveMeters, TimeSpan minimumInterval,
                           out double movedMeters)
    {
        if (!WouldTake(latitude, longitude, utcNow, minimumMoveMeters, minimumInterval, out movedMeters))
            return false;

        Mark(latitude, longitude, utcNow);
        return true;
    }
}
