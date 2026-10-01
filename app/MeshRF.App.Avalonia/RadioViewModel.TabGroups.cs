// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeshRF.AvaloniaApp;

/// <summary>One mesh in the tab strip's picker.</summary>
public sealed partial class TabGroupOption : ObservableObject
{
    /// <summary>Empty for the primary's mesh, otherwise the preset.</summary>
    public required string Group { get; init; }

    public required string Label { get; init; }

    /// <summary>
    /// Something on this mesh has unseen activity. Drives the same pulse the
    /// channel and conversation tabs use, so a mesh reads the way its children
    /// do.
    /// </summary>
    /// <remarks>
    /// Not cleared by bringing the mesh on show. Showing a mesh is not reading
    /// what arrived on it — the tab that has the activity is still sitting
    /// there unopened — and a mark that goes out on the way to the thing it
    /// was pointing at has stopped pointing at anything. It goes out when that
    /// tab is looked at, which is what clears the tab's own pulse.
    /// </remarks>
    [ObservableProperty] private bool _needsAttention;

    /// <summary>Whether this is the mesh on show, which is what paints its
    /// tab as the one the strip below belongs to.</summary>
    [ObservableProperty] private bool _isSelected;

    public override string ToString() => Label;
}

public partial class RadioViewModel
{
    /// <summary>
    /// The meshes with tabs, the primary's first. One is shown at a time: a
    /// wide capture can be listening for a dozen presets, and every one of
    /// their channels in a single strip would be unusable.
    /// </summary>
    public ObservableCollection<TabGroupOption> TabGroupOptions { get; } = new();

    [ObservableProperty] private TabGroupOption? _selectedTabGroupOption;

    /// <summary>The picker only earns its place once there is a choice.</summary>
    public bool HasSeveralMeshes => TabGroupOptions.Count > 1;

    /// <summary>True while the picker is being brought into line with the
    /// host, so a sync is not mistaken for the user choosing a mesh.</summary>
    private bool _syncingTabGroup;

    partial void OnSelectedTabGroupOptionChanged(TabGroupOption? value)
    {
        if (_syncingTabGroup || value is null) return;
        if (_rxHost.ShowGroup(value.Group))
            SelectedTab = Tabs.FirstOrDefault(t => t.IsTabListed);
        RefreshTabGroupAttention();
    }

    /// <summary>
    /// Rebuilds the picker from the tabs that exist. Reads the host rather
    /// than telling it anything, so it is safe to run while the tab
    /// collection is mid-change — which is exactly when a tab has just been
    /// added or closed.
    /// </summary>
    public void RefreshTabGroupOptions()
    {
        var groups = _rxHost.TabGroups();

        // Options are kept rather than rebuilt so the picker does not lose
        // its selection, and its unread marks, every time a tab appears.
        for (int i = TabGroupOptions.Count - 1; i >= 0; i--)
            if (!groups.Contains(TabGroupOptions[i].Group, StringComparer.Ordinal))
                TabGroupOptions.RemoveAt(i);

        for (int i = 0; i < groups.Count; i++)
        {
            var existing = TabGroupOptions.FirstOrDefault(o => o.Group == groups[i]);
            if (existing is null)
            {
                TabGroupOptions.Insert(Math.Min(i, TabGroupOptions.Count),
                    new TabGroupOption { Group = groups[i], Label = AvaloniaMeshRxHost.LabelForGroup(groups[i]) });
                continue;
            }
            int at = TabGroupOptions.IndexOf(existing);
            if (at != i) TabGroupOptions.Move(at, i);
        }

        var want = TabGroupOptions.FirstOrDefault(o => o.Group == _rxHost.ShownGroup)
                   ?? TabGroupOptions.FirstOrDefault();
        _syncingTabGroup = true;
        try { SelectedTabGroupOption = want; }
        finally { _syncingTabGroup = false; }

        OnPropertyChanged(nameof(HasSeveralMeshes));
        RefreshTabGroupAttention();
    }

    /// <summary>The primary's mesh stays first, as its channel does in the
    /// strip below; the rest move among themselves.</summary>
    public bool CanDragMesh(TabGroupOption? option) =>
        option is not null && option.Group != _rxHost.PrimaryListName;

    public bool CanReorderMeshPair(TabGroupOption? dragged, TabGroupOption? target) =>
        CanDragMesh(dragged) && CanDragMesh(target) && !ReferenceEquals(dragged, target);

    /// <summary>Puts the dragged mesh where the target is, the way a channel
    /// tab dropped on another takes its place. Returns true when the order
    /// changed.</summary>
    public bool ReorderMeshPair(TabGroupOption? dragged, TabGroupOption? target)
    {
        if (!CanReorderMeshPair(dragged, target)) return false;

        var order = TabGroupOptions.Select(o => o.Group)
                                   .Where(g => g != _rxHost.PrimaryListName).ToList();
        int from = order.IndexOf(dragged!.Group), to = order.IndexOf(target!.Group);
        if (from < 0 || to < 0) return false;
        order.RemoveAt(from);
        order.Insert(to, dragged.Group);

        // Meshes not on the strip right now keep their remembered places
        // after the ones that are.
        order.AddRange(_rxHost.MeshOrder.Where(g => !order.Contains(g, StringComparer.Ordinal)));
        _rxHost.MeshOrder = order;
        RefreshTabGroupOptions();
        SaveSettings();
        return true;
    }

    /// <summary>Marks the mesh on show, and every mesh holding a tab with
    /// unseen activity — the one on show included.</summary>
    private void RefreshTabGroupAttention()
    {
        foreach (var option in TabGroupOptions)
        {
            option.IsSelected = option.Group == _rxHost.ShownGroup;
            option.NeedsAttention = _rxHost.GroupNeedsAttention(option.Group);
        }
    }
}
