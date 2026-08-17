// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using Avalonia.Controls;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Per-peer location history: the recorded track on a map beside the list of
/// fixes that make it up. Ported from MeshRF.App's LocationHistoryWindow.
/// </summary>
public partial class LocationHistoryWindow : Window
{
    private ConversationTabViewModel? _conversation;

    public LocationHistoryWindow()
    {
        InitializeComponent();
    }

    private async void OnClear(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_conversation is null) return;
        int count = _conversation.LocationHistory.Count;
        if (count == 0) return;
        if (!await ConfirmDialog.ConfirmAsync(this, "Clear location history",
                $"Delete {count} recorded position{(count == 1 ? "" : "s")} for {_conversation.PeerName}? This removes the stored history and cannot be undone.",
                confirmText: "Clear"))
            return;
        _conversation.ClearLocationHistoryCommand.Execute(null);
    }

    private void OnGridKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Delete) return;
        e.Handled = true;
        _ = DeleteSelectedAsync(sender as DataGrid);
    }

    private void OnDeleteSelected(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _ = DeleteSelectedAsync(PointsGrid);

    /// <summary>
    /// Deletes whatever is selected, after confirming. The selection is copied
    /// out first: removing from the bound collection changes the grid's own
    /// SelectedItems underneath the loop.
    /// </summary>
    private async Task DeleteSelectedAsync(DataGrid? grid)
    {
        if (_conversation is null || grid is null) return;

        var points = grid.SelectedItems.OfType<LocationHistoryPoint>().ToList();
        if (points.Count == 0) return;

        if (!await ConfirmDialog.ConfirmAsync(this, "Delete positions",
                $"Delete {points.Count} recorded position{(points.Count == 1 ? "" : "s")} for {_conversation.PeerName}? This cannot be undone.",
                confirmText: "Delete"))
            return;

        _conversation.DeleteLocationHistoryPoints(points);
        RefreshTrack();
    }

    // One window per conversation, as above. This one also has to unsubscribe:
    // the CollectionChanged handler below outlives the window otherwise, so
    // every open left another dead window's handler firing on the live history.
    private static readonly Dictionary<uint, LocationHistoryWindow> s_open = new();

    public static void Show(Window owner, ConversationTabViewModel conversation)
    {
        if (s_open.TryGetValue(conversation.NodeNum, out var existing))
        {
            existing.Activate();
            return;
        }

        conversation.EnsureHistoryLoaded();

        var w = new LocationHistoryWindow
        {
            DataContext = conversation,
            _conversation = conversation,
        };
        w.Title = $"Location History — {conversation.TabHeader}";

        // The map needs a size before it can fit the track, so wait for layout.
        w.Opened += (_, _) => w.RefreshTrack();
        void OnHistoryChanged(object? s, NotifyCollectionChangedEventArgs e) => w.RefreshTrack();
        conversation.LocationHistory.CollectionChanged += OnHistoryChanged;

        s_open[conversation.NodeNum] = w;
        w.Closed += (_, _) =>
        {
            conversation.LocationHistory.CollectionChanged -= OnHistoryChanged;
            s_open.Remove(conversation.NodeNum);
        };

        w.Show(owner);
    }

    private void RefreshTrack()
    {
        if (_conversation is null) return;
        Map.ShowTrack(_conversation.LocationHistory
            .Select(p => (p.Latitude, p.Longitude))
            .ToList());
    }
}
