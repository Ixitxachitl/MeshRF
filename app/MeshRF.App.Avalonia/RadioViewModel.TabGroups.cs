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

    /// <summary>Something on this mesh is unread while another is on show.
    /// Hiding a mesh must not hide the fact that it is talking.</summary>
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

    /// <summary>Marks the mesh on show, and those that are not but have
    /// something unread.</summary>
    private void RefreshTabGroupAttention()
    {
        foreach (var option in TabGroupOptions)
        {
            option.IsSelected = option.Group == _rxHost.ShownGroup;
            option.NeedsAttention = !option.IsSelected
                                    && _rxHost.GroupNeedsAttention(option.Group);
        }
    }
}
