// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The questions asked before something is removed from a list.
/// </summary>
/// <remarks>
/// Shared rather than written at each entry point because the same delete is
/// reachable from more than one place — a grid's button, its Delete key, and a
/// marker's context menu on the map — and a warning that only some of them
/// carry is worse than none: it teaches that no warning means nothing to warn
/// about.
/// </remarks>
internal static class DeleteConfirm
{
    /// <summary>Asks before deleting waypoints, saying which of them this node
    /// cannot retire on the mesh.</summary>
    /// <returns>Whether to go ahead.</returns>
    public static async Task<bool> WaypointsAsync(
        Window owner, RadioViewModel viewModel, IReadOnlyList<WaypointRecord> waypoints)
    {
        if (waypoints.Count == 0) return false;

        string message = waypoints.Count == 1
            ? $"Delete waypoint \"{waypoints[0].DisplayName}\"?"
            : $"Delete {waypoints.Count} waypoints?";

        // A delete leaves as a past-dated expiry, which is what clears the
        // marker on everyone else's map. When there is nothing to send that
        // with, this is the only chance to say so: once the record is gone
        // from here there is nothing left to send it from either.
        var silent = viewModel.SilentDeletions(waypoints);
        message += SilentWarning(silent.OffAir.Count, waypoints.Count,
                                 "This node is not transmitting");
        message += SilentWarning(silent.Unchannelled.Count, waypoints.Count,
                                 "There is no enabled channel left to send the expiry on");

        message += "\n\nThis cannot be undone.";

        return await ConfirmDialog.ConfirmAsync(
            owner, waypoints.Count == 1 ? "Delete waypoint" : "Delete waypoints", message);
    }

    /// <summary>Asks before deleting nodes.</summary>
    /// <returns>Whether to go ahead.</returns>
    public static async Task<bool> NodesAsync(Window owner, IReadOnlyList<NodeRecord> nodes)
    {
        if (nodes.Count == 0) return false;

        string message = nodes.Count == 1
            ? $"Delete node \"{Label(nodes[0])}\"?\n\nThis removes it from the node list, " +
              "along with its position and telemetry history. It cannot be undone."
            : $"Delete {nodes.Count} nodes?\n\nThis removes them from the node list, " +
              "along with their position and telemetry history. It cannot be undone.";

        // A node is only ever forgotten, never told to go away, so one still on
        // the air comes back on its next packet — with an empty history, which
        // is the part that does not come back.
        message += nodes.Count == 1
            ? "\n\nIt reappears if it transmits again, without the history."
            : "\n\nAny of them still transmitting reappear, without their history.";

        return await ConfirmDialog.ConfirmAsync(
            owner, nodes.Count == 1 ? "Delete node" : "Delete nodes", message);
    }

    public static string Label(NodeRecord node) =>
        string.IsNullOrEmpty(node.LongName) ? node.DisplayId : node.LongName;

    /// <summary>
    /// A sentence naming how many of the selection go quietly, or nothing when
    /// none of them do.
    /// </summary>
    /// <param name="silent">How many will not be announced.</param>
    /// <param name="selected">How many are being deleted in all.</param>
    /// <param name="cause">What stops the announcement, as a clause that can
    /// start a sentence.</param>
    private static string SilentWarning(int silent, int selected, string cause)
    {
        if (silent == 0) return string.Empty;

        // A pronoun only reads right when the warning covers the whole
        // selection; short of that the count has to say which part it means.
        // Either way what follows agrees with how many go quietly, not with
        // how many were picked.
        string subject = silent == selected ? (silent == 1 ? "it" : "they") : $"{silent} of them";
        string stays = silent == 1
            ? "It stays on every other map that holds it."
            : "They stay on every other map that holds them.";
        return $"\n\n{cause}, so {subject} will not be marked expired on the mesh. {stays}";
    }
}
