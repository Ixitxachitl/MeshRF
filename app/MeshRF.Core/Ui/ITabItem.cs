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
    /// Draw a separator to the left of this tab, because it is the first of a
    /// different kind — the first conversation after the channels.
    /// </summary>
    /// <remarks>
    /// A flag on the tab rather than a rule in the header template: the two
    /// kinds share one <c>TabControl</c>, and a template has no way to ask
    /// what came before it in the list. Recomputed whenever the tab list
    /// changes, so it survives channels being added, DMs being closed and
    /// either being dragged into a new order.
    /// </remarks>
    bool StartsTabGroup { get; set; }

    /// <summary>Messages shown in this tab, newest first.</summary>
    ObservableCollection<ChannelMessage> Messages { get; }
}
