// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Waypoints;

/// <summary>Which way a node just crossed a fence, or that it did not.</summary>
public enum GeofenceCrossing
{
    None,
    Entered,
    Exited,
}

/// <summary>
/// Remembers which nodes are inside which fences, and turns a position report
/// into a crossing.
/// </summary>
/// <remarks>
/// Lives here rather than beside the receive path so the rule can be tested
/// without a node store, a channel tab or a radio — the receive path's job is
/// to say who moved where, and this decides whether that was a crossing.
/// </remarks>
public sealed class GeofenceTracker
{
    // Whether a node was last seen inside a given fence, keyed by waypoint and
    // node. Only a change of state is a crossing, so the previous answer has to
    // be remembered; without it every report from inside a fence would alert
    // again.
    private readonly Dictionary<(long WaypointId, uint NodeNum), bool> _inside = new();

    /// <summary>
    /// Files a position and reports the crossing it made, if any.
    /// </summary>
    /// <param name="lastKnownLat">Where the node was understood to be
    /// <em>before</em> this report, from the node table, or null if it has
    /// never reported a position. Read before the table is updated, or it is
    /// just this position again.</param>
    /// <remarks>
    /// <para>With nothing remembered for this pair yet — a fresh start, or a
    /// fence that has only just been drawn — the previous state is worked out
    /// from where the node was already understood to be. That keeps a restart
    /// from swallowing a real crossing: a node last seen outside whose next
    /// report is inside has entered, and it does not matter that the memory of
    /// it went away in between.</para>
    /// <para>A node with no position on file at all counts as outside, so the
    /// first one ever heard from inside a fence is an arrival. It is not
    /// provable that they crossed anything — they may have been sitting there
    /// all along — but from here they have just appeared inside, which is the
    /// event worth reporting and the one a greeting is waiting for.</para>
    /// </remarks>
    public GeofenceCrossing Evaluate(
        WaypointRecord fence, uint nodeNum, double lat, double lon,
        double? lastKnownLat, double? lastKnownLon)
    {
        bool inside = Geofence.Contains(fence, lat, lon);

        var key = (fence.Id, nodeNum);
        if (!_inside.TryGetValue(key, out bool wasInside))
        {
            wasInside = lastKnownLat is { } priorLat && lastKnownLon is { } priorLon
                     && Geofence.Contains(fence, priorLat, priorLon);
        }

        _inside[key] = inside;

        if (inside == wasInside) return GeofenceCrossing.None;
        return inside ? GeofenceCrossing.Entered : GeofenceCrossing.Exited;
    }

    /// <summary>Forgets everything, for a receiver being restarted in place.</summary>
    public void Clear() => _inside.Clear();
}
