// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
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
        Canvas.RequestCoverageFrom += OnRequestCoverageFrom;
        Canvas.RequestHorizonFrom += OnRequestHorizonFrom;
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
    private async void OnRequestLinkProfile(NodeRecord node) => await ShowLinkProfileAsync(node);

    /// <summary>The same profile, reachable from the node grid as well as from
    /// the marker. Says why rather than doing nothing when an end is missing,
    /// since a grid row gives no hint that a node has never reported where it
    /// is.</summary>
    public async Task ShowLinkProfileAsync(NodeRecord node)
    {
        if (_viewModel is null || _settings is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (node.Latitude is null || node.Longitude is null)
        {
            _viewModel.StatusText = $"{node.LongName} has not reported a position.";
            return;
        }

        if (!_viewModel.TryGetHomeLocation(out double lat, out double lon))
        {
            _viewModel.StatusText = "Set your own location before drawing a link profile.";
            return;
        }

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

    /// <summary>Margins the coverage sweep offers. Named rather than typed:
    /// the number means nothing without knowing what it does to the ring, and
    /// these are the few values anyone actually wants.</summary>
    private static readonly (string Label, double Db)[] CoverageMargins =
    [
        ("0 dB (edge)", 0),
        ("3 dB", 3),
        ("6 dB", 6),
        ("10 dB (reliable)", 10),
        ("15 dB (solid)", 15),
    ];

    /// <summary>Changing the margin changes the ring, so a sweep already on
    /// screen is redrawn rather than left showing the old answer under the new
    /// setting.</summary>
    private void OnCoverageMarginChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settings is null) return;
        if (CoverageMarginCombo.SelectedIndex is not (int index and >= 0)
            || index >= CoverageMargins.Length) return;

        double picked = CoverageMargins[index].Db;
        if (Math.Abs(picked - _settings.CoverageRequiredMarginDb) < 0.001) return;

        _settings.CoverageRequiredMarginDb = picked;
        _settings.Save();

        if (CoverageButton.IsChecked == true) OnCoverageToggle(sender, e);
    }

    /// <summary>Nominatim asks for no more than a request a second, so one
    /// instance holds the gap for the whole app rather than each keystroke
    /// starting fresh.</summary>
    private static readonly PlaceSearch s_places = new();

    private CancellationTokenSource? _placeLookup;

    /// <summary>
    /// Enter in the search box sends the map to the best match.
    ///
    /// Only on Enter, never as you type. Nominatim's usage policy is written
    /// for people typing into a box, and a lookup per keystroke is exactly what
    /// it asks callers not to do.
    /// </summary>
    private async void OnPlaceSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is null) return;
        e.Handled = true;

        var query = PlaceSearchBox.Text;
        if (string.IsNullOrWhiteSpace(query)) return;

        _placeLookup?.Cancel();
        var cts = new CancellationTokenSource();
        _placeLookup = cts;

        _viewModel.StatusText = $"Looking up “{query.Trim()}”…";
        try
        {
            var found = await s_places.FindAsync(query, limit: 1, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            if (found.Count == 0)
            {
                _viewModel.StatusText = $"Nothing found for “{query.Trim()}”.";
                return;
            }

            var place = found[0];
            Canvas.CenterOn(place.At.Lat, place.At.Lon);
            _viewModel.StatusText = place.Name;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later search, or the panel went away.
        }
        finally
        {
            if (ReferenceEquals(_placeLookup, cts)) _placeLookup = null;
            cts.Dispose();
        }
    }

    /// <summary>Supersedes an earlier sweep: the toggle can be flipped again
    /// while tiles are still being fetched for the last one.</summary>
    private CancellationTokenSource? _coverageRun;

    /// <summary>Where the next sweep runs from, when it is somewhere other than
    /// this station. Cleared when the toggle is used on its own, so the button
    /// always means "from here" and the menu entry always means "from
    /// there".</summary>
    private GeoPoint? _coverageOrigin;

    /// <summary>"Coverage from here" on bare map: sweep as though a node stood
    /// at that point, with this station's antenna and radio.</summary>
    private void OnRequestCoverageFrom(double lat, double lon)
    {
        _coverageOrigin = new GeoPoint(lat, lon);

        // Re-enter through the toggle so there is one path that runs a sweep.
        // Already on, and it has to be nudged: the origin changed, which the
        // checked state cannot express.
        if (CoverageButton.IsChecked == true) OnCoverageToggle(this, new RoutedEventArgs());
        else CoverageButton.IsChecked = true;
    }

    /// <summary>"Horizon from here…" on bare map: the skyline a node put there
    /// would see.</summary>
    private async void OnRequestHorizonFrom(double lat, double lon)
    {
        if (_viewModel is null || _settings is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await HorizonWindow.ShowForAsync(owner, _viewModel, _settings, lat, lon);
    }

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
            _coverageOrigin = null;
            Canvas.ShowCoverage(null);
            return;
        }

        if (_viewModel is null || _settings is null) return;

        double lat, lon;
        if (_coverageOrigin is { } dropped)
        {
            (lat, lon) = (dropped.Lat, dropped.Lon);
        }
        else if (!_viewModel.TryGetHomeLocation(out lat, out lon))
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
        // Only meaningful about this station: what a dropped point has "heard
        // directly" is nothing, since every reading was taken from here.
        double measuredM = _coverageOrigin is null
            ? FurthestHeardDirectM(_viewModel, new GeoPoint(lat, lon))
            : 0;
        var calibration = FittedPathLoss(_settings);
        var applied = UsableForCoverage(calibration) ? calibration : null;

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
            RequiredMarginDb: _settings.CoverageRequiredMarginDb,
            Calibration: applied,
            MaxCredibleRangeM: CredibleRangeM(applied));

        _viewModel.StatusText = "Sweeping coverage…";
        try
        {
            // The sweep itself is arithmetic over an in-memory grid, but it is
            // hundreds of radials of it, so it goes off the UI thread with the
            // tile fetch rather than after it.
            var result = await Task.Run<(TerrainArea Area, CoverageRing? Ring)?>(async () =>
            {
                // The radius the sweep will actually walk, cap included. The
                // open-ground reach alone is the wrong number to fetch by: a
                // sweep bounded to a few miles would still pull a disc hundreds
                // across, and the zoom that disc forces reads terrain at a
                // kilometre a pixel — which is how a bounded ring came out a
                // perfect circle with nothing obstructed anywhere.
                double radius = CoverageMap.OpenGroundRangeM(options) * 1.05;
                if (options.MaxCredibleRangeM > 0)
                    radius = Math.Min(radius, options.MaxCredibleRangeM);
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
                (_coverageOrigin is null ? string.Empty : "from a dropped point · ") +
                $"to {DisplayUnits.FormatShortDistance(ring.UnobstructedRangeM, units)} open" +
                (options.Calibration is null ? " · free space" : " · calibrated") +
                $" · terrain {TerrainGrid.MetresPerPixel(area.Zoom, options.Centre.Lat):0} m/px";

            Canvas.ShowCoverage(ring, note, measuredM, units);

            _viewModel.StatusText =
                CoverageSummary(ring, area, measuredM, applied, calibration, units,
                                fromDroppedPoint: _coverageOrigin is not null);
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
    /// Whether a fitted model can answer the question coverage asks, which is
    /// entirely about range.
    ///
    /// A fit that held its exponent at free space measured how strong signals
    /// were at about one distance and nothing at all about how they fall off.
    /// Handed to a sweep it does not merely predict badly — it predicts
    /// uselessly: with the exponent at 2 and a large negative offset the link
    /// keeps tens of decibels of headroom right out to the edge of what the fit
    /// has evidence for, so nothing anywhere obstructs it and the ring comes out
    /// a circle. Better to sweep on plain physics and say why.
    /// </summary>
    private static bool UsableForCoverage(PathLossFit? fit) =>
        fit is { ExponentFitted: true, IsPlausible: true };

    /// <summary>
    /// What the sweep found, and how much of it to believe. A ring drawn on
    /// free-space loss over anywhere with trees or buildings promises range the
    /// station does not have, and at these ranges the terrain is read too
    /// coarsely to argue back — so the line says which of those apply rather
    /// than reporting a number that looks authoritative.
    /// </summary>
    private static string CoverageSummary(
        CoverageRing ring, TerrainArea area, double measuredM,
        PathLossFit? applied, PathLossFit? onFile, UnitSystem units,
        bool fromDroppedPoint)
    {
        var parts = new List<string>
        {
            $"Coverage swept: {ring.CountOf(CoverageQuality.Clear)} bearings clear, " +
            $"{ring.CountOf(CoverageQuality.Weakened)} weakened, " +
            $"{ring.CountOf(CoverageQuality.Blocked)} blocked, over {area.TileCount} terrain tiles",
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
            parts.Add(fromDroppedPoint
                ? "nothing measured from a point nobody is standing at, so there is no check on this"
                : "nothing heard directly yet to check it against");
        }

        if (ring.RangeWasCapped)
        {
            parts.Add(ring.CountOf(CoverageQuality.Clear) == ring.Spokes.Count
                ? "nothing here limited the ring — it stops at the edge of the fit's evidence, " +
                  "not at the edge of coverage"
                : "stopped where the model runs out of evidence, not where the signal does");
        }

        parts.Add((applied, onFile) switch
        {
            ({ } fit, _) => $"calibrated, n = {fit.Exponent:0.00}",
            (null, { ExponentFitted: false }) =>
                "your calibration never measured a falloff, so it cannot answer a question about " +
                "range — swept on free space instead. It needs neighbours spread across range",
            (null, { IsPlausible: false } bad) =>
                $"your calibration fitted n = {bad.Exponent:0.00}, outside what real environments " +
                "produce — swept on free space instead",
            _ => "free-space loss — calibrate path loss to draw the range this site actually has",
        });

        if (!area.Complete) parts.Add("some terrain missing");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// How far out a fitted calibration may honestly be asked about, or zero
    /// when there is nothing fitted to bound.
    ///
    /// The bound is about extrapolation, so it applies only to a model that was
    /// fitted to something. Free-space loss is physics with no fitted
    /// parameters — optimistic over anywhere with clutter in it, but not
    /// extrapolating from evidence it does not have, so it is warned about
    /// rather than clipped. Clipping it to what this station happens to have
    /// heard would be a different claim entirely, and a wrong one: a radio's
    /// reach is not bounded by where somebody put a node.
    /// </summary>
    private static double CredibleRangeM(PathLossFit? calibration) =>
        calibration?.CredibleRangeM ?? 0;

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

        CoverageMarginCombo.ItemsSource = CoverageMargins.Select(m => m.Label).ToList();
        CoverageMarginCombo.SelectedIndex = Math.Max(0, Array.FindIndex(
            CoverageMargins, m => Math.Abs(m.Db - settings.CoverageRequiredMarginDb) < 0.001));
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
