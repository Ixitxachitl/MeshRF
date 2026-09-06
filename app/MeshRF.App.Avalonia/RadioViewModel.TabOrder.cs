// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Drag-to-reorder for the tab strip, ported from MeshRF.App's MainViewModel.
/// The view supplies the gesture; every ordering rule lives here.
///
/// Channels and DMs reorder on different terms. A channel tab's position *is*
/// its persisted channel index, so moving one rewrites the store; the primary
/// is pinned, since its index is what identifies it on the mesh. A DM tab is
/// just a view, so those move freely within the conversation group.
/// </summary>
public partial class RadioViewModel
{
    /// <summary>Whether a tab can be picked up at all. The primary channel is
    /// pinned, so dragging it is refused before a drag even starts rather than
    /// silently doing nothing on drop.</summary>
    public bool CanDragTab(object? tab) => tab switch
    {
        ChannelTabViewModel channel => channel.Config.Role != ChannelRole.Primary,
        ConversationTabViewModel => true,
        _ => false,
    };

    /// <summary>Whether one tab may be dropped onto another. Both must be the
    /// same kind, and channels must both be secondary.</summary>
    public bool CanReorderTabPair(object? dragged, object? target)
    {
        if (dragged is null || target is null || ReferenceEquals(dragged, target))
            return false;

        // Within one list only: a channel's list is which mesh it is on.
        if (dragged is ChannelTabViewModel dragChannel && target is ChannelTabViewModel targetChannel)
            return dragChannel.Config.Preset == targetChannel.Config.Preset &&
                   dragChannel.Config.Role != ChannelRole.Primary &&
                   targetChannel.Config.Role != ChannelRole.Primary;

        return dragged is ConversationTabViewModel && target is ConversationTabViewModel;
    }

    /// <summary>Applies a drop. Returns true when the order actually changed.</summary>
    public bool ReorderTabPair(object? dragged, object? target)
    {
        if (!CanReorderTabPair(dragged, target)) return false;

        if (dragged is ChannelTabViewModel dragChannel && target is ChannelTabViewModel targetChannel)
            return ReorderChannelsByDrag(dragChannel, targetChannel);

        if (dragged is ConversationTabViewModel dragConvo && target is ConversationTabViewModel targetConvo)
            return ReorderConversationsByDrag(dragConvo, targetConvo);

        return false;
    }

    /// <summary>DM tabs are presentation only, so this is a plain move within
    /// the conversation group — no channel indices are touched.</summary>
    private bool ReorderConversationsByDrag(ConversationTabViewModel dragged, ConversationTabViewModel target)
    {
        int dragIndex = Tabs.IndexOf(dragged);
        int targetIndex = Tabs.IndexOf(target);
        if (dragIndex < 0 || targetIndex < 0 || dragIndex == targetIndex) return false;

        // Conversations always sit after the channels; refuse anything that
        // would interleave them.
        int channelCount = Tabs.OfType<ChannelTabViewModel>().Count();
        if (dragIndex < channelCount || targetIndex < channelCount) return false;

        Tabs.Move(dragIndex, targetIndex);
        SelectedTab = dragged;
        SaveOpenConversations();
        return true;
    }

    /// <summary>
    /// Reorders secondary channels by reassigning their stored indices. The set
    /// of indices in use is preserved and only the mapping to channels changes,
    /// so a channel keeps a valid slot and nothing collides with the primary.
    /// </summary>
    private bool ReorderChannelsByDrag(ChannelTabViewModel dragged, ChannelTabViewModel target)
    {
        if (dragged.Config.Role == ChannelRole.Primary || target.Config.Role == ChannelRole.Primary)
            return false;

        var listName = dragged.Config.Preset;
        var secondaries = Tabs.OfType<ChannelTabViewModel>()
            .Where(t => t.Config.Preset == listName && t.Config.Role != ChannelRole.Primary)
            .OrderBy(t => t.Config.Index)
            .ToList();
        if (secondaries.Count < 2) return false;

        int dragPos = secondaries.FindIndex(t => t.Config.Index == dragged.Config.Index);
        int targetPos = secondaries.FindIndex(t => t.Config.Index == target.Config.Index);
        if (dragPos < 0 || targetPos < 0 || dragPos == targetPos) return false;

        var availableIndices = secondaries.Select(t => t.Config.Index).OrderBy(i => i).ToList();
        var moved = secondaries[dragPos];
        secondaries.RemoveAt(dragPos);
        secondaries.Insert(targetPos, moved);

        // Clear first: the store is keyed by index, so writing the new mapping
        // in place would collide with rows not yet moved.
        foreach (var idx in availableIndices) _rxHost.DeleteChannelIndex(listName, idx);
        for (int i = 0; i < secondaries.Count; i++)
        {
            secondaries[i].Config.Index = availableIndices[i];
            _rxHost.UpsertChannelConfig(secondaries[i].Config);
        }

        ReorderChannelTabs();
        SelectedTab = moved;
        return true;
    }

    /// <summary>Re-sorts the channel tabs to match their (just rewritten)
    /// indices, leaving the conversation tabs after them untouched.</summary>
    private void ReorderChannelTabs()
    {
        // The primary's list first, then each preset's list, each with its
        // Primary-role channel ahead of the rest.
        var ordered = Tabs.OfType<ChannelTabViewModel>()
            .OrderBy(t => t.Config.Preset.Length == 0 ? 0 : 1)
            .ThenBy(t => t.Config.Preset, StringComparer.Ordinal)
            .ThenBy(t => t.Config.Role == ChannelRole.Primary ? 0 : 1)
            .ThenBy(t => t.Config.Index)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            int current = Tabs.IndexOf(ordered[i]);
            if (current != i) Tabs.Move(current, i);
            ordered[i].NotifyConfigChanged();
        }
    }
}
