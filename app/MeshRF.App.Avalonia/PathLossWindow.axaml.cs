// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshRF.Map;
using MeshRF.Mesh;

namespace MeshRF.AvaloniaApp;

/// <summary>One neighbour's row in the table. <see cref="Include"/> is the only
/// thing the user changes; everything else is what the measurement said.
/// </summary>
public sealed partial class PathLossRow : ObservableObject
{
    [ObservableProperty]
    private bool _include = true;

    [ObservableProperty]
    private string _residual = "—";

    public PathLossRow(PathLossObservation observation, UnitSystem units, double frequencyMhz)
    {
        Observation = observation;
        Name = observation.Name;
        Range = DisplayUnits.FormatShortDistance(observation.DistanceM, units);
        Snr = $"{observation.MeasuredSnrDb:0.0} dB";
        double inTheWay = observation.DiffractionLossDb + observation.BuildingLossDb;
        TerrainLoss = inTheWay > 0
            ? (observation.BuildingLossDb > 0
                ? $"{inTheWay:0.0} dB  (bldg)"
                : $"{inTheWay:0.0} dB")
            : "—";

        ExcessDb = observation.PropagationLossDb
                 - LinkBudget.FreeSpacePathLossDb(observation.DistanceM, frequencyMhz);
        Excess = $"{ExcessDb:+0.0;-0.0;0.0} dB";

        // A path whose terrain could not be read would credit that terrain's
        // loss to clutter, so it starts out of the fit rather than in it.
        Include = observation.TerrainKnown;
    }

    public PathLossObservation Observation { get; }
    public string Name { get; }
    public string Range { get; }
    public string Snr { get; }
    public string TerrainLoss { get; }
    public string Excess { get; }
    public double ExcessDb { get; }
}

/// <summary>
/// Fits a log-distance path-loss model to every direct neighbour this station
/// has heard, so link predictions can carry the clutter loss the terrain model
/// does not.
///
/// This is the survey MeshLab RF sends someone out to walk, done from traffic
/// that has already arrived. What it buys over a walked survey is that it keeps
/// itself current; what it gives up is control of the measurement — each point
/// is one packet's SNR from a node whose transmit power is assumed and whose
/// position may be deliberately fuzzed. Hence the residual column: the fit is
/// meant to be looked at and pruned, not taken on faith.
/// </summary>
public partial class PathLossWindow : Window
{
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#66BB6A"));
    private static readonly IBrush Caution = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#EF5350"));

    private readonly ObservableCollection<PathLossRow> _rows = [];

    private RadioViewModel? _vm;
    private AppSettings? _settings;
    private GeoPoint _home;
    private UnitSystem _units;
    private double _frequencyMhz;
    private double _bandwidthKhz;
    private PathLossFit? _fit;
    private CancellationTokenSource? _running;

    public PathLossWindow()
    {
        InitializeComponent();
        NodeGrid.ItemsSource = _rows;
        Closed += (_, _) => _running?.Cancel();
    }

    public static async Task ShowForAsync(
        Window owner, RadioViewModel vm, AppSettings settings, double homeLat, double homeLon)
    {
        var window = new PathLossWindow
        {
            _vm = vm,
            _settings = settings,
            _home = new GeoPoint(homeLat, homeLon),
            _units = vm.CurrentUnitSystem,
        };
        window.Prepare();
        await window.ShowDialog(owner);
    }

    private void Prepare()
    {
        if (_vm is null || _settings is null) return;

        var (_, bwKhz, _) = _vm.EffectiveLoraParams;
        _frequencyMhz = _vm.CenterFreqMHz;
        _bandwidthKhz = bwKhz;

        string heightUnit = DisplayUnits.AltitudeUnitShort(_units);
        MyHeightLabel.Text = $"My antenna ({heightUnit})";
        PeerHeightLabel.Text = $"Peer antenna ({heightUnit})";
        MyHeightBox.Text = FormatHeight(_settings.LinkProfileMyAntennaM);
        PeerHeightBox.Text = FormatHeight(_settings.LinkProfilePeerAntennaM);
        PeerTxPowerBox.Text =
            _settings.PathLossAssumedPeerTxPowerDbm.ToString("0.#", CultureInfo.InvariantCulture);
        MyGainBox.Text = _settings.LinkProfileMyGainDbi.ToString("0.##", CultureInfo.InvariantCulture);
        PeerGainBox.Text = _settings.LinkProfilePeerGainDbi.ToString("0.##", CultureInfo.InvariantCulture);

        ClearButton.IsEnabled = _settings.PathLossExponent is not null;

        ShowSurveyCount(prefer: 1);

        _ = MeasureAsync();
    }

    /// <summary>Relabels the source picker with what the survey now holds, and
    /// enables the actions that need readings to act on.</summary>
    /// <param name="prefer">Which source to select if the survey has readings.
    /// Defaults to whatever is already selected, so relabelling after an import
    /// does not move the user off the source they were looking at.</param>
    private void ShowSurveyCount(int? prefer = null)
    {
        int surveyed = _vm!.Survey.Read().Count;
        int wanted = prefer ?? Math.Max(SourceCombo.SelectedIndex, 0);

        // Replacing the items resets the selection, which would refit twice
        // over: once on the reset and once on the restore.
        _relabelling = true;
        try
        {
            SourceCombo.ItemsSource = new[]
            {
                "Node list",
                surveyed > 0 ? $"Survey ({surveyed:N0})" : "Survey (empty)",
            };
            SourceCombo.SelectedIndex = surveyed > 0 ? wanted : 0;
        }
        finally
        {
            _relabelling = false;
        }

        ExportSurveyButton.IsEnabled = surveyed > 0;
        ClearSurveyButton.IsEnabled = surveyed > 0;
    }

    private bool _relabelling;

    private void OnRemeasure(object? sender, RoutedEventArgs e) => _ = MeasureAsync();

    /// <summary>Which readings to fit. Changing it refits from scratch, since
    /// the two sources produce entirely different observations.</summary>
    private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null && IsLoaded && !_relabelling) _ = MeasureAsync();
    }

    private bool UsingSurvey => SourceCombo.SelectedIndex == 1;

    private async Task MeasureAsync()
    {
        if (_vm is null || _settings is null) return;

        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;

        PersistInputs();

        // Fetched around this station, wide enough to cover the neighbours the
        // fit will reach for. Buildings the fit can name are taken out of it,
        // the same as terrain — otherwise the exponent absorbs them and then
        // charges for them a second time wherever it is applied.
        if (_settings.BuildingLossEnabled && _buildings.Count == 0)
        {
            BusyText.Text = "Reading buildings…";
            BusyOverlay.IsVisible = true;
            var extract = await SharedTerrain
                .BuildingsAroundAsync(_settings, _home, OverpassBuildings.MaxRadiusM, cts.Token)
                .ConfigureAwait(true);
            _buildings = extract.Index;
            if (cts.IsCancellationRequested) return;
        }
        else if (!_settings.BuildingLossEnabled)
        {
            _buildings = BuildingIndex.Empty;
        }

        var options = Options();

        if (UsingSurvey)
        {
            await MeasureSurveyAsync(options, cts).ConfigureAwait(true);
            return;
        }

        var candidates = PathLossSurvey.Candidates(_vm.Nodes, options, _vm.MyNodeNum);
        if (candidates.Count == 0)
        {
            _rows.Clear();
            Chart.Show([], null, _frequencyMhz, _units);
            BusyText.Text = "No direct neighbours with a position and a signal reading yet.";
            BusyOverlay.IsVisible = true;
            StatusText.Text = "Nothing to calibrate from.";
            ApplyButton.IsEnabled = false;
            return;
        }

        RemeasureButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        BusyOverlay.IsVisible = true;
        BusyText.Text = $"Reading terrain to {candidates.Count} neighbours…";
        StatusText.Text = string.Empty;

        var progress = new Progress<int>(done =>
            BusyText.Text = $"Reading terrain… {done} of {candidates.Count}");

        try
        {
            var survey = new PathLossSurvey(SharedTerrain.Tiles);
            var observations = await survey
                .MeasureAsync(candidates, options, progress, cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            _rows.Clear();
            foreach (var observation in observations)
            {
                var row = new PathLossRow(observation, _units, _frequencyMhz);
                row.PropertyChanged += OnRowChanged;
                _rows.Add(row);
            }

            BusyOverlay.IsVisible = false;
            Refit();
        }
        catch (OperationCanceledException)
        {
            // The window closed, or a newer run took over.
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
            {
                RemeasureButton.IsEnabled = true;
                _running = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Fits from the recorded survey instead of the node list.
    ///
    /// The difference that matters is not the count. A node list offers one
    /// packet per neighbour at whatever range it happens to sit, so a station
    /// whose neighbours share a mast can never measure a falloff; a survey
    /// offers many packets per neighbour across every range it was carried
    /// through, which both averages away the fading in a single reading and
    /// gives the fit the lever arm it needs.
    /// </summary>
    private async Task MeasureSurveyAsync(PathLossSurveyOptions options, CancellationTokenSource cts)
    {
        var samples = _vm!.Survey.Read();
        var bins = SurveyLog.Bin(samples);

        var peers = _vm.Nodes
            .Where(n => n.Latitude is not null && n.Longitude is not null)
            .GroupBy(n => n.NodeNum)
            .ToDictionary(
                g => g.Key,
                g => (Name: !string.IsNullOrWhiteSpace(g.First().LongName)
                          ? g.First().LongName
                          : $"!{g.Key:x8}",
                      At: new GeoPoint(g.First().Latitude!.Value, g.First().Longitude!.Value)));

        var usable = bins.Where(b => peers.ContainsKey(b.NodeNum)).ToList();
        if (usable.Count == 0)
        {
            _rows.Clear();
            Chart.Show([], null, _frequencyMhz, _units);
            BusyText.Text = samples.Count == 0
                ? "No survey recorded yet. Turn recording on and drive."
                : "The survey has no readings from a node whose position is known.";
            BusyOverlay.IsVisible = true;
            StatusText.Text = $"{samples.Count:N0} readings recorded, none usable yet.";
            ApplyButton.IsEnabled = false;
            return;
        }

        BusyText.Text = $"Reading terrain to {usable.Select(b => b.NodeNum).Distinct().Count()} peers…";
        var progress = new Progress<int>(done =>
            BusyText.Text = $"Reading terrain… {done} of {usable.Count}");

        var survey = new PathLossSurvey(SharedTerrain.Tiles);
        var observations = await survey
            .MeasureBinsAsync(usable, peers, options, progress, cts.Token)
            .ConfigureAwait(true);
        if (cts.IsCancellationRequested) return;

        _rows.Clear();
        foreach (var observation in observations)
        {
            var row = new PathLossRow(observation, _units, _frequencyMhz);
            row.PropertyChanged += OnRowChanged;
            _rows.Add(row);
        }

        BusyOverlay.IsVisible = false;
        Refit();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PathLossRow.Include)) Refit();
    }

    /// <summary>Refits from whichever rows are ticked and redraws everything
    /// that depends on the fit. Cheap enough to run on every tick, which is why
    /// pruning an outlier is immediate rather than a separate action.</summary>
    private void Refit()
    {
        var used = _rows.Where(r => r.Include).ToList();
        _fit = PathLossFit.Fit(used.Select(r => r.Observation.ToSample()).ToList(), _frequencyMhz);

        Chart.Show(_rows.Select(r => (r.Observation, r.Include)).ToList(), _fit, _frequencyMhz, _units);

        foreach (var row in _rows)
            row.Residual = _fit is null
                ? "—"
                : $"{_fit.ResidualDb(row.Observation.ToSample(), _frequencyMhz):+0.0;-0.0;0.0} dB";

        if (_fit is not { } fit)
        {
            ExponentText.Text = "—";
            ExponentNoteText.Text = "No neighbours selected.";
            OffsetText.Text = "—";
            RmsText.Text = "—";
            CountText.Text = "0";
            ApplyButton.IsEnabled = false;
            StatusText.Text = "Nothing selected to fit.";
            return;
        }

        ExponentText.Text = $"n = {fit.Exponent:0.00}";
        ExponentText.Foreground = !fit.IsPlausible ? Bad
            : fit.Exponent <= 2.5 ? Good
            : Caution;

        ExponentNoteText.Text =
            !fit.ExponentFitted
                ? $"Held at free space: {(EnoughSamples(fit) ? "the neighbours are all at much the same range" : "too few neighbours")} to measure a falloff. The offset carries what they show."
            : !fit.IsPlausible
                ? "Outside the range real environments produce — check the outliers in the table."
            : fit.Exponent <= 2.4 ? "Open ground: signal falls off close to free space."
            : fit.Exponent <= 3.2 ? "Light clutter, as trees or scattered buildings give."
            : "Heavy clutter: range falls away quickly here.";

        OffsetText.Text = $"{fit.OffsetDb:+0.0;-0.0;0.0} dB";
        RmsText.Text = $"{fit.RmsResidualDb:0.0} dB";
        CountText.Text = fit.SampleCount.ToString(CultureInfo.CurrentCulture);
        RmsText.Foreground = fit.RmsResidualDb <= 6 ? Good : fit.RmsResidualDb <= 12 ? Caution : Bad;

        ApplyButton.IsEnabled = true;
        StatusText.Text =
            $"{_rows.Count} direct neighbour{(_rows.Count == 1 ? "" : "s")} measured, " +
            $"{fit.SampleCount} in the fit  ·  at 5 km this model predicts " +
            $"{fit.ExcessOverFreeSpaceDb(5000):+0.0;-0.0;0.0} dB against free space";

        // Whether the exponent was held back by the spread of ranges rather
        // than by the count, which are different things to tell the user.
        static bool EnoughSamples(PathLossFit f) =>
            f.SampleCount >= PathLossFit.MinSamplesForExponent;
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_settings is null || _fit is not { } fit) return;

        _settings.PathLossExponent = fit.Exponent;
        _settings.PathLossOffsetDb = fit.OffsetDb;
        _settings.PathLossRmsDb = fit.RmsResidualDb;
        _settings.PathLossSampleCount = fit.SampleCount;
        _settings.PathLossExponentFitted = fit.ExponentFitted;
        _settings.PathLossFurthestSampleM = fit.FurthestSampleM;
        _settings.PathLossFittedUtc = DateTime.UtcNow;
        _settings.Save();

        ClearButton.IsEnabled = true;
        Close();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        if (_settings is null) return;

        _settings.PathLossExponent = null;
        _settings.PathLossOffsetDb = null;
        _settings.PathLossRmsDb = null;
        _settings.PathLossSampleCount = 0;
        _settings.PathLossExponentFitted = false;
        _settings.PathLossFurthestSampleM = 0;
        _settings.PathLossFittedUtc = null;
        _settings.Save();

        ClearButton.IsEnabled = false;
        StatusText.Text = "Calibration cleared. Link predictions are back to terrain only.";
    }

    // -- The survey file ----------------------------------------------------

    private static FilePickerFileType[] SurveyFileTypes =>
    [
        new FilePickerFileType("Survey CSV") { Patterns = ["*.csv"] },
        new FilePickerFileType("All files") { Patterns = ["*"] },
    ];

    /// <summary>
    /// Merges a survey recorded elsewhere into this one.
    ///
    /// The workflow this exists for: recording happens in a vehicle, on
    /// whatever machine has a GPS attached, and the fit is read back at the
    /// station the survey was collected for. Merging rather than replacing
    /// means several drives accumulate, which is exactly what the fit wants —
    /// each one adds range spread the last did not have.
    /// </summary>
    private async void OnImportSurvey(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import survey",
            AllowMultiple = false,
            FileTypeFilter = SurveyFileTypes,
        });
        if (picked.Count == 0) return;

        if (picked[0].TryGetLocalPath() is not { } path)
        {
            StatusText.Text = "That file is not one this machine can read directly.";
            return;
        }

        SurveyLog.SurveyImport result;
        try
        {
            result = _vm.Survey.Import(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
            return;
        }

        ShowSurveyCount(prefer: 1);
        StatusText.Text = Describe(result);

        if (result.Added > 0) _ = MeasureAsync();

        static string Describe(SurveyLog.SurveyImport r)
        {
            if (r.Added == 0 && r.Duplicates == 0)
                return r.Unreadable > 0
                    ? $"Nothing imported — none of the {r.Unreadable:N0} lines in that file were survey readings."
                    : "That file held no readings.";

            string added = $"Imported {r.Added:N0} reading{(r.Added == 1 ? "" : "s")}";
            if (r.Duplicates > 0) added += $", skipping {r.Duplicates:N0} already held";
            if (r.Unreadable > 0) added += $", and {r.Unreadable:N0} line{(r.Unreadable == 1 ? "" : "s")} that could not be read";
            return added + ".";
        }
    }

    private async void OnExportSurvey(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        int count = _vm.Survey.Read().Count;
        if (count == 0)
        {
            StatusText.Text = "Nothing recorded to export.";
            return;
        }

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export survey",
            SuggestedFileName = $"meshrf-survey-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = SurveyFileTypes,
        });
        if (file is null) return;

        if (file.TryGetLocalPath() is not { } path)
        {
            StatusText.Text = "That location is not one this machine can write to directly.";
            return;
        }

        StatusText.Text = _vm.Survey.Export(path)
            ? $"Exported {count:N0} reading{(count == 1 ? "" : "s")} to {file.Name}."
            : "Export failed — the file could not be written.";
    }

    private async void OnClearSurvey(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        int count = _vm.Survey.Read().Count;
        if (count == 0) return;

        if (!await ConfirmDialog.ConfirmAsync(this, "Clear survey",
                $"Discard all {count:N0} recorded readings? Export them first if you want to keep them.",
                "Clear"))
            return;

        _vm.Survey.Clear();
        ShowSurveyCount(prefer: 0);
        StatusText.Text = "Survey cleared.";
        _ = MeasureAsync();
    }

    private BuildingIndex _buildings = BuildingIndex.Empty;

    private PathLossSurveyOptions Options()
    {
        var settings = _settings!;
        return new PathLossSurveyOptions(
            Home: _home,
            MyAntennaM: settings.LinkProfileMyAntennaM,
            PeerAntennaM: settings.LinkProfilePeerAntennaM,
            MyGainDbi: settings.LinkProfileMyGainDbi,
            PeerGainDbi: settings.LinkProfilePeerGainDbi,
            AssumedPeerTxPowerDbm: settings.PathLossAssumedPeerTxPowerDbm,
            FrequencyMhz: _frequencyMhz,
            BandwidthKhz: _bandwidthKhz,
            Buildings: _buildings,
            BuildingLoss: SharedTerrain.LossModel(settings));
    }

    // -- Inputs -------------------------------------------------------------

    private const double FeetPerMetre = 3.28083989501312;

    private void PersistInputs()
    {
        if (_settings is null) return;

        _settings.LinkProfileMyAntennaM = HeightM(MyHeightBox, _settings.LinkProfileMyAntennaM);
        _settings.LinkProfilePeerAntennaM = HeightM(PeerHeightBox, _settings.LinkProfilePeerAntennaM);
        _settings.PathLossAssumedPeerTxPowerDbm =
            TryNumber(PeerTxPowerBox, out double tx) ? Math.Clamp(tx, -20, 40)
                                                     : _settings.PathLossAssumedPeerTxPowerDbm;
        _settings.LinkProfileMyGainDbi = GainDbi(MyGainBox, _settings.LinkProfileMyGainDbi);
        _settings.LinkProfilePeerGainDbi = GainDbi(PeerGainBox, _settings.LinkProfilePeerGainDbi);

        MyHeightBox.Text = FormatHeight(_settings.LinkProfileMyAntennaM);
        PeerHeightBox.Text = FormatHeight(_settings.LinkProfilePeerAntennaM);
        PeerTxPowerBox.Text =
            _settings.PathLossAssumedPeerTxPowerDbm.ToString("0.#", CultureInfo.InvariantCulture);
        MyGainBox.Text = _settings.LinkProfileMyGainDbi.ToString("0.##", CultureInfo.InvariantCulture);
        PeerGainBox.Text = _settings.LinkProfilePeerGainDbi.ToString("0.##", CultureInfo.InvariantCulture);

        _settings.Save();
    }

    private string FormatHeight(double metres) =>
        (DisplayUnits.IsImperial(_units) ? metres * FeetPerMetre : metres)
            .ToString("0.#", CultureInfo.InvariantCulture);

    private double HeightM(TextBox box, double fallback)
    {
        if (!TryNumber(box, out double value)) return fallback;
        if (DisplayUnits.IsImperial(_units)) value /= FeetPerMetre;
        return Math.Clamp(value, 0, 500);
    }

    private static double GainDbi(TextBox box, double fallback) =>
        TryNumber(box, out double value) ? Math.Clamp(value, -20, 30) : fallback;

    private static bool TryNumber(TextBox box, out double value) =>
        double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
