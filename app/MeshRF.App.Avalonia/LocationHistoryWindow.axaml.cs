// SPDX-License-Identifier: GPL-3.0-or-later
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

    public static void Show(Window owner, ConversationTabViewModel conversation)
    {
        conversation.EnsureHistoryLoaded();

        var w = new LocationHistoryWindow
        {
            DataContext = conversation,
            _conversation = conversation,
        };
        w.Title = $"Location History — {conversation.TabHeader}";

        // The map needs a size before it can fit the track, so wait for layout.
        w.Opened += (_, _) => w.RefreshTrack();
        conversation.LocationHistory.CollectionChanged += (_, _) => w.RefreshTrack();

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
