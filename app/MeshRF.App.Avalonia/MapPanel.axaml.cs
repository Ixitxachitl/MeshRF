// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeshRF.Map;
using MeshRF.Mesh;
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

    /// <summary>"Path loss…" on the map chrome: fits a model to every direct
    /// neighbour at once, rather than to the one link a marker stands for.
    /// </summary>
    private async void OnOpenPathLoss(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _settings is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!_viewModel.TryGetHomeLocation(out double lat, out double lon))
        {
            _viewModel.StatusText = "Set your own location before calibrating path loss.";
            return;
        }

        await PathLossWindow.ShowForAsync(owner, _viewModel, _settings, lat, lon);
    }

    /// <summary>"Horizon…" on the map chrome: what this antenna can see, which
    /// is a question about the station rather than about any one link.</summary>
    private async void OnOpenHorizon(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _settings is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!_viewModel.TryGetHomeLocation(out double lat, out double lon))
        {
            _viewModel.StatusText = "Set your own location before sweeping the horizon.";
            return;
        }

        await HorizonWindow.ShowForAsync(owner, _viewModel, _settings, lat, lon);
    }

    /// <summary>Supersedes an earlier sweep: the toggle can be flipped again
    /// while tiles are still being fetched for the last one.</summary>
    private CancellationTokenSource? _coverageRun;

    /// <summary>
    /// The "Coverage" toggle: sweeps the compass from this station and draws
    /// how far it reaches in each direction.
    ///
    /// Run on demand rather than kept live. A sweep reads terrain over the
    /// whole disc and takes a moment on a cold cache, and the answer only
    /// changes when the station moves or its radio settings do — neither of
    /// which happens while someone is looking at the map.
    /// </summary>
    private async void OnCoverageToggle(object? sender, RoutedEventArgs e)
    {
        _coverageRun?.Cancel();

        if (CoverageButton.IsChecked != true)
        {
            Canvas.ShowCoverage(null);
            return;
        }

        if (_viewModel is null || _settings is null) return;
        if (!_viewModel.TryGetHomeLocation(out double lat, out double lon))
        {
            _viewModel.StatusText = "Set your own location before sweeping coverage.";
            CoverageButton.IsChecked = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _coverageRun = cts;

        var (sf, bwKhz, _) = _viewModel.EffectiveLoraParams;

        // The one measured distance available, and the bound on how far any of
        // this may be carried. Read before the sweep so it can shape it rather
        // than merely annotate it afterwards.
        double measuredM = FurthestHeardDirectM(_viewModel, new GeoPoint(lat, lon));
        var calibration = FittedPathLoss(_settings);

        var options = new CoverageOptions(
            Centre: new GeoPoint(lat, lon),
            MyAntennaM: _settings.LinkProfileMyAntennaM,
            PeerAntennaM: _settings.LinkProfilePeerAntennaM,
            MyGainDbi: _settings.LinkProfileMyGainDbi,
            PeerGainDbi: _settings.LinkProfilePeerGainDbi,
            TxPowerDbm: _viewModel.IsTxSx1262 ? _viewModel.Sx1262TxPowerDbm : 22,
            FrequencyMhz: _viewModel.CenterFreqMHz,
            BandwidthKhz: bwKhz,
            SpreadingFactor: sf,
            Calibration: calibration,
            MaxCredibleRangeM: CredibleRangeM(calibration, measuredM));

        _viewModel.StatusText = "Sweeping coverage…";
        try
        {
            // The sweep itself is arithmetic over an in-memory grid, but it is
            // hundreds of radials of it, so it goes off the UI thread with the
            // tile fetch rather than after it.
            var result = await Task.Run<(TerrainArea Area, CoverageRing? Ring)?>(async () =>
            {
                // The open-ground reach, not the link budget's own: at LoRa
                // sensitivity the budget runs to hundreds of kilometres, and a
                // disc that size would be fetched at a zoom too coarse to see
                // any terrain at all.
                double radius = CoverageMap.OpenGroundRangeM(options) * 1.05;
                var area = await SharedTerrain.Tiles
                    .LoadAreaAsync(options.Centre, radius, cts.Token)
                    .ConfigureAwait(false);
                return area is null ? null : (area, CoverageMap.Build(area.Grid, options));
            }, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;

            if (result is not { Ring: { } ring, Area: { } area })
            {
                _viewModel.StatusText = "No elevation data around this location.";
                CoverageButton.IsChecked = false;
                return;
            }

            var units = _viewModel.CurrentUnitSystem;

            string note =
                $"to {DisplayUnits.FormatShortDistance(ring.UnobstructedRangeM, units)} open" +
                (options.Calibration is null ? " · free space" : " · calibrated") +
                $" · terrain {TerrainGrid.MetresPerPixel(area.Zoom, options.Centre.Lat):0} m/px";

            Canvas.ShowCoverage(ring, note, measuredM, units);

            _viewModel.StatusText = CoverageSummary(ring, area, measuredM, options, units);
        }
        catch (OperationCanceledException)
        {
            // The toggle was flipped again, or the panel went away.
        }
        finally
        {
            if (ReferenceEquals(_coverageRun, cts)) _coverageRun = null;
            cts.Dispose();
        }
    }

    /// <summary>How far this station has actually heard a node for itself —
    /// zero hops, over the air. Zero when it has heard none, which is its own
    /// answer: there is nothing to check the prediction against.</summary>
    private static double FurthestHeardDirectM(RadioViewModel vm, GeoPoint home)
    {
        var direct = PathLossSurvey.Candidates(vm.Nodes, home, vm.MyNodeNum);
        return direct.Count == 0
            ? 0
            : direct.Max(n => Geodesy.DistanceM(home, new GeoPoint(n.Latitude!.Value, n.Longitude!.Value)));
    }

    /// <summary>
    /// What the sweep found, and how much of it to believe. A ring drawn on
    /// free-space loss over anywhere with trees or buildings promises range the
    /// station does not have, and at these ranges the terrain is read too
    /// coarsely to argue back — so the line says which of those apply rather
    /// than reporting a number that looks authoritative.
    /// </summary>
    private static string CoverageSummary(
        CoverageRing ring, TerrainArea area, double measuredM,
        CoverageOptions options, UnitSystem units)
    {
        var parts = new List<string>
        {
            $"Coverage swept: {ring.Spokes.Count} bearings over {area.TileCount} terrain tiles",
        };

        if (measuredM > 0)
        {
            double ratio = ring.UnobstructedRangeM / measuredM;
            parts.Add(
                $"furthest heard direct {DisplayUnits.FormatShortDistance(measuredM, units)}" +
                (ratio >= 3 ? $", so this ring claims {ratio:0}× what the radio has managed" : string.Empty));
        }
        else
        {
            parts.Add("nothing heard directly yet to check it against");
        }

        if (ring.RangeWasCapped)
            parts.Add("stopped where the model runs out of evidence, not where the signal does");

        parts.Add(options.Calibration switch
        {
            null => "free-space loss — calibrate path loss to draw the range this site actually has",
            { ExponentFitted: false } => "the calibration never measured a falloff, so it says nothing " +
                                         "about longer ranges — it needs neighbours spread across range",
            { IsPlausible: false } => "the fitted exponent is outside what real environments produce",
            _ => $"calibrated, n = {options.Calibration.Exponent:0.00}",
        });

        if (!area.Complete) parts.Add("some terrain missing");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// How far out this station's models may honestly be asked about.
    ///
    /// A calibration is evidence only over the ranges it was measured across,
    /// and stretches a little past them. Failing that, the furthest node heard
    /// directly is the only distance anything here has been checked at. With
    /// neither, there is no bound to give and the ring runs to wherever the
    /// link budget and the horizon take it — which is what the summary warns
    /// about.
    /// </summary>
    private static double CredibleRangeM(PathLossFit? calibration, double measuredM)
    {
        double fromFit = calibration?.CredibleRangeM ?? 0;
        double fromMeasurement = measuredM > 0 ? measuredM * PathLossFit.ExtrapolationFactor : 0;
        return Math.Max(fromFit, fromMeasurement);
    }

    /// <summary>The applied path-loss calibration, if there is one. Without it
    /// the sweep spends free-space loss, which draws a ring far larger than any
    /// station in clutter actually has.</summary>
    private static PathLossFit? FittedPathLoss(AppSettings settings) =>
        settings is { PathLossExponent: double exponent, PathLossOffsetDb: double offset }
            ? new PathLossFit(exponent, offset, settings.PathLossRmsDb ?? 0,
                              settings.PathLossSampleCount,
                              ExponentFitted: settings.PathLossExponentFitted,
                              OffsetFitted: true,
                              FurthestSampleM: settings.PathLossFurthestSampleM)
            : null;

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
