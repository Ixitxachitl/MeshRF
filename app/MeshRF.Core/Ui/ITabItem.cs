// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;

namespace MeshRF;

/// <summary>
/// Common surface for anything shown as a tab in the channel/conversation
/// tab strip. Lets channels and direct-message conversations coexist in one
/// list while sharing a header presentation and message-list content.
/// </summary>
public interface ITabItem
{
    /// <summary>Text shown on the tab header.</summary>
    string TabHeader { get; }

    /// <summary>When true, the tab header should draw attention to unseen activity.</summary>
    bool TabNeedsAttention { get; set; }

    /// <summary>True if the user may close this tab (DM conversations); channels
    /// are permanent and return false.</summary>
    bool CanClose { get; }

    /// <summary>
    /// This tab opens a group, so it carries the group's name and a separator
    /// to its left.
    /// </summary>
    /// <remarks>
    /// A flag on the tab rather than a rule in the header template: every kind
    /// of tab shares one <c>TabControl</c>, and a template has no way to ask
    /// what came before it in the list. Recomputed whenever the tab list
    /// changes, so it survives channels being added, DMs being closed and
    /// either being dragged into a new order.
    /// </remarks>
    bool StartsTabGroup { get; set; }

    /// <summary>
    /// Which mesh this tab belongs to: empty for the primary's, otherwise the
    /// name of the preset being listened for. Channels belong to the list they
    /// are in; a conversation belongs to the mesh its peer was heard on.
    /// </summary>
    string TabGroup { get; set; }

    /// <summary>
    /// Whether the tab is shown at all. A channel on a preset that is not
    /// being listened for is kept — its messages and its key are still
    /// there — but there is nothing it could send or hear, so it is not
    /// offered.
    /// </summary>
    bool IsTabListed { get; set; }

    /// <summary>Messages shown in this tab, newest first.</summary>
    ObservableCollection<ChannelMessage> Messages { get; }
}
