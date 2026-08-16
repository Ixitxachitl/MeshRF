// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// Keeps waypoints in step with a list of records from a REST feed.
/// </summary>
/// <remarks>
/// <para>A different shape from <see cref="MeshScript"/>, and deliberately not
/// expressed in its vocabulary. A script answers an event: something happens,
/// and it reacts. A feed is reconciliation — walk a list, place what is new,
/// resend what changed, retire what has gone. The last of those is the reason
/// this cannot be a script at all: nothing <em>happens</em> when a record stops
/// appearing in a list, so no trigger can represent it. Only something holding
/// the previous list can notice.</para>
/// <para>Expressing that as a rule would have meant giving the script language
/// loops, variables and set arithmetic, which would make every simple script
/// worse to keep one complicated one possible. It gets its own engine instead,
/// and reuses everything that was already general: the parser, the credential
/// store, the HTTP client and the waypoint encoder.</para>
/// </remarks>
public sealed class MeshFeedSync
{
    public bool Enabled { get; init; }

    /// <summary>Name for the list and the log.</summary>
    public string Alias { get; init; } = string.Empty;

    /// <summary>How often the feed is re-read.</summary>
    public TimeSpan Every { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The request. Reuses the script action's model, so credentials,
    /// headers and timeouts behave identically.</summary>
    public ScriptHttpRequest Request { get; init; } = new();

    /// <summary>Path to the array of records. Empty means the response is the
    /// array itself, which is how Watch Duty's geo_events answers.</summary>
    public string ItemsPath { get; init; } = string.Empty;

    /// <summary>Path within a record to its identity. This is what makes an
    /// update an update rather than a second marker, and it has to be stable
    /// for the life of the record.</summary>
    public string IdPath { get; init; } = "id";

    /// <summary>Path to the flag saying the record is still live. Empty means
    /// every record returned counts as live.</summary>
    public string ActivePath { get; init; } = string.Empty;

    public string LatitudePath { get; init; } = string.Empty;
    public string LongitudePath { get; init; } = string.Empty;

    /// <summary>Only mirror records this close to home. Null = no filter.</summary>
    public double? WithinMetres { get; init; }

    /// <summary>
    /// Paths whose values decide whether a record has meaningfully changed.
    /// </summary>
    /// <remarks>
    /// Without this, a feed that stamps every record with a modification time
    /// would look changed on every poll and rebroadcast the lot. Naming the
    /// fields that matter is what keeps a marker off the air until it says
    /// something new.
    /// </remarks>
    public IReadOnlyList<string> WatchPaths { get; init; } = Array.Empty<string>();

    /// <summary>The marker to place. Its name and description are templates
    /// over <c>{item.*}</c>.</summary>
    public ScriptWaypoint Waypoint { get; init; } = new();

    /// <summary>
    /// How long a placed marker lives before it lapses on its own, refreshed on
    /// every poll that still sees the record.
    /// </summary>
    /// <remarks>
    /// Never zero. Firmware only draws a waypoint while <c>expire &gt; now</c>,
    /// so an unexpiring marker is not permanent — it is invisible. A rolling
    /// window is also what makes the in-memory state safe: if this node stops
    /// running, everything it placed lapses by itself rather than outliving the
    /// thing it described.
    /// </remarks>
    public TimeSpan Expires { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>What the sync wants done to one marker.</summary>
public enum FeedSyncActionKind
{
    /// <summary>A record not seen before.</summary>
    Place,
    /// <summary>A record whose watched fields changed.</summary>
    Update,
    /// <summary>A record still present and unchanged, whose marker is being
    /// kept alive before its rolling expiry lapses.</summary>
    Refresh,
    /// <summary>A record that has gone or gone inactive. Sent with an expiry in
    /// the past, which is how firmware is told to drop one.</summary>
    Remove,
}

/// <summary>One marker to send, fully resolved.</summary>
/// <param name="Kind">Why it is being sent.</param>
/// <param name="ItemId">The record's identity in the feed, for the log.</param>
/// <param name="WaypointId">Derived from <paramref name="ItemId"/> and stable
/// across restarts, so a re-send replaces rather than duplicates.</param>
/// <param name="Latitude">Position, or 0 for a removal.</param>
/// <param name="Name">Expanded name.</param>
/// <param name="Description">Expanded description.</param>
/// <param name="ExpireEpoch">Absolute expiry; in the past for a removal.</param>
public sealed record FeedSyncAction(
    FeedSyncActionKind Kind,
    string ItemId,
    uint WaypointId,
    double Latitude,
    double Longitude,
    string Name,
    string Description,
    uint ExpireEpoch)
{
    public bool IsRemoval => Kind == FeedSyncActionKind.Remove;
}
