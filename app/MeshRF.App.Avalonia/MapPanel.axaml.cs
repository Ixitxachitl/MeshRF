// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Chrome around <see cref="MapCanvas"/>: zoom/fit/follow buttons, the label
/// and tile-provider selectors, and the "new waypoint" composer that
/// Ctrl+left-click on the map sends. Mirrors the overlay layout of
/// MeshRF.App's MapView.xaml.
/// </summary>
public partial class MapPanel : UserControl
{
    private RadioViewModel? _viewModel;

    /// <summary>Held so the link-profile window can read and write the antenna
    /// facts it needs, which belong to the station rather than to one profile.
    /// </summary>
    private AppSettings? _settings;

    public MapPanel()
    {
        InitializeComponent();
        MapTileThemeCombo.ItemsSource = MapCanvas.MapTileThemeOptions;
        MapTileThemeCombo.SelectedItem = Canvas.TileTheme;

        Canvas.AttributionChanged += () => AttributionText.Text = Canvas.Attribution;
        AttributionText.Text = Canvas.Attribution;

        // The canvas turns follow mode off when the user pans; keep the toggle
        // in sync without re-entering OnFollowHomeToggle.
        Canvas.FollowHomeChanged += follow =>
        {
            if (FollowHomeButton.IsChecked != follow) FollowHomeButton.IsChecked = follow;
        };
        Canvas.RequestSendWaypoint += OnRequestSendWaypoint;
        Canvas.RequestEditWaypoint += OnRequestEditWaypoint;
        Canvas.RequestDeleteWaypoint += OnRequestDeleteWaypoint;
        Canvas.RequestDeleteNode += OnRequestDeleteNode;
        Canvas.RequestLinkProfile += OnRequestLinkProfile;
    }

    /// <summary>"Edit…" on a waypoint marker's context menu, and a double-click
    /// on the marker itself. Same dialog and same update call as the waypoints
    /// grid's own Edit entry.</summary>
    private async void OnRequestEditWaypoint(WaypointRecord wp)
    {
        if (_viewModel is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await WaypointEditWindow.EditAndApplyAsync(owner, _viewModel, wp);
    }

    /// <summary>"Delete" on a waypoint marker's context menu. Asks the same
    /// question the waypoints grid asks, warning when this node cannot retire
    /// the marker on the mesh.</summary>
    private async void OnRequestDeleteWaypoint(WaypointRecord wp)
    {
        if (_viewModel is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (!await DeleteConfirm.WaypointsAsync(owner, _viewModel, [wp])) return;
        await _viewModel.DeleteWaypointCommand.ExecuteAsync(wp);
    }

    /// <summary>"Delete" on a node marker's context menu, asking what the node
    /// list asks.</summary>
    private async void OnRequestDeleteNode(NodeRecord node)
    {
        if (_viewModel is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (!await DeleteConfirm.NodesAsync(owner, [node])) return;
        _viewModel.DeleteNodeCommand.Execute(node);
    }

    /// <summary>"Link profile…" on a node marker's context menu: the terrain
    /// cross-section from this station to that node.</summary>
    private async void OnRequestLinkProfile(NodeRecord node)
    {
        if (_viewModel is null || _settings is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!_viewModel.TryGetHomeLocation(out double lat, out double lon)) return;

        await LinkProfileWindow.ShowForAsync(owner, _viewModel, _settings, node, lat, lon);
    }

    /// <summary>Binds the panel to the view model and restores saved map
    /// preferences. Called once from MainWindow.</summary>
    public void Attach(RadioViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        Canvas.Attach(viewModel);
        Canvas.LoadFromSettings(settings);
        ClusterNodesButton.IsChecked = Canvas.ClusterNodes;
        MapTileThemeCombo.SelectedItem = Canvas.TileTheme;
        AttributionText.Text = Canvas.Attribution;
    }

    public void SaveToSettings(AppSettings settings) => Canvas.SaveToSettings(settings);

    /// <summary>Centers the map on a node — used by the nodes grid's
    /// "Show on map" action.</summary>
    public void CenterOn(double lat, double lon) => Canvas.CenterOn(lat, lon);

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Canvas.ZoomIn();
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Canvas.ZoomOut();
    private void OnGoHome(object? sender, RoutedEventArgs e) => Canvas.GoHome();
    private void OnFitAll(object? sender, RoutedEventArgs e) => Canvas.FitAll();

    private void OnFollowHomeToggle(object? sender, RoutedEventArgs e) =>
        Canvas.SetFollowHome(FollowHomeButton.IsChecked == true);

    private void OnClusterToggle(object? sender, RoutedEventArgs e) =>
        Canvas.SetClusterNodes(ClusterNodesButton.IsChecked == true);

    private void OnMapTileThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MapTileThemeCombo.SelectedItem is string theme) Canvas.SetTileTheme(theme);
    }

    /// <summary>Resets the in-progress pick whenever the toggle flips, so a
    /// stale first corner can't silently complete a later box.</summary>
    private void OnPickBoundingBoxCornersToggled(object? sender, RoutedEventArgs e) =>
        Canvas.ResetBoundingBoxPick();

    private async void OnPickEmoji(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        // A waypoint icon travels as a single uint32 code point, so multi-scalar
        // emoji (flags, keycaps, ZWJ sequences) can't be offered here.
        var picked = await EmojiPickerWindow.PickAsync(owner, singleCodePointOnly: true);
        if (!string.IsNullOrEmpty(picked)) _viewModel.SelectedWaypointEmoji = picked;
    }

    /// <summary>Ctrl+left-click on the map: ask where to send, then transmit.</summary>
    private async void OnRequestSendWaypoint(double lat, double lon)
    {
        if (_viewModel is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dest = await ChannelPickerWindow.PickAsync(owner, _viewModel, "Send waypoint on which channel?");
        if (dest is null) return;

        await _viewModel.SendWaypointFromMapAsync(lat, lon, dest.Value.Channel, dest.Value.DmNodeNum);
    }
}
