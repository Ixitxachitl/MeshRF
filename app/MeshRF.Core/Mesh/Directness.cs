// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;

namespace MeshRF.Mesh;

/// <summary>One hearing of a node, and the geometry it happened at.</summary>
/// <remarks>Both positions are required. Without them there is no way to tell
/// a node that is still where it was from one that has driven away, and
/// crediting the second with the first's path is the whole mistake this exists
/// to avoid.</remarks>
public readonly record struct DirectSighting(
    byte HopsAway,
    DateTimeOffset When,
    float? SnrDb,
    float? RssiDbm,
    GeoPoint Mine,
    GeoPoint Theirs);

/// <summary>
/// The best path a node has actually been heard over, as opposed to how its
/// last packet happened to arrive.
/// </summary>
/// <remarks>
/// <para>Meshtastic stores <c>hops_away</c> from the most recent packet, and
/// the firmware overwrites it every time — see NodeDB's
/// <c>info->hops_away = hopsAway</c>. That answers "how did the last packet
/// get here", which is a property of a packet, not of a node.</para>
/// <para>Every RF question in this app asks something else: is there a direct
/// path between these two positions. Those answers part company whenever a
/// direct path exists but faded for one transmission, so a relayed copy was
/// the only one to land. The nodes that happens to are the marginal ones — far
/// away, weak, intermittent — which are exactly the samples a path-loss fit
/// most needs. Losing them biases the fitted exponent shallow, because what is
/// left is the near, strong neighbours.</para>
/// <para>So the better value is kept alongside the protocol's rather than
/// replacing it, and it is tied to the geometry it was observed at. Move
/// either end and it is discarded outright: seven hops away means seven hops
/// away until we hear otherwise from the new position.</para>
/// </remarks>
public static class Directness
{
    /// <summary>How far either end may move before a past hearing says nothing
    /// about the present one.</summary>
    /// <remarks>Deliberately small. A reset that fires when it need not costs
    /// nothing — the node falls back to the hop count the protocol reports,
    /// which is the honest default — while a reset that fails to fire credits
    /// a node with a path it no longer has. The costs are not symmetric, so
    /// this errs towards forgetting.</remarks>
    public const double GeometryToleranceM = 150;

    /// <summary>How long a hearing stays worth crediting when nothing has
    /// moved.</summary>
    /// <remarks>The firmware's own "is this still a neighbour" window is two
    /// hours, which is right for a routing decision and far too short for
    /// planning — a path that worked this morning still describes the terrain
    /// this evening. A week is long enough to survive a quiet node and short
    /// enough that a changed antenna or a season of leaves works its way out.
    /// Any direct hearing refreshes it, so a real neighbour never ages out.
    /// </remarks>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(7);

    /// <summary>Whether two positions are far enough apart to be different
    /// places.</summary>
    public static bool Moved(GeoPoint was, GeoPoint now) =>
        Geodesy.DistanceM(was, now) > GeometryToleranceM;

    /// <summary>
    /// Which of the stored hearing and a fresh one to keep.
    /// </summary>
    /// <remarks>An equal hop count takes the fresh one rather than keeping the
    /// old: it carries a newer SNR for the same path, and it restarts the
    /// horizon so a neighbour heard direct every day never expires.</remarks>
    public static DirectSighting Reconcile(
        DirectSighting? stored, DirectSighting fresh, TimeSpan? horizon = null)
    {
        if (stored is not { } was) return fresh;

        // Either end somewhere else: the old hearing describes a path that no
        // longer exists, and no part of it is worth carrying over.
        if (Moved(was.Mine, fresh.Mine) || Moved(was.Theirs, fresh.Theirs)) return fresh;

        if (fresh.When - was.When > (horizon ?? Horizon)) return fresh;

        return fresh.HopsAway <= was.HopsAway ? fresh : was;
    }

    /// <summary>Whether a stored hearing is still recent enough to believe,
    /// asked at read time so one that quietly aged out is not reported as
    /// current.</summary>
    public static bool IsFresh(DirectSighting stored, DateTimeOffset now, TimeSpan? horizon = null) =>
        now - stored.When <= (horizon ?? Horizon);

    /// <summary>Whether a node has been heard over a direct path at the
    /// geometry it is at now.</summary>
    public static bool HeardDirect(DirectSighting? stored, DateTimeOffset now, TimeSpan? horizon = null) =>
        stored is { HopsAway: 0 } best && IsFresh(best, now, horizon);
}
