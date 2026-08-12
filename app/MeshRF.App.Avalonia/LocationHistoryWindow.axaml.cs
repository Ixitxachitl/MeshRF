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
