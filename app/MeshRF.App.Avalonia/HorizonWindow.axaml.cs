// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using MeshRF.Map;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The 360° skyline from this station, with the mesh's nodes plotted against
/// it.
///
/// Geometry rather than radio: it answers what the antenna can see, which is
/// the question behind where to put a node and how high to put it. A node
/// standing above the skyline may still be out of range, and one just under it
/// may still be heard by diffraction — the link profile is the authority on
/// either. What this shows is which neighbours are hidden by one ridge rather
/// than by distance, and how much of a mast it would take to clear it.
/// </summary>
public partial class HorizonWindow : Window
{
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#66BB6A"));
    private static readonly IBrush Caution = new SolidColorBrush(Color.Parse("#FFB74D"));

    /// <summary>Ranges the sweep offers, in metres. A near radius is read at a
    /// deep zoom where a garden wall is a pixel or two across; a far one steps
    /// back to fit the disc in a sensible number of tiles.</summary>
    private static readonly (string Label, double Metres)[] Radii =
    [
        ("2 km", 2_000),
        ("5 km", 5_000),
        ("15 km", 15_000),
        ("40 km", 40_000),
    ];

    private RadioViewModel? _vm;
    private AppSettings? _settings;
    private GeoPoint _centre;
    private UnitSystem _units;
    private double _radiusM = 15_000;
    private CancellationTokenSource? _running;

    public HorizonWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _running?.Cancel();
    }

    public static async Task ShowForAsync(
        Window owner, RadioViewModel vm, AppSettings settings, double lat, double lon)
    {
        var window = new HorizonWindow
        {
            _vm = vm,
            _settings = settings,
            _centre = new GeoPoint(lat, lon),
            _units = vm.CurrentUnitSystem,
        };
        window.Prepare();
        await window.ShowDialog(owner);
    }

    private void Prepare()
    {
        if (_vm is null || _settings is null) return;

        string heightUnit = DisplayUnits.AltitudeUnitShort(_units);
        MyHeightLabel.Text = $"My antenna ({heightUnit})";
        PeerHeightLabel.Text = $"Peer antenna ({heightUnit})";
        MyHeightBox.Text = FormatHeight(_settings.LinkProfileMyAntennaM);
        PeerHeightBox.Text = FormatHeight(_settings.LinkProfilePeerAntennaM);

        RadiusCombo.ItemsSource = Radii.Select(r => r.Label).ToList();
        RadiusCombo.SelectedIndex = Array.FindIndex(Radii, r => r.Metres == _radiusM);

        AttributionText.Text = TerrainTiles.Attribution;
        HeaderText.Text = $"Skyline from {_centre.Lat:F5}, {_centre.Lon:F5}";

        _ = SweepAsync();
    }

    private void OnResweep(object? sender, RoutedEventArgs e) => _ = SweepAsync();

    /// <summary>Changing the radius changes the answer, so it re-sweeps rather
    /// than waiting to be asked. Ignored while the window is still being set
    /// up, which would otherwise start a second sweep on open.</summary>
    private void OnRadiusChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (RadiusCombo.SelectedIndex is int index and >= 0 && index < Radii.Length)
        {
            if (Radii[index].Metres == _radiusM) return;
            _radiusM = Radii[index].Metres;
            _ = SweepAsync();
        }
    }

    private async Task SweepAsync()
    {
        if (_vm is null || _settings is null) return;

        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;

        PersistInputs();

        ResweepButton.IsEnabled = false;
        BusyText.Text = "Reading terrain…";
        BusyOverlay.IsVisible = true;

        var options = new HorizonOptions(
            _centre, _settings.LinkProfileMyAntennaM, _radiusM);
        double peerAntennaM = _settings.LinkProfilePeerAntennaM;

        // Nodes with a position, named as the map names them. Read on the UI
        // thread: the collection belongs to it.
        var nodes = _vm.Nodes
            .Where(n => n.NodeNum != _vm.MyNodeNum && n.Latitude is not null && n.Longitude is not null)
            .Select(n => (
                Name: !string.IsNullOrWhiteSpace(n.LongName) ? n.LongName
                    : !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName
                    : $"!{n.NodeNum:x8}",
                At: new GeoPoint(n.Latitude!.Value, n.Longitude!.Value)))
            .ToList();

        try
        {
            var swept = await Task.Run<(TerrainArea Area, HorizonProfile Profile,
                                        IReadOnlyList<HorizonTarget> Targets)?>(async () =>
            {
                var area = await SharedTerrain.Tiles
                    .LoadAreaAsync(_centre, _radiusM, cts.Token)
                    .ConfigureAwait(false);
                if (area is null) return null;

                var profile = HorizonPanorama.Build(area.Grid, options);
                if (profile is null) return null;

                var targets = HorizonPanorama.Place(profile, area.Grid, nodes, peerAntennaM);
                return (area, profile, targets);
            }, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;

            if (swept is not { Profile: { } profile, Targets: { } targets, Area: { } area })
            {
                BusyText.Text = "No elevation data around this location.";
                return;
            }

            BusyOverlay.IsVisible = false;
            Render(profile, targets, area);
        }
        catch (OperationCanceledException)
        {
            // The window closed, or a newer sweep took over.
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
            {
                ResweepButton.IsEnabled = true;
                _running = null;
            }
            cts.Dispose();
        }
    }

    private void Render(
        HorizonProfile profile, IReadOnlyList<HorizonTarget> targets, TerrainArea area)
    {
        Chart.Show(profile, targets, _units);

        var highest = profile.Highest;
        HighestText.Text = $"{highest.ElevationAngleDeg:+0.0;-0.0;0.0}°";
        HighestText.Foreground = highest.ElevationAngleDeg > 0 ? Caution : Good;

        HighestWhereText.Text =
            $"{Compass(highest.BearingDegrees)}, " +
            $"{DisplayUnits.FormatShortDistance(highest.DistanceM, _units)}";

        ObstructedText.Text = $"{profile.FractionObstructed * 100:0}%";
        ObstructedText.Foreground = profile.FractionObstructed > 0.25 ? Caution : Good;

        int visible = targets.Count(t => t.IsVisible);
        NodesText.Text = targets.Count == 0 ? "—" : $"{visible} of {targets.Count}";
        NodesText.Foreground = targets.Count == 0 || visible == targets.Count ? Good : Caution;

        // Nodes outside the sweep are left off the chart entirely, so say so
        // rather than let the count read as the whole mesh.
        int positioned = _vm?.Nodes.Count(n =>
            n.NodeNum != _vm.MyNodeNum && n.Latitude is not null && n.Longitude is not null) ?? 0;
        int beyond = positioned - targets.Count;

        SourceText.Text = string.Join("  ·  ",
            $"Antenna at {DisplayUnits.FormatAltitude((int)Math.Round(profile.ObserverElevationM), _units)} " +
            $"({profile.Points.Count} bearings, terrain zoom {area.Zoom}, {area.TileCount} tiles)",
            beyond > 0
                ? $"{beyond} node{(beyond == 1 ? "" : "s")} beyond {DisplayUnits.FormatShortDistance(_radiusM, _units)} not shown"
                : "every positioned node is within range of this sweep",
            area.Complete ? TerrainTiles.Attribution
                          : "Some terrain here could not be read. " + TerrainTiles.Attribution);
    }

    private static string Compass(double bearingDegrees)
    {
        string[] points = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
                           "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];
        int index = (int)Math.Round(((bearingDegrees % 360) + 360) % 360 / 22.5) % points.Length;
        return $"{points[index]} {bearingDegrees:0}°";
    }

    // -- Inputs -------------------------------------------------------------

    private const double FeetPerMetre = 3.28083989501312;

    private void PersistInputs()
    {
        if (_settings is null) return;

        _settings.LinkProfileMyAntennaM = HeightM(MyHeightBox, _settings.LinkProfileMyAntennaM);
        _settings.LinkProfilePeerAntennaM = HeightM(PeerHeightBox, _settings.LinkProfilePeerAntennaM);

        MyHeightBox.Text = FormatHeight(_settings.LinkProfileMyAntennaM);
        PeerHeightBox.Text = FormatHeight(_settings.LinkProfilePeerAntennaM);

        _settings.Save();
    }

    private string FormatHeight(double metres) =>
        (DisplayUnits.IsImperial(_units) ? metres * FeetPerMetre : metres)
            .ToString("0.#", CultureInfo.InvariantCulture);

    private double HeightM(TextBox box, double fallback)
    {
        if (!double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                             out double value))
            return fallback;
        if (DisplayUnits.IsImperial(_units)) value /= FeetPerMetre;
        return Math.Clamp(value, 0, 500);
    }
}
