// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using MeshRF.Map;
using MeshRF.Mesh;
using MeshRF.Nodes;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// What the ground between this station and one peer does to the link: the
/// terrain cross-section, the first Fresnel zone, and the loss a ridge in the
/// way costs, set against what the radio should hear if the path were clear.
///
/// The prediction is terrain-only — no buildings, foliage or fading — so its
/// job is to separate a path that is geometrically fine from one that is not,
/// and to say how far a marginal one is from clearing. Where the peer is a
/// direct neighbour the measured SNR is shown beside the predicted one: the gap
/// between them is the clutter loss this model does not carry, which is the
/// number worth knowing for a real site.
/// </summary>
public partial class LinkProfileWindow : Window
{
    private static readonly IBrush Clear = new SolidColorBrush(Color.Parse("#66BB6A"));
    private static readonly IBrush Marginal = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush Blocked = new SolidColorBrush(Color.Parse("#EF5350"));

    private RadioViewModel? _vm;
    private AppSettings? _settings;
    private NodeRecord? _node;
    private GeoPoint _from;
    private GeoPoint _to;
    private UnitSystem _units;
    private CancellationTokenSource? _running;
    private BuildingIndex _buildings = BuildingIndex.Empty;
    private BuildingExtract _buildingExtract = BuildingExtract.None;

    public LinkProfileWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _running?.Cancel();
    }

    /// <summary>Opens the profile from this station to a node. The caller has
    /// already established that both ends have a position.</summary>
    /// <param name="fromName">What to call the near end in the header. The
    /// origin is not always this station, and a header that says it is when the
    /// profile was drawn from a chosen point is simply wrong.</param>
    public static async Task ShowForAsync(
        Window owner, RadioViewModel vm, AppSettings settings, NodeRecord node,
        double fromLat, double fromLon, string fromName = "This station")
    {
        if (node.Latitude is not double toLat || node.Longitude is not double toLon) return;

        var window = new LinkProfileWindow
        {
            _vm = vm,
            _settings = settings,
            _node = node,
            _from = new GeoPoint(fromLat, fromLon),
            _to = new GeoPoint(toLat, toLon),
            _units = vm.CurrentUnitSystem,
            _fromName = fromName,
        };
        window.Prepare();
        await window.ShowDialog(owner);
    }

    /// <summary>
    /// A profile between two places on the map, neither of which is a node.
    ///
    /// The far end no longer has to be something the mesh knows about, which is
    /// the question behind siting two nodes at once: neither exists yet, so
    /// neither can be picked from the node list. Everything the window draws is
    /// geometry and terrain; the measured-SNR comparison is the only part that
    /// needed a node, and it simply does not appear.
    /// </summary>
    public static async Task ShowBetweenAsync(
        Window owner, RadioViewModel vm, AppSettings settings,
        GeoPoint from, GeoPoint to, string fromName, string toName)
    {
        var window = new LinkProfileWindow
        {
            _vm = vm,
            _settings = settings,
            _from = from,
            _to = to,
            _units = vm.CurrentUnitSystem,
            _fromName = fromName,
            _toName = toName,
        };
        window.Prepare();
        await window.ShowDialog(owner);
    }

    private string _fromName = "This station";
    private string? _toName;

    private string PeerName =>
        _toName is not null ? _toName
        : !string.IsNullOrWhiteSpace(_node?.LongName) ? _node!.LongName
        : !string.IsNullOrWhiteSpace(_node?.ShortName) ? _node!.ShortName
        : _node is null ? "peer" : $"!{_node.NodeNum:x8}";

    private void Prepare()
    {
        if (_vm is null || _settings is null) return;

        Title = $"Link Profile — {PeerName}";
        HeaderText.Text = $"{_fromName}  →  {PeerName}";

        string heightUnit = DisplayUnits.AltitudeUnitShort(_units);
        MyHeightLabel.Text = $"My antenna ({heightUnit})";
        PeerHeightLabel.Text = $"Peer antenna ({heightUnit})";
        MyHeightBox.Text = FormatHeight(_settings.LinkProfileMyAntennaM);
        PeerHeightBox.Text = FormatHeight(_settings.LinkProfilePeerAntennaM);
        MyGainBox.Text = _settings.LinkProfileMyGainDbi.ToString("0.##", CultureInfo.InvariantCulture);
        PeerGainBox.Text = _settings.LinkProfilePeerGainDbi.ToString("0.##", CultureInfo.InvariantCulture);

        // Seeded from the radio each time rather than persisted: the configured
        // output power is the truth, and a stale copy here would quietly
        // predict a link the radio cannot make.
        TxPowerBox.Text = DefaultTxPowerDbm().ToString("0.#", CultureInfo.InvariantCulture);

        var (sf, bwKhz, _) = _vm.EffectiveLoraParams;
        FrequencyText.Text = $"{_vm.CenterFreqMHz:0.###} MHz";
        ModemText.Text = $"SF{sf} · {bwKhz:0.#} kHz";
        SensitivityText.Text = $"{LinkBudget.SensitivityDbm(sf, bwKhz):0.0} dBm";

        _ = RecomputeAsync();
    }

    private double DefaultTxPowerDbm() =>
        _vm is { IsTxSx1262: true } vm ? vm.Sx1262TxPowerDbm : 22;

    private void OnRecompute(object? sender, RoutedEventArgs e) => _ = RecomputeAsync();

    private async Task RecomputeAsync()
    {
        if (_vm is null || _settings is null) return;

        // A second run supersedes the first: the inputs it was started with are
        // no longer on screen, so its answer would be for a question nobody is
        // asking any more.
        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;

        PersistInputs();

        RecomputeButton.IsEnabled = false;
        BusyText.Text = "Fetching terrain…";
        BusyOverlay.IsVisible = true;

        try
        {
            var terrain = await SharedTerrain.Tiles.SampleAsync(_from, _to, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            // Fetched around the midpoint, with room for the whole path: one
            // extract covers the line rather than one per end.
            if (_settings.BuildingLossEnabled)
            {
                BusyText.Text = "Reading buildings…";
                var middle = Geodesy.Interpolate(_from, _to, 0.5);
                double reach = Geodesy.DistanceM(_from, _to) / 2 + 200;
                _buildingExtract = await SharedTerrain
                    .BuildingsAroundAsync(_settings, middle, reach, cts.Token)
                    .ConfigureAwait(true);
                _buildings = _buildingExtract.Index;
            }
            else
            {
                _buildingExtract = BuildingExtract.None;
                _buildings = BuildingIndex.Empty;
            }

            if (cts.IsCancellationRequested) return;

            if (terrain is null)
            {
                BusyText.Text = "No elevation data for this path.";
                SourceText.Text = TerrainTiles.Attribution;
                return;
            }

            BusyOverlay.IsVisible = false;
            Render(terrain);
        }
        catch (OperationCanceledException)
        {
            // The window closed, or a newer run took over.
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
            {
                RecomputeButton.IsEnabled = true;
                _running = null;
            }
            cts.Dispose();
        }
    }

    private void Render(TerrainPath terrain)
    {
        if (_vm is null || _settings is null) return;

        var (sf, bwKhz, _) = _vm.EffectiveLoraParams;
        double frequency = _vm.CenterFreqMHz;

        var profile = LinkProfile.Build(
            terrain.Ground,
            _settings.LinkProfileMyAntennaM,
            _settings.LinkProfilePeerAntennaM,
            frequency);

        Chart.Show(profile, _units, "This station", PeerName);

        DistanceText.Text = DisplayUnits.FormatShortDistance(profile.DistanceM, _units);

        (VerdictText.Text, VerdictText.Foreground) =
            !profile.HasLineOfSight ? ("Obstructed", Blocked)
            : !profile.IsFresnelClear ? ("Grazing", Marginal)
            : ("Clear", Clear);

        // How short the path is, and by how much: the second number is what a
        // taller mast or a different site has to buy.
        ClearanceText.Text = double.IsInfinity(profile.WorstClearanceRatio)
            ? "—"
            : profile.MetresShortOfClearance > 0
                ? $"{profile.WorstClearanceRatio:0.00} × F1  (+{DisplayUnits.FormatShortDistance(profile.MetresShortOfClearance, _units)})"
                : $"{profile.WorstClearanceRatio:0.00} × F1";
        ClearanceText.Foreground = profile.IsFresnelClear ? Clear : Marginal;

        WorstText.Text =
            $"{DisplayUnits.FormatShortDistance(profile.Worst.DistanceM, _units)} in, " +
            $"{DisplayUnits.FormatAltitude((int)Math.Round(profile.Worst.GroundM), _units)}";

        double fspl = LinkBudget.FreeSpacePathLossDb(profile.DistanceM, frequency);
        PathLossText.Text = $"{fspl:0.0} dB";

        var crossed = _buildings.AlongPath(_from, _to);
        double buildingLoss = SharedTerrain.LossModel(_settings).LossDb(crossed);

        // The caveat has to follow what the model actually did. Saying
        // buildings are not modelled while charging for eleven of them is
        // worse than saying nothing.
        CaveatText.Text = !_settings.BuildingLossEnabled
            ? "Terrain only. Buildings, trees and fading are not modelled, so a clear path here can still fail in the field."
            : _buildingExtract is { LookupFailed: true, Explanation: { } why }
                ? $"Terrain only — buildings are switched on, but {why}, so none were charged for. Trees and fading are never modelled."
                : crossed.Count > 0
                    ? $"Terrain and {crossed.Count} building{(crossed.Count == 1 ? "" : "s")} on this path. Trees and fading are not modelled, and the building figures are a starting point the path-loss fit is meant to correct."
                    : "Terrain only on this path — buildings are switched on, but none are mapped along it. Trees and fading are not modelled.";

        // Terrain and buildings share a tile: they are the two things in the
        // way, and a path with both has one number for what the ground costs.
        DiffractionText.Text = crossed.Count > 0
            ? $"{profile.DiffractionLossDb + buildingLoss:0.0} dB  ({crossed.Count} bldg)"
            : $"{profile.DiffractionLossDb:0.0} dB";
        DiffractionText.Foreground =
            profile.DiffractionLossDb + buildingLoss > 0 ? Marginal : Clear;

        double rxPower = LinkBudget.ReceivedPowerDbm(
            TxPowerDbm(), GainDbi(MyGainBox, _settings.LinkProfileMyGainDbi),
            GainDbi(PeerGainBox, _settings.LinkProfilePeerGainDbi),
            fspl, profile.DiffractionLossDb + buildingLoss);
        double predictedSnr = LinkBudget.SnrDb(rxPower, bwKhz);
        double margin = LinkBudget.MarginDb(rxPower, sf, bwKhz);

        PredictedText.Text = $"{predictedSnr:0.0} dB";
        MarginText.Text = $"{margin:0.0} dB";
        MarginText.Foreground = margin >= 10 ? Clear : margin > 0 ? Marginal : Blocked;

        ShowCalibrated(profile.DistanceM, rxPower, sf, bwKhz);
        ShowMeasured(predictedSnr);

        SourceText.Text = string.Join("  ·  ",
            $"Terrain zoom {terrain.Zoom} ({TerrainGrid.MetresPerPixel(terrain.Zoom, _from.Lat):0} m/px), " +
            $"{terrain.TileCount} tiles",
            terrain.Complete ? TerrainTiles.Attribution
                             : "Part of this path had no elevation data and was bridged. " + TerrainTiles.Attribution,
            _buildings.Count > 0 ? OverpassBuildings.Attribution : string.Empty);
    }

    /// <summary>
    /// The same link put through the path-loss model fitted to this station's
    /// own neighbours, when one has been applied. Everything above this is
    /// terrain and free space; this row is the clutter those two cannot see —
    /// foliage, buildings, whatever else this site is surrounded by — measured
    /// from traffic rather than assumed.
    /// </summary>
    private void ShowCalibrated(double distanceM, double freeSpaceRxPowerDbm, int sf, double bwKhz)
    {
        if (_settings is not { PathLossExponent: double exponent, PathLossOffsetDb: double offset })
        {
            CalibratedHeader.IsVisible = false;
            ClutterLabel.IsVisible = false;
            CalibratedMarginLabel.IsVisible = false;
            ClutterText.Text = string.Empty;
            CalibratedMarginText.Text = string.Empty;
            return;
        }

        var fit = new PathLossFit(exponent, offset, _settings.PathLossRmsDb ?? 0,
                                  _settings.PathLossSampleCount,
                                  ExponentFitted: _settings.PathLossExponentFitted,
                                  OffsetFitted: true,
                                  FurthestSampleM: _settings.PathLossFurthestSampleM);
        double clutter = fit.ExcessOverFreeSpaceDb(distanceM);
        double margin = LinkBudget.MarginDb(freeSpaceRxPowerDbm - clutter, sf, bwKhz);

        CalibratedHeader.IsVisible = true;
        ClutterLabel.IsVisible = true;
        CalibratedMarginLabel.IsVisible = true;

        ClutterText.Text = $"{clutter:+0.0;-0.0;0.0} dB";
        ClutterText.Foreground = clutter > 0 ? Marginal : Clear;
        CalibratedMarginText.Text = $"{margin:0.0} dB";
        CalibratedMarginText.Foreground = margin >= 10 ? Clear : margin > 0 ? Marginal : Blocked;

        // A fit whose exponent was held at free space is a reading taken at one
        // range. It is worth showing at this link's range, and worth saying so.
        bool beyondEvidence = fit.CredibleRangeM > 0 && distanceM > fit.CredibleRangeM;
        if (!fit.ExponentFitted || beyondEvidence) ClutterText.Foreground = Blocked;

        ToolTip.SetTip(CalibratedHeader,
            $"n = {exponent:0.00}, offset {offset:+0.0;-0.0;0.0} dB, fitted to " +
            $"{_settings.PathLossSampleCount} neighbour{(_settings.PathLossSampleCount == 1 ? "" : "s")}" +
            (_settings.PathLossFittedUtc is DateTime when
                ? $" on {when.ToLocalTime():d}"
                : string.Empty) +
            (fit.ExponentFitted
                ? string.Empty
                : ". The exponent was never measured — this is one range's reading carried across.") +
            (beyondEvidence
                ? $" This link is past the {DisplayUnits.FormatShortDistance(fit.CredibleRangeM, _units)} " +
                  "the fit has evidence for."
                : string.Empty));
    }

    /// <summary>The measured side of the comparison. Only a direct neighbour's
    /// reading belongs beside a prediction for this path: a reading that
    /// arrived through a relay measured the last hop, which is somewhere
    /// else.</summary>
    private void ShowMeasured(double predictedSnr)
    {
        if (_node?.SnrDb is not float measured)
        {
            // A place on the map has never transmitted, so there is nothing to
            // compare the prediction against and never will be — a different
            // thing from a node that simply has not been heard yet.
            MeasuredCaption.Text = "Measured SNR";
            MeasuredText.Text = "—";
            MeasuredText.Foreground = Foreground;
            ToolTip.SetTip(MeasuredText, _node is null
                ? "Nothing transmits from a place on the map, so this path is prediction only"
                : "Nothing heard from this node yet");
            return;
        }

        ToolTip.SetTip(MeasuredText, null);

        if (_node.HopsAway is not (null or 0))
        {
            MeasuredCaption.Text = $"Measured ({_node.HopsAway} hops away)";
            MeasuredText.Text = $"{measured:0.0} dB";
            MeasuredText.Foreground = Foreground;
            return;
        }

        double delta = measured - predictedSnr;
        MeasuredCaption.Text = "Measured SNR (direct)";
        MeasuredText.Text = $"{measured:0.0} dB  ({delta:+0.0;-0.0;0.0})";

        // A measurement close to the prediction means the terrain model
        // accounts for the path. A large shortfall is the clutter this model
        // does not carry — trees, walls, a bad antenna — and is the number to
        // act on.
        MeasuredText.Foreground = Math.Abs(delta) <= 6 ? Clear : delta < 0 ? Marginal : Foreground;
    }

    // -- Inputs -------------------------------------------------------------

    /// <summary>Takes the panel's values into settings, leaving anything that
    /// will not parse at its stored value and putting that value back in the
    /// box, so the panel always shows what the numbers were computed from.
    /// </summary>
    private void PersistInputs()
    {
        if (_settings is null) return;

        _settings.LinkProfileMyAntennaM = HeightM(MyHeightBox, _settings.LinkProfileMyAntennaM);
        _settings.LinkProfilePeerAntennaM = HeightM(PeerHeightBox, _settings.LinkProfilePeerAntennaM);
        _settings.LinkProfileMyGainDbi = GainDbi(MyGainBox, _settings.LinkProfileMyGainDbi);
        _settings.LinkProfilePeerGainDbi = GainDbi(PeerGainBox, _settings.LinkProfilePeerGainDbi);

        MyHeightBox.Text = FormatHeight(_settings.LinkProfileMyAntennaM);
        PeerHeightBox.Text = FormatHeight(_settings.LinkProfilePeerAntennaM);
        MyGainBox.Text = _settings.LinkProfileMyGainDbi.ToString("0.##", CultureInfo.InvariantCulture);
        PeerGainBox.Text = _settings.LinkProfilePeerGainDbi.ToString("0.##", CultureInfo.InvariantCulture);
        TxPowerBox.Text = TxPowerDbm().ToString("0.#", CultureInfo.InvariantCulture);

        _settings.Save();
    }

    private const double FeetPerMetre = 3.28083989501312;

    private string FormatHeight(double metres) =>
        (DisplayUnits.IsImperial(_units) ? metres * FeetPerMetre : metres)
            .ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>A height box in metres. Clamped rather than refused: a negative
    /// antenna is not a typo worth a dialog, and an absurd mast would only
    /// stretch the chart.</summary>
    private double HeightM(TextBox box, double fallback)
    {
        if (!TryNumber(box, out double value)) return fallback;
        if (DisplayUnits.IsImperial(_units)) value /= FeetPerMetre;
        return Math.Clamp(value, 0, 500);
    }

    private static double GainDbi(TextBox box, double fallback) =>
        TryNumber(box, out double value) ? Math.Clamp(value, -20, 30) : fallback;

    private double TxPowerDbm() =>
        TryNumber(TxPowerBox, out double value) ? Math.Clamp(value, -20, 40) : DefaultTxPowerDbm();

    private static bool TryNumber(TextBox box, out double value) =>
        double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
