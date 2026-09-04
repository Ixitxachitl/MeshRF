// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MeshRF.Map;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// A self-contained OpenStreetMap "slippy map". Standard 256px Web-Mercator
/// tiles are fetched over HTTP and cached on disk, so the control needs no
/// browser runtime — only a connection for first-time tile loads. Plots the
/// home location, every node that reports a position, and every waypoint with
/// its circular and/or rectangular geofence.
///
/// Everything is drawn immediate-mode into a single
/// <see cref="Render(DrawingContext)"/> rather than as retained
/// Image/Ellipse/TextBlock visuals. That drops the whole apparatus a retained
/// tree needs to stay fast — per-node visual diffing, a spatial bucket index,
/// bitmap caching, render-throttle timers and a background coordinate cache —
/// because redrawing a few hundred markers into a DrawingContext costs less
/// than reconciling a visual tree. What it costs instead is hit-testing:
/// markers are not visuals, so pointer hits are resolved against
/// <see cref="_hitTargets"/>, rebuilt each render.
/// </summary>
public sealed class MapCanvas : Control
{
    private const int TileSize = 256;
    private const int MinZoom = 2;
    private const int MaxZoom = 19;

    // -- Tile providers -----------------------------------------------------

    /// <summary>A raster basemap. <paramref name="Invert"/>,
    /// <paramref name="HueRotate"/> and <paramref name="Saturation"/> turn a
    /// light tileset into a dark one: inverting alone leaves water orange and
    /// parks magenta, so the hue is rotated back a half turn and the result
    /// desaturated to settle the palette.</summary>
    private readonly record struct TileProvider(
        string Id, string UrlTemplate, string Subdomains, string Attribution,
        double Brightness = 1.0, double Gamma = 1.0,
        bool Invert = false, double HueRotate = 0.0, double Saturation = 1.0,
        string? StyleUrl = null, int DeepestZoom = MaxZoom)
    {
        /// <summary>A vector provider names a style rather than a tile URL:
        /// the tiles it draws are rasterised here from geometry.</summary>
        public bool IsVector => StyleUrl is not null;

        /// <summary>No basemap at all. For working offline, and for looking at
        /// an overlay — a coverage field, a recorded track — without a map
        /// underneath competing with it.</summary>
        public bool IsBlank => UrlTemplate.Length == 0 && StyleUrl is null;

        public bool NeedsPostProcess =>
            Brightness != 1.0 || Gamma != 1.0 || Invert || HueRotate != 0.0 || Saturation != 1.0;
    }

    private const string GestureHint = "  ·  Ctrl+left-click send waypoint";

    /// <summary>Appended only while the position is ours to place: with the USB
    /// GPS selected the gesture is refused, so the line must not offer it.</summary>
    private const string SetLocationHint = "  ·  Ctrl+right-click set location";

    private static readonly TileProvider LightTiles = new(
        "osm", "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", "abc",
        "© OpenStreetMap contributors" + GestureHint);

    // ArcGIS tile services take the row before the column — /tile/{z}/{y}/{x}
    // — and publish no {s} subdomain pool, so Subdomains is empty.
    private const string EsriRoot = "https://server.arcgisonline.com/ArcGIS/rest/services/";
    private const string EsriPath = "/MapServer/tile/{z}/{y}/{x}";

    private static readonly TileProvider StreetTiles = new(
        "esristreet", EsriRoot + "World_Street_Map" + EsriPath, "",
        "© Esri · HERE · Garmin · USGS · © OpenStreetMap contributors" + GestureHint);

    private static readonly TileProvider SatelliteTiles = new(
        "esriimagery", EsriRoot + "World_Imagery" + EsriPath, "",
        "© Esri · Maxar · Earthstar Geographics" + GestureHint);

    /// <summary>Drawn here from vector geometry rather than fetched as
    /// pixels. The source stops at zoom 14, so deeper zooms magnify the parent
    /// tile: detail stops increasing but the drawing stays sharp, where the
    /// Esri canvas simply had nothing to serve.</summary>
    private static readonly TileProvider VectorDarkTiles = new(
        "ofmdark", string.Empty, string.Empty,
        "© OpenFreeMap · © OpenMapTiles · © OpenStreetMap contributors" + GestureHint,
        // The style paints street names in rgba(80,78,78) on a near-black
        // ground, which is fainter than this app wants over a dark shell. The
        // lift brings it level with the other dark basemap.
        Gamma: 1.4,
        StyleUrl: "https://tiles.openfreemap.org/styles/dark");

    /// <summary>Esri's Canvas basemaps stop at zoom 16 and serve a "Map data
    /// not yet available" placeholder above it, so the dark map is OSM's own
    /// tiles inverted: they carry full detail across the whole zoom range and
    /// need no key.</summary>
    /// <summary>Contour lines and hillshading, which is the basemap the RF
    /// tools want under them: a coverage ring over a topographic map shows the
    /// ridge that shaped it. Published only to zoom 17, and its tiles are a
    /// volunteer service — see <see cref="TileProvider.DeepestZoom"/>, which
    /// stops the map asking for tiles that do not exist.</summary>
    /// <summary>Draws nothing and fetches nothing.</summary>
    private static readonly TileProvider NoTiles = new(
        "none", string.Empty, string.Empty, "No basemap" + GestureHint);

    private static readonly TileProvider TopoTiles = new(
        "opentopo", "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png", "abc",
        "© OpenTopoMap (CC-BY-SA) · © OpenStreetMap contributors" + GestureHint,
        DeepestZoom: 17);

    private static readonly TileProvider DarkTiles = new(
        "osmdark", "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", "abc",
        "© OpenStreetMap contributors" + GestureHint,
        Brightness: 0.85, Invert: true, HueRotate: 180.0, Saturation: 0.6);

    /// <summary>No "Auto" entry, unlike MeshRF.App: that option exists to
    /// follow a light/dark app theme, and this app's shell is always dark.</summary>
    public static readonly IReadOnlyList<string> MapTileThemeOptions =
        ["Dark", "Dark (Vector)", "Light", "Street", "Topographic", "Satellite", "None"];

    private const string DefaultTileTheme = "Dark";

    /// <summary>The tile theme is one app-wide preference, not a per-canvas
    /// one: the picker lives on the main map, but every map drawn from this
    /// control — the location history window included — has to follow it, both
    /// when it opens and while it is open.</summary>
    private static string s_mapTileTheme = DefaultTileTheme;

    /// <summary>Raised on every canvas when the shared theme changes.
    /// Instances subscribe only while attached to a visual tree, so a closed
    /// window's canvas is not kept alive by this static.</summary>
    private static event Action? TileThemeChanged;

    private TileProvider CurrentTiles => s_mapTileTheme switch
    {
        "Dark (Vector)" => VectorDarkTiles,
        "Light" => LightTiles,
        "Topographic" => TopoTiles,
        "None" => NoTiles,
        "Street" => StreetTiles,
        "Satellite" => SatelliteTiles,
        _ => DarkTiles,
    };

    public string Attribution => CurrentTiles.Attribution +
        (_vm is null || _vm.IsManualLocationSource ? SetLocationHint : string.Empty);

    /// <summary>Raised when the tile provider or the location source changes,
    /// so the host can refresh its attribution line.</summary>
    public event Action? AttributionChanged;

    // -- Tile cache ---------------------------------------------------------

    private static readonly HttpClient s_http = CreateHttpClient();
    private static readonly string s_cacheDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeshRF", "tiles");

    // Bounded FIFO: a long session panning across the world would otherwise
    // leak decoded tile bitmaps for the life of the process.
    private const int MaxMemCacheTiles = 1000;
    private static readonly ConcurrentDictionary<string, Bitmap> s_memCache = new();
    private static readonly ConcurrentQueue<string> s_memCacheOrder = new();
    private readonly HashSet<string> _tilesInFlight = new(StringComparer.Ordinal);

    /// <summary>Non-zero while a retry pass is already scheduled.</summary>
    private int _retryScheduled;

    /// <summary>Floor on the retry wake, so a run of quick failures cannot turn
    /// into a redraw loop.</summary>
    private static readonly TimeSpan MinRetryWake = TimeSpan.FromSeconds(1);

    private static readonly string[] RetiredProviderIds =
        ["cartodark", "cartopositron", "cartovoyager", "esridark", "esrilight"];
    private static int s_retiredSweepStarted;

    // A failed tile is cached nowhere, so without this it is re-requested by the
    // very next render — several times a second while nodes are arriving — and a
    // provider that is rate-limiting us gets hammered precisely when it has
    // asked us to stop. Static like the caches: the backoff belongs to the tile
    // rather than to whichever canvas asked for it first.
    private static readonly FetchBackoff s_tileBackoff =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));

    private static HttpClient CreateHttpClient()
    {
        // A vector source tile is protobuf and compresses by around two fifths,
        // but HttpClient offers no encoding unless told to, so without this the
        // tiles arrive whole. Raster tiles are compressed already and simply
        // decline.
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // OSM's tile policy requires an identifying UA.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRF/1.0 (+https://github.com/meshrf)");
        return http;
    }

    // -- View state ---------------------------------------------------------

    private RadioViewModel? _vm;
    private double _centerLat = 39.5;
    private double _centerLon = -98.35;
    private int _zoom = 4;
    private bool _userMovedView;
    private bool _followHome;
    private bool _clusterNodes = true;

    private bool _dragging;
    private Point _lastPointer;
    private bool _dragMoved;

    /// <summary>First corner picked while drawing a rectangular geofence; null
    /// before the first click or after the box completes/cancels.</summary>
    private (double Lat, double Lon)? _pendingBboxCorner;

    // -- Palette ------------------------------------------------------------

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
    private static readonly IBrush NodeFill = new SolidColorBrush(Color.FromRgb(0x2d, 0x8c, 0xff));
    private static readonly IBrush WaypointFill = new SolidColorBrush(Color.FromRgb(0x2e, 0x7d, 0x32));
    private static readonly IBrush WaypointExpiredFill = new SolidColorBrush(Color.FromRgb(0xc6, 0x28, 0x28));
    private static readonly IBrush ClusterFill = new SolidColorBrush(Color.FromRgb(0xff, 0x8c, 0x2d));
    private static readonly IBrush GeofenceFill = new SolidColorBrush(Color.FromArgb(0x33, 0x2d, 0x8c, 0xff));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0));
    private static readonly IBrush SpiderLegBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
    private static readonly IPen MarkerOutline = new Pen(Brushes.White, 1.5);
    private static readonly IPen GeofencePen =
        new Pen(new SolidColorBrush(Color.FromRgb(0x2d, 0x8c, 0xff)), 1.5, new DashStyle([4, 3], 0));
    private static readonly IPen SpiderLegPen = new Pen(SpiderLegBrush, 1.5);
    private static readonly Typeface LabelTypeface = new(FontFamily.Default);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private const double ClusterRadiusPx = 14;
    private const double MarkerRadiusPx = 6;
    private const double ClusterBadgeRadiusPx = 12;

    // -- Hit-testing --------------------------------------------------------

    /// <summary>What sits under a given screen point. Rebuilt every render
    /// because markers aren't visuals and can't be hit-tested by the
    /// framework.</summary>
    /// <param name="Marker">The marker drawn here, so a right-click can act on
    /// it. Null for a cluster badge, which stands for several at once.</param>
    private readonly record struct HitTarget(
        double X, double Y, double Radius, string Tooltip,
        List<(RadioViewModel.MapMarker mk, double px, double py)>? Cluster,
        RadioViewModel.MapMarker? Marker = null);

    private readonly List<HitTarget> _hitTargets = new();

    // Spider (fanned-out cluster) state. Members are stored with the screen
    // position they were fanned around so a re-render can redraw them.
    private List<(RadioViewModel.MapMarker mk, double px, double py)>? _spiderMembers;
    private double _spiderCx, _spiderCy, _spiderLegLen;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        try { System.IO.Directory.CreateDirectory(s_cacheDir); } catch { /* cache is best-effort */ }
        SweepRetiredProviderTiles();
        TrimCache();
    }

    /// <summary>Ceiling for the on-disk tile cache. Rasterised tiles are a few
    /// kilobytes each, but an encoded vector source tile is closer to half a
    /// megabyte, so a session panning widely over the vector basemap is what
    /// this is really holding back.</summary>
    private const long MaxCacheBytes = 256L * 1024 * 1024;

    /// <summary>Trimming goes below the ceiling rather than to it, so a cache
    /// sitting at the limit does not re-trim on every write.</summary>
    private const double TrimTo = 0.85;

    /// <summary>How many newly written tiles trigger another check. A long
    /// session would otherwise only ever be trimmed at startup.</summary>
    private const int TrimEveryWrites = 400;

    /// <summary>Tiles cached under a provider the app no longer offers are dead
    /// bytes, and a well-travelled map leaves thousands of them. Swept once per
    /// process, off the UI thread since the delete count is unbounded.</summary>
    private static void SweepRetiredProviderTiles()
    {
        if (Interlocked.Exchange(ref s_retiredSweepStarted, 1) != 0) return;
        Task.Run(() =>
        {
            foreach (var prefix in RetiredProviderIds)
            {
                try
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(s_cacheDir, prefix + "_*.png"))
                        try { System.IO.File.Delete(f); } catch { /* in use, or gone already */ }
                }
                catch { /* cache is best-effort */ }
            }
        });
    }

    private static int s_trimRunning;
    private static int s_writesSinceTrim;

    /// <summary>Counts a tile written to disk and trims once enough have
    /// accumulated to be worth the directory walk.</summary>
    private static void CountCacheWrite()
    {
        if (Interlocked.Increment(ref s_writesSinceTrim) < TrimEveryWrites) return;
        Interlocked.Exchange(ref s_writesSinceTrim, 0);
        TrimCache();
    }

    /// <summary>Brings the cache back under its ceiling, off the UI thread and
    /// one at a time.</summary>
    private static void TrimCache()
    {
        if (Interlocked.Exchange(ref s_trimRunning, 1) != 0) return;
        Task.Run(() =>
        {
            try { TileDiskCache.Trim(s_cacheDir, MaxCacheBytes, (long)(MaxCacheBytes * TrimTo)); }
            catch { /* cache is best-effort */ }
            finally { Interlocked.Exchange(ref s_trimRunning, 0); }
        });
    }

    private static void MarkCacheHit(string file) =>
        TileDiskCache.MarkUsed(file, TimeSpan.FromDays(1));

    public void Attach(RadioViewModel vm)
    {
        if (_vm is not null)
        {
            _vm.MapDataChanged -= OnMapDataChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = vm;
        _vm.MapDataChanged += OnMapDataChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _markerCache = null;
        InvalidateVisual();
    }

    private void OnMapDataChanged(object? sender, EventArgs e)
    {
        _markerCache = null;
        if (_followHome && _vm is not null && _vm.TryGetHomeLocation(out double lat, out double lon))
        {
            _centerLat = ClampLat(lat);
            _centerLon = lon;
        }
        else if (!_userMovedView)
        {
            FitToMarkers();
        }
        // Coalesce rather than redraw now. This fires once per received packet
        // (the node filter rebuilds on every node update), and a redraw
        // re-projects and re-clusters every marker — at several hundred nodes
        // that saturates the UI thread and makes the whole panel, including
        // hovering the controls layered over it, feel sticky.
        _renderPending = true;
    }

    /// <summary>The attribution line carries the set-location gesture, which is
    /// only offered while the location source is manual.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadioViewModel.IsManualLocationSource))
            AttributionChanged?.Invoke();
    }

    private bool _renderPending;
    private DispatcherTimer? _renderThrottle;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderThrottle ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
            {
                if (!_renderPending) return;
                _renderPending = false;
                InvalidateVisual();
            });
        _renderThrottle.Start();
        TileThemeChanged += ApplyTileTheme;
        ApplyTileTheme();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _renderThrottle?.Stop();
        TileThemeChanged -= ApplyTileTheme;
    }

    // Marker projection is rebuilt only when the data changes, never per
    // frame: GetMapMarkers walks every node and builds a tooltip string for
    // each, which at several hundred nodes is far too much to redo on every
    // pan frame. Panning and zooming only re-project the cached list.
    private IReadOnlyList<RadioViewModel.MapMarker>? _markerCache;

    private IReadOnlyList<RadioViewModel.MapMarker> Markers =>
        _markerCache ??= _vm?.GetMapMarkers() ?? [];

    // -- Web-Mercator projection -------------------------------------------

    private static double LonToX(double lon, int zoom) =>
        (lon + 180.0) / 360.0 * (1 << zoom) * TileSize;

    private static double LatToY(double lat, int zoom)
    {
        var rad = lat * Math.PI / 180.0;
        var n = 1 << zoom;
        return (1.0 - Math.Log(Math.Tan(rad) + 1.0 / Math.Cos(rad)) / Math.PI) / 2.0 * n * TileSize;
    }

    private static double XToLon(double x, int zoom) =>
        x / ((1 << zoom) * TileSize) * 360.0 - 180.0;

    private static double YToLat(double y, int zoom)
    {
        var n = 1 << zoom;
        var t = Math.PI * (1.0 - 2.0 * y / (n * TileSize));
        return Math.Atan(Math.Sinh(t)) * 180.0 / Math.PI;
    }

    /// <summary>Ground resolution (m/px) at a latitude and zoom — the standard
    /// 256px-tile formula, used to size circular geofences.</summary>
    private static double MetersPerPixel(double lat, int zoom) =>
        156543.03392804062 * Math.Cos(lat * Math.PI / 180.0) / (1 << zoom);

    private static double ClampLat(double lat) => Math.Clamp(lat, -85.05, 85.05);

    private static double NormalizeLon(double lon) => ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

    /// <summary>Top-left world-pixel of the current viewport.</summary>
    private (double X, double Y) Origin =>
        (LonToX(_centerLon, _zoom) - Bounds.Width / 2.0,
         LatToY(_centerLat, _zoom) - Bounds.Height / 2.0);

    private (double Lat, double Lon) ScreenToGeo(Point p)
    {
        var (ox, oy) = Origin;
        return (ClampLat(YToLat(oy + p.Y, _zoom)), NormalizeLon(XToLon(ox + p.X, _zoom)));
    }

    // -- Rendering ----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        context.FillRectangle(BackgroundBrush, new Rect(0, 0, w, h));

        var (originX, originY) = Origin;
        DrawTiles(context, originX, originY, w, h);

        // Under the track and the markers: coverage is ground the map is about,
        // not something drawn on top of what the map is about.
        DrawCoverage(context, originX, originY);
        DrawTrack(context, originX, originY);

        _hitTargets.Clear();
        if (_vm is not null)
        {
            DrawPendingBoundingBox(context, originX, originY);
            DrawPendingLinkProfile(context, originX, originY);
            DrawMarkers(context, originX, originY, w, h);
            DrawSpider(context);
        }

        // Last, and outside the view-model guard: the legend explains the ring,
        // so it belongs over the markers and wherever the ring can be shown.
        DrawCoverageLegend(context, h);
    }

    // -- Coverage ring ------------------------------------------------------

    private static readonly IBrush CoverageClearFill = new SolidColorBrush(Color.Parse("#66BB6A"), 0.22);
    private static readonly IBrush CoverageWeakFill = new SolidColorBrush(Color.Parse("#FFB74D"), 0.22);
    private static readonly IBrush CoverageBlockedFill = new SolidColorBrush(Color.Parse("#EF5350"), 0.22);
    private static readonly Pen CoverageEdgePen = new(new SolidColorBrush(Color.Parse("#CCFFFFFF")), 1.0);
    private static readonly IBrush LegendBackground = new SolidColorBrush(Color.Parse("#CC202020"));

    private static readonly Pen MeasuredReachPen =
        new(new SolidColorBrush(Color.Parse("#4FC3F7")), 1.6) { DashStyle = new DashStyle([6, 4], 0) };

    private CoverageRing? _coverage;
    private string _coverageNote = string.Empty;
    private double _measuredReachM;
    private string? _coverageBusy;

    /// <summary>Shows what a sweep is waiting on, in the corner the legend
    /// will occupy once there is one. On the map rather than only in the status
    /// bar: a sweep on a cold cache pulls a hundred-odd terrain tiles and can
    /// sit for a minute, and the place to say so is where the user is
    /// looking.</summary>
    public void ShowCoverageBusy(string? message)
    {
        if (_coverageBusy == message) return;
        _coverageBusy = message;
        InvalidateVisual();
    }
    private CoverageHeatmap? _heatmap;
    private bool _heatmapVisible = true;

    /// <summary>Whether a painted field is being drawn. Painted or not is a
    /// question about the sweep; drawn or not is a question about the view, and
    /// keeping them apart means toggling costs nothing.</summary>
    private bool ShowingHeatmap => _heatmap is not null && _heatmapVisible;

    /// <summary>Shows or hides the shaded field over a sweep already on
    /// screen. The bitmap is kept either way, so this is instant.</summary>
    public void SetHeatmapVisible(bool visible)
    {
        if (_heatmapVisible == visible) return;
        _heatmapVisible = visible;
        InvalidateVisual();
    }
    private UnitSystem _coverageUnits = UnitSystem.Metric;

    /// <summary>Shows a coverage sweep over the basemap, or clears it with
    /// null. The ring is held in geographic coordinates and projected on every
    /// render, so it stays put under a pan or a zoom rather than needing to be
    /// swept again.</summary>
    /// <param name="measuredReachM">How far this station has actually heard a
    /// node directly, drawn as a circle beside the predicted ring. Zero to leave
    /// it off. It is the only number on the map that was measured rather than
    /// modelled, so a prediction wildly larger than it is a prediction to
    /// distrust.</param>
    public void ShowCoverage(
        CoverageRing? ring, string note = "", double measuredReachM = 0,
        UnitSystem units = UnitSystem.Metric)
    {

        _coverage = ring;
        _coverageUnits = units;
        _coverageNote = note;
        _measuredReachM = measuredReachM;

        // Painted once here rather than per frame: it is a bitmap in
        // Web-Mercator space, so every later pan and zoom is a transform.
        _heatmap = ring?.Field is { } field ? CoverageHeatmap.Paint(field) : null;

        InvalidateVisual();
    }

    public bool HasCoverage => _coverage is not null;

    /// <summary>
    /// The ring as a fan of wedges from the station, one per bearing, coloured
    /// by how that direction fared.
    ///
    /// Three geometries rather than one per wedge: a sweep is a couple of
    /// hundred bearings and the map redraws on every pan, so batching by colour
    /// turns a few hundred draw calls a frame into three.
    /// </summary>
    private void DrawCoverage(DrawingContext context, double originX, double originY)
    {
        if (_coverage is not { Spokes.Count: > 2 } ring) return;

        // The field first, under the wedges: it carries the gradient and the
        // islands the ring cannot, and the wedges say where contiguous
        // coverage ends.
        DrawHeatmap(context, originX, originY);

        var centre = new Point(
            LonToX(ring.Centre.Lon, _zoom) - originX,
            LatToY(ring.Centre.Lat, _zoom) - originY);

        var edges = new Point[ring.Spokes.Count];
        for (int i = 0; i < ring.Spokes.Count; i++)
        {
            var spoke = ring.Spokes[i];
            var at = CoverageMap.Along(ring.Centre, spoke.BearingDegrees, spoke.ReachM);
            edges[i] = new Point(LonToX(at.Lon, _zoom) - originX, LatToY(at.Lat, _zoom) - originY);
        }

        // With a field painted underneath, the wedges are dropped: two
        // translucent washes over one another read as neither. The heatmap says
        // how good the link is everywhere, and the outline below still says
        // where contiguous coverage ends, which is what the wedges were for.
        if (!ShowingHeatmap) DrawQualityWedges(context, ring, centre, edges);

        DrawReachOutline(context, edges);
        DrawMeasuredReach(context, ring.Centre, originX, originY);
    }

    /// <summary>The ring as a fan of wedges coloured by how each direction
    /// fared, for when there is no field to shade instead.</summary>
    private static void DrawQualityWedges(
        DrawingContext context, CoverageRing ring, Point centre, Point[] edges)
    {
        var wedges = new Dictionary<CoverageQuality, StreamGeometryContext>();
        var geometries = new Dictionary<CoverageQuality, StreamGeometry>();
        foreach (var quality in new[] { CoverageQuality.Clear, CoverageQuality.Weakened, CoverageQuality.Blocked })
        {
            var geometry = new StreamGeometry();
            geometries[quality] = geometry;
            wedges[quality] = geometry.Open();
        }

        for (int i = 0; i < ring.Spokes.Count; i++)
        {
            int next = (i + 1) % ring.Spokes.Count;

            // The worse of the two ends owns the wedge between them, so a
            // boundary reads as the start of the trouble rather than the end.
            var quality = (CoverageQuality)Math.Max(
                (int)ring.Spokes[i].Quality, (int)ring.Spokes[next].Quality);

            var ctx = wedges[quality];
            ctx.BeginFigure(centre, isFilled: true);
            ctx.LineTo(edges[i]);
            ctx.LineTo(edges[next]);
            ctx.EndFigure(true);
        }

        foreach (var ctx in wedges.Values) ctx.Dispose();

        context.DrawGeometry(CoverageClearFill, null, geometries[CoverageQuality.Clear]);
        context.DrawGeometry(CoverageWeakFill, null, geometries[CoverageQuality.Weakened]);
        context.DrawGeometry(CoverageBlockedFill, null, geometries[CoverageQuality.Blocked]);
    }

    /// <summary>Where contiguous coverage ends, drawn on its own so the reach
    /// reads as an edge rather than as wherever a translucent fill stops.
    /// </summary>
    private static void DrawReachOutline(DrawingContext context, Point[] edges)
    {
        var outline = new StreamGeometry();
        using (var ctx = outline.Open())
        {
            ctx.BeginFigure(edges[0], isFilled: false);
            for (int i = 1; i < edges.Length; i++) ctx.LineTo(edges[i]);
            ctx.EndFigure(true);
        }
        context.DrawGeometry(null, CoverageEdgePen, outline);
    }

    /// <summary>Places the painted field on the map. The bitmap covers a fixed
    /// square of the world, so putting it down is a matter of projecting two
    /// corners at the current zoom.</summary>
    private void DrawHeatmap(DrawingContext context, double originX, double originY)
    {
        if (!ShowingHeatmap || _heatmap is not { } heatmap) return;

        double left = LonToX(heatmap.West, _zoom) - originX;
        double right = LonToX(heatmap.East, _zoom) - originX;
        double top = LatToY(heatmap.North, _zoom) - originY;
        double bottom = LatToY(heatmap.South, _zoom) - originY;
        if (right - left < 1 || bottom - top < 1) return;

        var size = heatmap.Bitmap.PixelSize;
        context.DrawImage(
            heatmap.Bitmap,
            new Rect(0, 0, size.Width, size.Height),
            new Rect(left, top, right - left, bottom - top));
    }

    /// <summary>The furthest a node has actually been heard from here, as a
    /// circle. Drawn as a geographic circle rather than a screen one, so it
    /// stays honest at any latitude and zoom.</summary>
    private void DrawMeasuredReach(
        DrawingContext context, GeoPoint centre, double originX, double originY)
    {
        if (_measuredReachM <= 0) return;

        const int steps = 90;
        var circle = new StreamGeometry();
        using (var ctx = circle.Open())
        {
            for (int i = 0; i <= steps; i++)
            {
                var at = CoverageMap.Along(centre, 360.0 * i / steps, _measuredReachM);
                var point = new Point(
                    LonToX(at.Lon, _zoom) - originX, LatToY(at.Lat, _zoom) - originY);
                if (i == 0) ctx.BeginFigure(point, isFilled: false);
                else ctx.LineTo(point);
            }
            ctx.EndFigure(true);
        }
        context.DrawGeometry(null, MeasuredReachPen, circle);
    }

    /// <summary>What the three colours mean, plus what the sweep found. Drawn
    /// only while a ring is shown, in the one corner the map's own chrome
    /// leaves free.</summary>
    private void DrawCoverageLegend(DrawingContext context, double height)
    {
        if (_coverageBusy is { Length: > 0 } busy)
        {
            var text = new FormattedText(busy, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                         LabelTypeface, 11, Brushes.White);
            var box = new Rect(8, height - text.Height - 20, text.Width + 16, text.Height + 12);

            context.FillRectangle(LegendBackground, box, 3);
            context.DrawText(text, new Point(box.X + 8, box.Y + 6));
            return;
        }

        if (_coverage is not { } ring) return;

        // The swatches mean different things depending on what was drawn. With
        // a field they are bands of link odds, shading every point; without one
        // they are per-bearing verdicts, and the counts are what there is to
        // say.
        (IBrush Fill, string Label)[] entries = ShowingHeatmap
            ?
            [
                (CoverageClearFill, "Reliable"),
                (CoverageWeakFill, "Marginal"),
                (CoverageBlockedFill, "Fringe"),
            ]
            :
            [
                (CoverageClearFill, $"Clear  {ring.CountOf(CoverageQuality.Clear)}"),
                (CoverageWeakFill, $"Weakened  {ring.CountOf(CoverageQuality.Weakened)}"),
                (CoverageBlockedFill, $"Blocked  {ring.CountOf(CoverageQuality.Blocked)}"),
            ];

        const double pad = 6, swatch = 10, lineHeight = 15;

        // Laid out from the measured text rather than a guessed width: the note
        // carries a distance, a source and a resolution, and how wide that runs
        // depends on the units and on how far the station reaches.
        var rows = entries
            .Select(e => (e.Fill, Text: Row(e.Label)))
            .ToList();

        FormattedText? measured = _measuredReachM > 0
            ? Row($"Heard direct  {DisplayUnits.FormatShortDistance(_measuredReachM, _coverageUnits)}")
            : null;

        FormattedText? note = _coverageNote.Length > 0
            ? new FormattedText(_coverageNote, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                LabelTypeface, 10, new SolidColorBrush(Color.Parse("#BBBBBB")))
            : null;

        double labelled = rows.Max(r => r.Text.Width);
        if (measured is not null) labelled = Math.Max(labelled, measured.Width);

        double boxWidth = Math.Max(swatch + 6 + labelled, note?.Width ?? 0) + pad * 2;
        int lines = rows.Count + (measured is null ? 0 : 1) + (note is null ? 0 : 1);
        double boxHeight = pad * 2 + lineHeight * lines;
        double top = height - boxHeight - 8;

        context.FillRectangle(LegendBackground, new Rect(8, top, boxWidth, boxHeight), 3);

        double y = top + pad;
        foreach (var (fill, text) in rows)
        {
            context.FillRectangle(fill, new Rect(8 + pad, y + 2, swatch, swatch));
            context.DrawRectangle(null, CoverageEdgePen, new Rect(8 + pad, y + 2, swatch, swatch));
            context.DrawText(text, new Point(8 + pad + swatch + 6, y));
            y += lineHeight;
        }

        // The measured circle last in the list and drawn as a line rather than
        // a swatch, because it is a different kind of thing to the three above
        // it: those are predicted, this one happened.
        if (measured is not null)
        {
            double midline = y + 2 + swatch / 2;
            context.DrawLine(MeasuredReachPen,
                new Point(8 + pad, midline), new Point(8 + pad + swatch, midline));
            context.DrawText(measured, new Point(8 + pad + swatch + 6, y));
            y += lineHeight;
        }

        if (note is not null) context.DrawText(note, new Point(8 + pad, y));

        static FormattedText Row(string label) =>
            new(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, 11, Brushes.White);
    }

    // -- Track overlay (location history) -----------------------------------

    private IReadOnlyList<(double Lat, double Lon)> _track = Array.Empty<(double, double)>();

    private static readonly IPen TrackPen =
        new Pen(new SolidColorBrush(Color.FromRgb(0x2d, 0x8c, 0xff)), 2);
    private static readonly IBrush TrackPointFill = new SolidColorBrush(Color.FromRgb(0xff, 0xc1, 0x07));

    /// <summary>Draws a recorded path over the basemap and fits the view to it.
    /// Used by the location-history window, which shows one peer's track rather
    /// than the live node markers, so it needs no view model.</summary>
    public void ShowTrack(IReadOnlyList<(double Lat, double Lon)> points)
    {
        _track = points;
        if (points.Count > 0) FitToCoordinates(points);
        InvalidateVisual();
    }

    private void DrawTrack(DrawingContext context, double originX, double originY)
    {
        if (_track.Count == 0) return;

        if (_track.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (int i = 0; i < _track.Count; i++)
                {
                    var p = new Point(LonToX(_track[i].Lon, _zoom) - originX,
                                      LatToY(_track[i].Lat, _zoom) - originY);
                    if (i == 0) ctx.BeginFigure(p, false);
                    else ctx.LineTo(p);
                }
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, TrackPen, geometry);
        }

        // Endpoints of each recorded fix, so individual samples stay visible
        // when the track doubles back on itself.
        foreach (var (lat, lon) in _track)
            context.DrawEllipse(TrackPointFill, null,
                new Point(LonToX(lon, _zoom) - originX, LatToY(lat, _zoom) - originY), 2.5, 2.5);
    }

    /// <summary>Centre and zoom so every supplied coordinate fits.</summary>
    private void FitToCoordinates(IReadOnlyList<(double Lat, double Lon)> points)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var (lat, lon) in points)
        {
            minLat = Math.Min(minLat, lat); maxLat = Math.Max(maxLat, lat);
            minLon = Math.Min(minLon, lon); maxLon = Math.Max(maxLon, lon);
        }

        _centerLat = ClampLat((minLat + maxLat) / 2.0);
        _centerLon = (minLon + maxLon) / 2.0;
        _userMovedView = true;

        if (points.Count == 1) { _zoom = 15; return; }

        double w = Bounds.Width > 0 ? Bounds.Width : 600;
        double h = Bounds.Height > 0 ? Bounds.Height : 400;
        int best = MinZoom;
        for (int z = MaxZoom; z >= MinZoom; z--)
        {
            double spanX = Math.Abs(LonToX(maxLon, z) - LonToX(minLon, z));
            double spanY = Math.Abs(LatToY(minLat, z) - LatToY(maxLat, z));
            if (spanX <= w * 0.85 && spanY <= h * 0.85) { best = z; break; }
        }
        _zoom = best;
    }

    private void DrawTiles(DrawingContext context, double originX, double originY, double w, double h)
    {
        if (CurrentTiles.IsBlank) return;

        var provider = CurrentTiles;
        int n = 1 << _zoom;
        int firstTileX = (int)Math.Floor(originX / TileSize);
        int firstTileY = (int)Math.Floor(originY / TileSize);
        int lastTileX = (int)Math.Floor((originX + w) / TileSize);
        int lastTileY = (int)Math.Floor((originY + h) / TileSize);

        for (int ty = firstTileY; ty <= lastTileY; ty++)
        {
            if (ty < 0 || ty >= n) continue; // No wrapping vertically.

            // Snap each edge to a whole device pixel and derive the size from
            // the *next* tile's snapped edge. Drawing at fractional offsets
            // leaves a hairline seam between tiles, because each one's edge
            // gets antialiased against the background independently instead of
            // meeting its neighbour exactly.
            double top = Math.Round(ty * (double)TileSize - originY);
            double bottom = Math.Round((ty + 1) * (double)TileSize - originY);

            for (int tx = firstTileX; tx <= lastTileX; tx++)
            {
                // Wrap horizontally so panning past the antimeridian works.
                int wrappedX = ((tx % n) + n) % n;
                double left = Math.Round(tx * (double)TileSize - originX);
                double right = Math.Round((tx + 1) * (double)TileSize - originX);

                var key = $"{provider.Id}/{_zoom}/{wrappedX}/{ty}";
                if (s_memCache.TryGetValue(key, out var bmp))
                    context.DrawImage(bmp, new Rect(left, top, right - left, bottom - top));
                else
                    RequestTile(key, provider, wrappedX, ty, _zoom);
            }
        }
    }

    private void RequestTile(string key, TileProvider provider, int x, int y, int zoom)
    {
        if (!s_tileBackoff.ShouldTry(key, DateTimeOffset.UtcNow)) return;
        if (!_tilesInFlight.Add(key)) return;
        _ = LoadTileAsync(key, provider, x, y, zoom);
    }

    private async Task LoadTileAsync(string key, TileProvider provider, int x, int y, int zoom)
    {
        try
        {
            var bmp = await Task.Run(() => GetTileBitmapAsync(provider, x, y, zoom)).ConfigureAwait(true);
            if (bmp is null) return;
            s_tileBackoff.Succeeded(key);
            if (s_memCache.TryAdd(key, bmp))
            {
                s_memCacheOrder.Enqueue(key);
                // Evicted bitmaps are dropped, not disposed: a Render in
                // progress may still hold one, and drawing a disposed bitmap
                // throws. Their finalizers release the unmanaged memory.
                while (s_memCache.Count > MaxMemCacheTiles && s_memCacheOrder.TryDequeue(out var oldest))
                    s_memCache.TryRemove(oldest, out _);
            }
            InvalidateVisual();
        }
        // The tile is left out of both caches, so a retry once the backoff has
        // elapsed refetches rather than serving nothing.
        catch
        {
            s_tileBackoff.Failed(key, DateTimeOffset.UtcNow);
            ScheduleRetry(key);
        }
        finally { _tilesInFlight.Remove(key); }
    }

    /// <summary>Wakes the canvas once a failed tile is allowed to be tried
    /// again.
    ///
    /// Tiles are only ever requested from a render, and a render only happens
    /// when something asks for one. Nothing else necessarily will: a map left
    /// alone redraws no more, so without this a tile that failed once stays
    /// blank until the view is panned or zoomed, however long its backoff
    /// actually was.</summary>
    private void ScheduleRetry(string key)
    {
        // One wake serves every tile waiting on it, so a screenful of failures
        // schedules a single pass rather than one apiece.
        if (Interlocked.Exchange(ref _retryScheduled, 1) != 0) return;

        var delay = s_tileBackoff.RetryIn(key, DateTimeOffset.UtcNow);
        if (delay < MinRetryWake) delay = MinRetryWake;

        _ = Task.Delay(delay).ContinueWith(
            _ =>
            {
                Volatile.Write(ref _retryScheduled, 0);
                Dispatcher.UIThread.Post(InvalidateVisual);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task<Bitmap?> GetTileBitmapAsync(TileProvider provider, int x, int y, int zoom)
    {
        var file = System.IO.Path.Combine(s_cacheDir, $"{provider.Id}_{zoom}_{x}_{y}.png");

        // A vector tile is drawn rather than downloaded, but only once: the
        // bitmap it produces is cached on disk like any other tile, so the
        // style, the geometry and the rasteriser are all off the path for
        // every later visit to the same tile. What is cached is the tile as
        // drawn, before any recolouring, so a freshly drawn tile and one read
        // back from the cache come out of here looking the same.
        Bitmap drawn;
        if (provider.IsVector && !System.IO.File.Exists(file))
        {
            var rasterised = await RasterizeVectorTileAsync(provider, file, x, y, zoom)
                .ConfigureAwait(false);
            if (rasterised is null) return null;
            drawn = rasterised;
        }
        else
        {
            drawn = await LoadTileBytesAsync(provider, file, x, y, zoom).ConfigureAwait(false);
        }

        if (!provider.NeedsPostProcess) return drawn;
        using (drawn) return PostProcessTile(drawn, provider);
    }

    private static async Task<Bitmap> LoadTileBytesAsync(
        TileProvider provider, string file, int x, int y, int zoom)
    {
        byte[] bytes;
        if (System.IO.File.Exists(file))
        {
            bytes = await System.IO.File.ReadAllBytesAsync(file).ConfigureAwait(false);
            MarkCacheHit(file);
        }
        else
        {
            // Rotate across the provider's tile subdomains, where it has a
            // pool; providers serving one host leave Subdomains empty.
            var subs = provider.Subdomains;
            var url = provider.UrlTemplate;
            if (subs.Length > 0)
                url = url.Replace("{s}", subs[(x + y) % subs.Length].ToString());
            url = url
                .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
            bytes = await s_http.GetByteArrayAsync(url).ConfigureAwait(false);
            try
            {
                await System.IO.File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
                CountCacheWrite();
            }
            catch { /* cache best-effort */ }
        }

        using var ms = new System.IO.MemoryStream(bytes);
        return new Bitmap(ms);
    }

    // -- Vector tiles -------------------------------------------------------

    /// <summary>A style and the tile source it resolved to. Held per style URL
    /// as the Task itself, so concurrent tile loads racing on a cold cache all
    /// await one fetch rather than each starting their own.</summary>
    private sealed record VectorStyle(MapStyle Style, string TileTemplate, int SourceMaxZoom);

    private static readonly ConcurrentDictionary<string, Task<VectorStyle>> s_vectorStyles = new();

    // Decoded source tiles. Small on purpose: one is several megabytes of
    // features, and magnified zooms draw hundreds of output tiles from a single
    // parent, so a handful covers the panning that actually happens.
    private const int MaxVectorSourceTiles = 8;
    private static readonly ConcurrentDictionary<string, Task<VectorTile>> s_vectorSources = new();
    private static readonly ConcurrentQueue<string> s_vectorSourceOrder = new();

    private static Task<VectorStyle> VectorStyleAsync(string styleUrl) =>
        s_vectorStyles.GetOrAdd(styleUrl, static url => LoadVectorStyleAsync(url));

    private static async Task<VectorStyle> LoadVectorStyleAsync(string styleUrl)
    {
        var style = MapStyle.Parse(await s_http.GetStringAsync(styleUrl).ConfigureAwait(false));
        var source = style.VectorSource()
            ?? throw new InvalidOperationException($"{styleUrl} declares no vector source");

        // The tile template is resolved from TileJSON rather than hardcoded:
        // the OpenFreeMap tile path carries a dated build in it, and the
        // publisher asks that it not be pinned.
        string template;
        int maxZoom = source.MaxZoom;
        if (source.Url is { } tileJsonUrl)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                await s_http.GetStringAsync(tileJsonUrl).ConfigureAwait(false));
            template = doc.RootElement.GetProperty("tiles")[0].GetString()
                ?? throw new InvalidOperationException($"{tileJsonUrl} lists no tiles");
            if (doc.RootElement.TryGetProperty("maxzoom", out var mz)) maxZoom = mz.GetInt32();
        }
        else template = source.Tiles.FirstOrDefault()
            ?? throw new InvalidOperationException($"{styleUrl} names neither a TileJSON nor tiles");

        return new VectorStyle(style, template, maxZoom);
    }

    private static async Task<Bitmap?> RasterizeVectorTileAsync(
        TileProvider provider, string file, int x, int y, int zoom)
    {
        var vector = await VectorStyleAsync(provider.StyleUrl!).ConfigureAwait(false);
        var (sourceZoom, sourceX, sourceY) =
            TileProjection.SourceTile(zoom, x, y, vector.SourceMaxZoom);

        var tile = await SourceTileAsync(provider, vector, sourceZoom, sourceX, sourceY)
            .ConfigureAwait(false);

        var bitmap = VectorTileRasterizer.Render(
            tile, vector.Style, zoom, x, y, vector.SourceMaxZoom, TileSize);

        try
        {
            bitmap.Save(file, new PngBitmapEncoderOptions());
            CountCacheWrite();
        }
        catch { /* cache best-effort */ }
        return bitmap;
    }

    /// <summary>The decoded source tile, fetching it if needed.
    ///
    /// The Task is what is cached, not the tile: a burst of neighbouring tiles
    /// magnified from one parent would otherwise all miss together and each
    /// start its own download of the same half megabyte.</summary>
    private static Task<VectorTile> SourceTileAsync(
        TileProvider provider, VectorStyle vector, int zoom, int x, int y)
    {
        var key = $"{provider.Id}_{zoom}_{x}_{y}";

        bool created = false;
        var task = s_vectorSources.GetOrAdd(key, k =>
        {
            created = true;
            return LoadSourceTileAsync(vector, k, zoom, x, y);
        });

        if (created)
        {
            s_vectorSourceOrder.Enqueue(key);
            while (s_vectorSources.Count > MaxVectorSourceTiles
                   && s_vectorSourceOrder.TryDequeue(out var oldest))
                s_vectorSources.TryRemove(oldest, out _);

            // A failed fetch must not stay cached as a failure, or the backoff
            // would retry into the same faulted task for the life of the app.
            _ = task.ContinueWith(
                t => s_vectorSources.TryRemove(key, out _),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return task;
    }

    private static async Task<VectorTile> LoadSourceTileAsync(
        VectorStyle vector, string key, int zoom, int x, int y)
    {
        // The encoded tile is kept on disk too, so a first pass over an area
        // after a restart does not refetch half a megabyte per parent.
        var path = System.IO.Path.Combine(s_cacheDir, key + ".pbf");
        byte[] bytes;
        if (System.IO.File.Exists(path))
        {
            bytes = await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(false);
            MarkCacheHit(path);
        }
        else
        {
            var url = vector.TileTemplate
                .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
            bytes = await s_http.GetByteArrayAsync(url).ConfigureAwait(false);
            try
            {
                await System.IO.File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                CountCacheWrite();
            }
            catch { /* cache best-effort */ }
        }

        return VectorTile.Parse(bytes);
    }

    /// <summary>Recolours a tile so a provider's palette suits the app: invert
    /// flips a light tileset dark, the hue rotation and saturation settle the
    /// colours that inversion throws off, and gamma/brightness set the final
    /// level. Skipped entirely unless the provider asks for it.</summary>
    private static unsafe Bitmap PostProcessTile(Bitmap src, TileProvider provider)
    {
        var size = src.PixelSize;
        var target = new WriteableBitmap(size, src.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);

        // Channel value after inversion, before the colour matrix.
        var pre = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            pre[i] = provider.Invert ? 1.0 - v : v;
        }

        // Brightness and gamma fold into one lookup over the matrix output.
        var post = new byte[256];
        double gammaInv = (provider.Gamma > 0.0 && provider.Gamma != 1.0) ? 1.0 / provider.Gamma : 1.0;
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            if (gammaInv != 1.0) v = Math.Pow(v, gammaInv);
            post[i] = (byte)Math.Clamp(v * provider.Brightness * 255.0, 0.0, 255.0);
        }

        var m = ColorMatrix(provider.HueRotate, provider.Saturation);

        using (var fb = target.Lock())
        {
            src.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                           fb.Address, fb.RowBytes * size.Height, fb.RowBytes);
            byte* p = (byte*)fb.Address;
            for (int row = 0; row < size.Height; row++)
            {
                byte* line = p + (long)row * fb.RowBytes;
                for (int col = 0; col < size.Width; col++)
                {
                    byte* px = line + col * 4;
                    double b = pre[px[0]], g = pre[px[1]], r = pre[px[2]];

                    double nr = m[0] * r + m[1] * g + m[2] * b;
                    double ng = m[3] * r + m[4] * g + m[5] * b;
                    double nb = m[6] * r + m[7] * g + m[8] * b;

                    px[0] = post[(int)Math.Clamp(nb * 255.0, 0.0, 255.0)];
                    px[1] = post[(int)Math.Clamp(ng * 255.0, 0.0, 255.0)];
                    px[2] = post[(int)Math.Clamp(nr * 255.0, 0.0, 255.0)];
                    // alpha (px[3]) untouched
                }
            }
        }
        return target;
    }

    /// <summary>Row-major 3x3 combining a hue rotation with a saturation
    /// scale, both as defined by the CSS filter effects colour matrices, so the
    /// numbers match what the same filter chain produces in a browser.</summary>
    private static double[] ColorMatrix(double hueDegrees, double saturation)
    {
        double c = Math.Cos(hueDegrees * Math.PI / 180.0);
        double s = Math.Sin(hueDegrees * Math.PI / 180.0);
        double[] hue =
        [
            0.213 + c * 0.787 - s * 0.213, 0.715 - c * 0.715 - s * 0.715, 0.072 - c * 0.072 + s * 0.928,
            0.213 - c * 0.213 + s * 0.143, 0.715 + c * 0.285 + s * 0.140, 0.072 - c * 0.072 - s * 0.283,
            0.213 - c * 0.213 - s * 0.787, 0.715 - c * 0.715 + s * 0.715, 0.072 + c * 0.928 + s * 0.072,
        ];
        double k = saturation;
        double[] sat =
        [
            0.213 + 0.787 * k, 0.715 - 0.715 * k, 0.072 - 0.072 * k,
            0.213 - 0.213 * k, 0.715 + 0.285 * k, 0.072 - 0.072 * k,
            0.213 - 0.213 * k, 0.715 - 0.715 * k, 0.072 + 0.928 * k,
        ];

        // sat * hue: the hue rotation applies first.
        var m = new double[9];
        for (int r = 0; r < 3; r++)
            for (int col = 0; col < 3; col++)
                m[r * 3 + col] = sat[r * 3] * hue[col]
                               + sat[r * 3 + 1] * hue[3 + col]
                               + sat[r * 3 + 2] * hue[6 + col];
        return m;
    }

    // -- Markers ------------------------------------------------------------

    private void DrawMarkers(DrawingContext context, double originX, double originY, double w, double h)
    {
        if (_vm is null) return;
        var markers = Markers;

        var nodes = new List<(RadioViewModel.MapMarker mk, double px, double py)>();

        foreach (var mk in markers)
        {
            double px = LonToX(mk.Lon, _zoom) - originX;
            double py = LatToY(mk.Lat, _zoom) - originY;

            if (!mk.IsHome && !mk.IsWaypoint)
            {
                // Nodes are culled after they have been grouped, not here.
                // Which nodes are on screen changes with every pan, and dropping
                // one first would change how the rest group, so a group near the
                // edge would rearrange as the pan carried its neighbours across
                // the boundary.
                nodes.Add((mk, px, py));
                continue;
            }

            if (OffScreen(px, py, w, h)) continue;

            if (mk.IsHome)
            {
                DrawHome(context, mk, px, py);
            }
            else
            {
                if (mk.GeofenceRadiusM > 0) DrawGeofenceCircle(context, mk, px, py);
                if (mk.BboxWest is double bw && mk.BboxSouth is double bs &&
                    mk.BboxEast is double be && mk.BboxNorth is double bn)
                    DrawGeofenceRectangle(context, bw, bs, be, bn, originX, originY);
                DrawDot(context, mk.IsExpired ? WaypointExpiredFill : WaypointFill, px, py);
                DrawLabel(context, mk.Label, px, py);
                _hitTargets.Add(new HitTarget(px, py, MarkerRadiusPx + 2, mk.Title, null, mk));
            }
        }

        // Fixed order, whatever order the markers arrived in. The marker list
        // follows the node grid, which reorders as nodes are heard, and both
        // the node a group forms around and which overlapping label lands on
        // top would otherwise change under a packet arriving mid-drag.
        nodes.Sort(static (a, b) => (a.mk.NodeNum ?? 0).CompareTo(b.mk.NodeNum ?? 0));

        if (!_clusterNodes)
        {
            foreach (var n in nodes)
            {
                if (OffScreen(n.px, n.py, w, h)) continue;
                DrawDot(context, NodeFill, n.px, n.py);
                DrawLabel(context, n.mk.Label, n.px, n.py);
                _hitTargets.Add(new HitTarget(n.px, n.py, MarkerRadiusPx + 2, n.mk.Title, null, n.mk));
            }
            return;
        }

        // Group nodes landing on nearly the same pixel so stacked nodes don't
        // hide each other's dot and tooltip.
        foreach (var cluster in BuildMarkerClusters(nodes, ClusterRadiusPx))
        {
            if (cluster.Count == 1)
            {
                var (mk, px, py) = cluster[0];
                if (OffScreen(px, py, w, h)) continue;
                DrawDot(context, NodeFill, px, py);
                DrawLabel(context, mk.Label, px, py);
                _hitTargets.Add(new HitTarget(px, py, MarkerRadiusPx + 2, mk.Title, null, mk));
            }
            else
            {
                DrawCluster(context, cluster, w, h);
            }
        }
    }

    /// <summary>A margin outside the viewport keeps edge markers and their
    /// labels from popping in late.</summary>
    private const double CullMarginPx = 48;

    private static bool OffScreen(double px, double py, double w, double h) =>
        px < -CullMarginPx || px > w + CullMarginPx || py < -CullMarginPx || py > h + CullMarginPx;

    private static void DrawDot(DrawingContext context, IBrush fill, double px, double py) =>
        context.DrawEllipse(fill, MarkerOutline, new Point(px, py), MarkerRadiusPx, MarkerRadiusPx);

    private void DrawHome(DrawingContext context, RadioViewModel.MapMarker mk, double px, double py)
    {
        var glyph = new FormattedText("⌂", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                      LabelTypeface, 20, Brushes.Gold);
        context.DrawText(glyph, new Point(px - glyph.Width / 2, py - glyph.Height / 2));
        DrawLabel(context, mk.Label, px, py);
        _hitTargets.Add(new HitTarget(px, py, 10, mk.Title, null, mk));
    }

    /// <summary>Marker label, offset to the lower-right of the dot on a
    /// translucent plate so it stays readable over any basemap.</summary>
    private static void DrawLabel(DrawingContext context, string text, double px, double py)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                   LabelTypeface, 11, Brushes.White);
        double x = px + 9, y = py - ft.Height / 2.0;
        context.FillRectangle(LabelBackground, new Rect(x - 2, y, ft.Width + 4, ft.Height));
        context.DrawText(ft, new Point(x, y));
    }

    private void DrawCluster(DrawingContext context,
                             List<(RadioViewModel.MapMarker mk, double px, double py)> members,
                             double w, double h)
    {
        double cx = members.Average(m => m.px);
        double cy = members.Average(m => m.py);
        if (OffScreen(cx, cy, w, h)) return;
        context.DrawEllipse(ClusterFill, MarkerOutline, new Point(cx, cy),
                            ClusterBadgeRadiusPx, ClusterBadgeRadiusPx);

        var ft = new FormattedText(members.Count.ToString(CultureInfo.InvariantCulture),
                                   CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                   new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                                   12, Brushes.White);
        context.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));

        _hitTargets.Add(new HitTarget(cx, cy, ClusterBadgeRadiusPx,
                                      $"{members.Count} nodes here — click to expand", members));
    }

    /// <summary>Single-link clustering over a uniform grid: each node joins the
    /// nearest existing cluster whose anchor is within the radius, checking only
    /// the 9 neighbouring buckets rather than every cluster.
    ///
    /// Nearest, not first found, because the buckets are laid out from the
    /// viewport's origin and so shift under the nodes as the map is panned. A
    /// node in reach of two anchors would join whichever bucket the scan
    /// reached first, and a one-pixel pan is enough to reverse that order —
    /// groups would swap members and their badges jump mid-drag. Distance
    /// between nodes does not depend on where the grid falls, so the same nodes
    /// group the same way at every pan offset.</summary>
    private static List<List<(RadioViewModel.MapMarker mk, double px, double py)>> BuildMarkerClusters(
        IReadOnlyList<(RadioViewModel.MapMarker mk, double px, double py)> nodes, double clusterRadiusPx)
    {
        var clusters = new List<List<(RadioViewModel.MapMarker mk, double px, double py)>>();
        if (nodes.Count == 0) return clusters;

        double radiusSq = clusterRadiusPx * clusterRadiusPx;
        double cellSize = clusterRadiusPx;
        var anchors = new List<(double Px, double Py)>();
        var buckets = new Dictionary<long, List<int>>();

        foreach (var node in nodes)
        {
            int bucketX = (int)Math.Floor(node.px / cellSize);
            int bucketY = (int)Math.Floor(node.py / cellSize);
            int hit = -1;
            double bestSq = double.MaxValue;

            for (int bx = bucketX - 1; bx <= bucketX + 1; bx++)
            {
                for (int by = bucketY - 1; by <= bucketY + 1; by++)
                {
                    if (!buckets.TryGetValue(BucketKey(bx, by), out var candidates)) continue;
                    foreach (var ci in candidates)
                    {
                        double dx = node.px - anchors[ci].Px;
                        double dy = node.py - anchors[ci].Py;
                        double distSq = dx * dx + dy * dy;
                        if (distSq > radiusSq) continue;
                        // Ties go to the older cluster, so an exact draw is
                        // settled by the order the nodes came in rather than by
                        // the order the buckets happened to be scanned in.
                        if (distSq < bestSq || (distSq == bestSq && ci < hit)) { bestSq = distSq; hit = ci; }
                    }
                }
            }

            if (hit >= 0) { clusters[hit].Add(node); continue; }

            int newIndex = clusters.Count;
            clusters.Add([node]);
            anchors.Add((node.px, node.py));
            long key = BucketKey(bucketX, bucketY);
            if (!buckets.TryGetValue(key, out var members)) buckets[key] = members = new List<int>();
            members.Add(newIndex);
        }

        return clusters;
    }

    private static long BucketKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    // -- Geofences ----------------------------------------------------------

    /// <summary>Circular geofence, sized from its real-world radius via the
    /// current zoom's ground resolution.</summary>
    private void DrawGeofenceCircle(DrawingContext context, RadioViewModel.MapMarker mk, double px, double py)
    {
        double mpp = MetersPerPixel(mk.Lat, _zoom);
        if (mpp <= 0 || double.IsNaN(mpp) || double.IsInfinity(mpp)) return;
        double radiusPx = mk.GeofenceRadiusM / mpp;
        if (radiusPx < 1 || double.IsNaN(radiusPx) || double.IsInfinity(radiusPx)) return;
        context.DrawEllipse(GeofenceFill, GeofencePen, new Point(px, py), radiusPx, radiusPx);
    }

    /// <summary>Rectangular geofence from west/south/east/north degrees.</summary>
    private void DrawGeofenceRectangle(DrawingContext context,
                                       double west, double south, double east, double north,
                                       double originX, double originY)
    {
        double x1 = LonToX(west, _zoom) - originX;
        double x2 = LonToX(east, _zoom) - originX;
        double y1 = LatToY(north, _zoom) - originY; // north = smaller Y
        double y2 = LatToY(south, _zoom) - originY;
        var rect = new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
        context.DrawRectangle(GeofenceFill, GeofencePen, rect);
    }

    private static readonly Pen ChosenPointPen =
        new(new SolidColorBrush(Color.Parse("#FFD479")), 1.4);

    /// <summary>
    /// A point the user picked for a tool to work from, marked so the choice is
    /// visible rather than only announced.
    ///
    /// One shape for all of them. A dropped point is the same kind of thing
    /// whichever tool asked for it, and a ring centred somewhere unexpected is
    /// a great deal easier to understand with a crosshair on it.
    /// </summary>
    private void DrawChosenPoint(
        DrawingContext context, GeoPoint at, string label, double originX, double originY)
    {
        double px = LonToX(at.Lon, _zoom) - originX;
        double py = LatToY(at.Lat, _zoom) - originY;

        context.DrawEllipse(null, ChosenPointPen, new Point(px, py), 7, 7);
        context.DrawLine(ChosenPointPen, new Point(px - 12, py), new Point(px + 12, py));
        context.DrawLine(ChosenPointPen, new Point(px, py - 12), new Point(px, py + 12));

        DrawLabel(context, label, px + 14, py - 7);
    }

    private void DrawPendingLinkProfile(DrawingContext context, double originX, double originY)
    {
        if (ChosenPoint is { } chosen)
            DrawChosenPoint(context, chosen, "chosen point", originX, originY);
    }

    private void DrawPendingBoundingBox(DrawingContext context, double originX, double originY)
    {
        if (_vm is null) return;

        if (_vm.IsPickingWaypointBoundingBox && _pendingBboxCorner is (double cLat, double cLon))
        {
            double cx = LonToX(cLon, _zoom) - originX;
            double cy = LatToY(cLat, _zoom) - originY;
            context.DrawEllipse(null, GeofencePen, new Point(cx, cy), 5, 5);
        }

        if (_vm.WaypointBboxWest is double west && _vm.WaypointBboxSouth is double south &&
            _vm.WaypointBboxEast is double east && _vm.WaypointBboxNorth is double north)
            DrawGeofenceRectangle(context, west, south, east, north, originX, originY);
    }

    // -- Spiderfy -----------------------------------------------------------

    /// <summary>Fans a stacked group out around its shared point so each node
    /// gets its own dot, label and hit target.</summary>
    private void SpiderExpand(List<(RadioViewModel.MapMarker mk, double px, double py)> members,
                              double cx, double cy)
    {
        _spiderMembers = members;
        _spiderCx = cx;
        _spiderCy = cy;
        _spiderLegLen = Math.Max(34, 14 + members.Count * 4);
        InvalidateVisual();
    }

    private void SpiderCollapse()
    {
        if (_spiderMembers is null) return;
        _spiderMembers = null;
        InvalidateVisual();
    }

    private void DrawSpider(DrawingContext context)
    {
        if (_spiderMembers is not { Count: > 0 } members) return;

        for (int i = 0; i < members.Count; i++)
        {
            double angle = 2 * Math.PI * i / members.Count - Math.PI / 2;
            double mx = _spiderCx + _spiderLegLen * Math.Cos(angle);
            double my = _spiderCy + _spiderLegLen * Math.Sin(angle);

            context.DrawLine(SpiderLegPen, new Point(_spiderCx, _spiderCy), new Point(mx, my));
            DrawDot(context, NodeFill, mx, my);

            // Flip the label to the inside when the leg points left, so it
            // doesn't run off the fanned-out group.
            var ft = new FormattedText(members[i].mk.Label, CultureInfo.CurrentCulture,
                                       FlowDirection.LeftToRight, LabelTypeface, 11, Brushes.White);
            double lx = Math.Cos(angle) >= 0 ? mx + 10 : mx - 10 - ft.Width;
            double ly = my - ft.Height / 2;
            context.FillRectangle(LabelBackground, new Rect(lx - 2, ly, ft.Width + 4, ft.Height));
            context.DrawText(ft, new Point(lx, ly));

            _hitTargets.Add(new HitTarget(mx, my, MarkerRadiusPx + 2, members[i].mk.Title, null, members[i].mk));
        }
    }

    /// <summary>True while the pointer is inside the fanned-out group's hull,
    /// so moving between the badge, legs and dots doesn't collapse it.</summary>
    private bool IsWithinSpider(Point p)
    {
        if (_spiderMembers is null) return false;
        double hull = _spiderLegLen + 24;
        double dx = p.X - _spiderCx, dy = p.Y - _spiderCy;
        return dx * dx + dy * dy <= hull * hull;
    }

    // -- Pointer interaction ------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            if (_vm is null) return;

            // Ctrl+right-click drops the home location here. Checked before the
            // marker menu so the gesture still works over a crowded map.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var (lat, lon) = ScreenToGeo(p);
                _vm.SetHomeLocation(lat, lon);
                e.Handled = true;
                return;
            }

            // Markers aren't visuals, so there is nothing for the framework to
            // attach a ContextMenu to — resolve the marker ourselves and build
            // the menu for whatever is under the pointer.
            var (menuLat, menuLon) = ScreenToGeo(p);
            var menu = HitTest(p)?.Marker is { } hitMarker
                ? BuildMarkerMenu(hitMarker)
                : BuildGroundMenu(menuLat, menuLon);
            if (menu is null) return;

            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Pointer;
            menu.Open(this);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // Corner picking is its own explicit mode (the "Pick corners on map"
        // toggle), so it fires on a plain click — Ctrl is only for send-waypoint.
        if (_vm is { IsPickingWaypointBoundingBox: true })
        {
            var (lat, lon) = ScreenToGeo(p);
            if (_pendingBboxCorner is (double cLat, double cLon))
            {
                _pendingBboxCorner = null;
                _vm.SetWaypointBoundingBox(cLat, cLon, lat, lon);
            }
            else
            {
                _pendingBboxCorner = (lat, lon);
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_vm is not null && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var (lat, lon) = ScreenToGeo(p);
            RequestSendWaypoint?.Invoke(lat, lon);
            e.Handled = true;
            return;
        }

        var hit = HitTest(p);

        // A double-click on a marker does what double-clicking its row in the
        // grids does: opens a node's DM tab, or a waypoint's editor.
        if (e.ClickCount == 2 && ActivateMarker(hit?.Marker))
        {
            e.Handled = true;
            return;
        }

        // A click on a cluster badge fans it out instead of starting a drag.
        if (hit is { Cluster: not null } clusterHit)
        {
            SpiderExpand(clusterHit.Cluster!, clusterHit.X, clusterHit.Y);
            e.Handled = true;
            return;
        }

        if (_spiderMembers is not null && !IsWithinSpider(p)) SpiderCollapse();

        // Dragging breaks follow mode.
        if (_followHome)
        {
            _followHome = false;
            FollowHomeChanged?.Invoke(false);
        }

        _dragging = true;
        _dragMoved = false;
        _lastPointer = p;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        if (_dragging)
        {
            double dx = p.X - _lastPointer.X;
            double dy = p.Y - _lastPointer.Y;
            _lastPointer = p;
            if (dx == 0 && dy == 0) return;
            _dragMoved = true;

            // Pan by moving the center the opposite way in world pixels.
            double cx = LonToX(_centerLon, _zoom) - dx;
            double cy = LatToY(_centerLat, _zoom) - dy;
            _centerLon = NormalizeLon(XToLon(cx, _zoom));
            _centerLat = ClampLat(YToLat(cy, _zoom));
            _userMovedView = true;
            SpiderCollapse();
            InvalidateVisual();
            return;
        }

        // Hover: resolve a tooltip against the hit targets from the last render.
        if (_spiderMembers is not null && !IsWithinSpider(p)) SpiderCollapse();

        // Only touch the tooltip and cursor when they actually change: this
        // runs on every pointer move, and Cursor is disposable.
        var hit = HitTest(p);
        string? tip = hit?.Tooltip;
        if (ToolTip.GetTip(this) as string != tip)
        {
            ToolTip.SetTip(this, tip);
            Cursor = tip is null ? Cursor.Default : HandCursor;
        }
    }

    /// <summary>Drop the hover tooltip when the pointer leaves. Without this it
    /// stays armed while the pointer is over the controls layered on top of the
    /// map, and can re-open over them.</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (ToolTip.GetTip(this) is null) return;
        ToolTip.SetTip(this, null);
        Cursor = Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        if (_dragMoved) InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private HitTarget? HitTest(Point p)
    {
        // Later targets are drawn on top, so search back to front.
        for (int i = _hitTargets.Count - 1; i >= 0; i--)
        {
            var t = _hitTargets[i];
            double dx = p.X - t.X, dy = p.Y - t.Y;
            if (dx * dx + dy * dy <= t.Radius * t.Radius) return t;
        }
        return null;
    }

    /// <summary>Ctrl+left-click asked to drop a waypoint at these coordinates.
    /// The host handles it so the destination picker can own a parent window.</summary>
    public event Action<double, double>? RequestSendWaypoint;

    /// <summary>"Edit…" was chosen on a waypoint marker. Raised rather than
    /// handled here for the same reason as <see cref="RequestSendWaypoint"/>:
    /// the edit dialog needs a parent window, which the host owns.</summary>
    public event Action<WaypointRecord>? RequestEditWaypoint;

    /// <summary>"Delete" was chosen on a waypoint marker. Raised rather than
    /// run straight off the menu so the same confirmation the waypoints grid
    /// asks gets asked here, which needs a parent window the host owns.</summary>
    public event Action<WaypointRecord>? RequestDeleteWaypoint;

    /// <summary>"Delete" was chosen on a node marker, for the same reason.</summary>
    public event Action<NodeRecord>? RequestDeleteNode;

    /// <summary>"Link profile" was chosen on a node marker. Offered only when
    /// both ends have a position, since a cross-section needs two points.
    /// </summary>
    public event Action<NodeRecord>? RequestLinkProfile;

    /// <summary>A place on bare map was chosen as the viewpoint for a sweep.
    /// Every other tool here is anchored to this station; these two answer the
    /// other question, which is where a node ought to go.</summary>
    public event Action<double, double>? RequestCoverageFrom;

    /// <summary>The same, for the skyline.</summary>
    public event Action<double, double>? RequestHorizonFrom;

    /// <summary>The chosen point was cleared, so anything drawn from it wants
    /// redoing from this station.</summary>
    public event Action? ChosenPointCleared;

    /// <summary>
    /// A place on the map the RF tools work from instead of this station.
    ///
    /// One point, not one per tool. Siting a node means asking several
    /// questions about the same spot — what does it reach, what can it see,
    /// what is the path to each of these three nodes — so the point stays until
    /// it is cleared rather than being spent by whichever tool used it last.
    /// </summary>
    public GeoPoint? ChosenPoint { get; private set; }

    public void SetChosenPoint(double lat, double lon)
    {
        ChosenPoint = new GeoPoint(lat, lon);
        InvalidateVisual();
    }

    public void ClearChosenPoint()
    {
        if (ChosenPoint is null) return;
        ChosenPoint = null;
        InvalidateVisual();
        ChosenPointCleared?.Invoke();
    }

    // -- Marker context menu ------------------------------------------------

    /// <summary>
    /// Menu for the marker under the pointer, or null when there is nothing to
    /// act on — empty map, the home marker (it is a setting, not a peer), or a
    /// cluster badge, which stands for several nodes at once and would need one
    /// picked first. Mirrors the node and waypoint grids' menus, minus the
    /// entries that are inherently grid-bound (Copy, Show on map).
    /// </summary>
    /// <summary>
    /// The menu for bare ground: sweep from here rather than from home.
    ///
    /// The RF tools all answer a question about a viewpoint, and until now the
    /// viewpoint was always this station. Letting it be dropped anywhere turns
    /// them from "what do I reach" into "what would a node here reach", which
    /// is the question behind putting one up.
    /// </summary>
    private ContextMenu? BuildGroundMenu(double lat, double lon)
    {
        if (_vm is null) return null;

        var coverage = new MenuItem { Header = "Coverage from here" };
        coverage.Click += (_, _) =>
        {
            SetChosenPoint(lat, lon);
            RequestCoverageFrom?.Invoke(lat, lon);
        };

        var horizon = new MenuItem { Header = "Horizon from here…" };
        horizon.Click += (_, _) =>
        {
            SetChosenPoint(lat, lon);
            RequestHorizonFrom?.Invoke(lat, lon);
        };

        // A profile needs a far end, so this only chooses the near one. The
        // node picked next — from its marker or from the grid — finishes it,
        // and the point stays afterwards so the next node can be profiled from
        // the same spot.
        var profile = new MenuItem { Header = "Link profile from here…" };
        profile.Click += (_, _) =>
        {
            SetChosenPoint(lat, lon);
            _vm.StatusText = "Point chosen. Pick a node to draw the profile to.";
        };

        var items = new List<Control> { coverage, horizon, profile };

        if (ChosenPoint is not null)
        {
            items.Add(new Separator());

            var move = new MenuItem { Header = "Move chosen point here" };
            move.Click += (_, _) => SetChosenPoint(lat, lon);

            var clear = new MenuItem { Header = "Clear chosen point" };
            clear.Click += (_, _) =>
            {
                ClearChosenPoint();
                _vm.StatusText = "Chosen point cleared. The tools work from this station again.";
            };

            items.Add(move);
            items.Add(clear);
        }

        return Menu([.. items]);
    }

    private ContextMenu? BuildMarkerMenu(RadioViewModel.MapMarker? marker)
    {
        if (_vm is null || marker is not { } mk || mk.IsHome) return null;

        if (mk.IsWaypoint)
        {
            if (WaypointFor(mk) is not { } wp) return null;

            var edit = new MenuItem { Header = "Edit…" };
            edit.Click += (_, _) => RequestEditWaypoint?.Invoke(wp);
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += (_, _) => RequestDeleteWaypoint?.Invoke(wp);
            return Menu(
                edit,
                Item("Resend", _vm.ResendWaypointCommand, wp),
                new Separator(),
                delete);
        }

        if (NodeFor(mk) is not { } node) return null;

        var deleteNode = new MenuItem { Header = "Delete" };
        deleteNode.Click += (_, _) => RequestDeleteNode?.Invoke(node);

        // Needs a position at both ends. Shown either way rather than hidden,
        // and disabled with the reason: an entry that comes and goes with the
        // node under the pointer is harder to find than one that explains
        // itself.
        bool haveBothEnds = node.Latitude is not null && node.Longitude is not null
            && (ChosenPoint is not null || _vm.TryGetHomeLocation(out _, out _));
        var linkProfile = new MenuItem
        {
            Header = ChosenPoint is null
                ? "Link profile…"
                : "Link profile from chosen point…",
            IsEnabled = haveBothEnds,
        };
        if (!haveBothEnds)
            ToolTip.SetTip(linkProfile, node.Latitude is null
                ? "This node has not reported a position"
                : "Set your own location first");
        linkProfile.Click += (_, _) => RequestLinkProfile?.Invoke(node);

        return Menu(
            Item("Message", _vm.MessageNodeCommand, node),
            new Separator(),
            linkProfile,
            new Separator(),
            Item("Request node info", _vm.RequestNodeInfoCommand, node),
            Item("Exchange node info", _vm.ExchangeNodeInfoCommand, node),
            Item("Request location", _vm.RequestLocationCommand, node),
            Item("Exchange location", _vm.ExchangeLocationCommand, node),
            Item("Request telemetry", _vm.RequestTelemetryCommand, node),
            Item("Traceroute", _vm.TracerouteCommand, node),
            Item("Request new keys", _vm.RequestNewKeysCommand, node),
            new Separator(),
            Item("Toggle ignore", _vm.ToggleIgnoreNodeCommand, node),
            Item("Toggle favorite", _vm.ToggleFavoriteNodeCommand, node),
            new Separator(),
            deleteNode);
    }

    /// <summary>Acts on the marker under a double-click: a node opens its DM
    /// tab, a waypoint its editor, matching what double-clicking the same thing
    /// in the node and waypoint grids does. Home has nothing to open and a
    /// cluster badge stands for several nodes at once, so both fall through to
    /// the click handling that expands and pans. Says whether it opened
    /// anything.</summary>
    private bool ActivateMarker(RadioViewModel.MapMarker? marker)
    {
        if (marker is not { } mk) return false;

        if (WaypointFor(mk) is { } wp)
        {
            RequestEditWaypoint?.Invoke(wp);
            return true;
        }

        if (NodeFor(mk) is { } node)
        {
            _vm!.MessageNodeCommand.Execute(node);
            return true;
        }

        return false;
    }

    /// <summary>The waypoint a marker stands for, or null when it is not a
    /// waypoint marker or its row has since gone.</summary>
    private WaypointRecord? WaypointFor(RadioViewModel.MapMarker mk) =>
        _vm is not null && mk.IsWaypoint && mk.WaypointRowId is long rowId
            ? _vm.Waypoints.FirstOrDefault(w => w.Id == rowId)
            : null;

    /// <summary>The node a marker stands for, or null when it is not a node
    /// marker or the node has since been filtered out of the grid.</summary>
    private NodeRecord? NodeFor(RadioViewModel.MapMarker mk) =>
        _vm is not null && !mk.IsHome && !mk.IsWaypoint && mk.NodeNum is uint nodeNum
            ? _vm.FilteredNodes.FirstOrDefault(n => n.NodeNum == nodeNum)
            : null;

    private static ContextMenu Menu(params Control[] items)
    {
        var menu = new ContextMenu();
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    private static MenuItem Item(string header, System.Windows.Input.ICommand command, object parameter) =>
        new() { Header = header, Command = command, CommandParameter = parameter };

    /// <summary>Follow mode turned itself off (the user panned).</summary>
    public event Action<bool>? FollowHomeChanged;

    // -- Viewport commands --------------------------------------------------

    private void ZoomAt(Point anchor, int delta)
    {
        int newZoom = Math.Clamp(_zoom + delta, MinZoom, CurrentTiles.DeepestZoom);
        if (newZoom == _zoom) return;

        // Keep the geographic point under the cursor fixed across the zoom.
        var (originX, originY) = Origin;
        double anchorLon = XToLon(originX + anchor.X, _zoom);
        double anchorLat = YToLat(originY + anchor.Y, _zoom);

        _zoom = newZoom;

        double ax = LonToX(anchorLon, _zoom);
        double ay = LatToY(anchorLat, _zoom);
        double cx = ax + (Bounds.Width / 2.0 - anchor.X);
        double cy = ay + (Bounds.Height / 2.0 - anchor.Y);
        _centerLon = NormalizeLon(XToLon(cx, _zoom));
        _centerLat = ClampLat(YToLat(cy, _zoom));
        _userMovedView = true;
        SpiderCollapse();
        InvalidateVisual();
    }

    public void ZoomIn() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1);
    public void ZoomOut() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), -1);

    /// <summary>Centers on the home location, or fits all markers when no home
    /// is set (a one-time fit, without arming follow mode).</summary>
    public void GoHome()
    {
        _followHome = false;
        FollowHomeChanged?.Invoke(false);
        if (_vm is not null && _vm.TryGetHomeLocation(out double lat, out double lon))
        {
            _centerLat = ClampLat(lat);
            _centerLon = lon;
            if (_zoom < 12) _zoom = 14;
            _userMovedView = true;
        }
        else
        {
            _userMovedView = FitToMarkers();
        }
        InvalidateVisual();
    }

    public void FitAll()
    {
        _followHome = false;
        FollowHomeChanged?.Invoke(false);
        _userMovedView = true;
        FitToMarkers();
        InvalidateVisual();
    }

    public void SetFollowHome(bool follow)
    {
        _followHome = follow;
        if (follow && _vm is not null && _vm.TryGetHomeLocation(out double lat, out double lon))
        {
            _centerLat = ClampLat(lat);
            _centerLon = lon;
            InvalidateVisual();
        }
    }

    public void SetClusterNodes(bool cluster)
    {
        _clusterNodes = cluster;
        if (!cluster) SpiderCollapse();
        InvalidateVisual();
    }

    public void SetTileTheme(string theme)
    {
        if (string.Equals(s_mapTileTheme, theme, StringComparison.Ordinal)) return;
        s_mapTileTheme = theme;
        TileThemeChanged?.Invoke();
    }

    /// <summary>Redraws this canvas against the shared theme. Also run on
    /// attach, since a canvas detached while the theme changed (an unselected
    /// tab, a window not yet open) missed the notification.</summary>
    private void ApplyTileTheme()
    {
        // Switching to a shallower provider while zoomed past what it publishes
        // would otherwise leave a blank map and a run of requests for tiles
        // that have never existed.
        _zoom = Math.Min(_zoom, CurrentTiles.DeepestZoom);

        AttributionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Resets an in-progress corner pick, so a stale first corner from
    /// a cancelled pick never silently completes a later one.</summary>
    public void ResetBoundingBoxPick()
    {
        _pendingBboxCorner = null;
        InvalidateVisual();
    }

    public void CenterOn(double lat, double lon, int zoom = 14)
    {
        _centerLat = ClampLat(lat);
        _centerLon = NormalizeLon(lon);
        _zoom = Math.Clamp(zoom, MinZoom, CurrentTiles.DeepestZoom);
        _userMovedView = true;
        InvalidateVisual();
    }

    /// <summary>Center and zoom so every marker fits. Returns true if the
    /// viewport actually moved.</summary>
    private bool FitToMarkers()
    {
        var markers = Markers;
        if (markers.Count == 0) return false;

        double oldLat = _centerLat, oldLon = _centerLon;
        int oldZoom = _zoom;

        if (markers.Count == 1)
        {
            _centerLat = ClampLat(markers[0].Lat);
            _centerLon = markers[0].Lon;
            _zoom = 13;
        }
        else
        {
            double minLat = double.MaxValue, maxLat = double.MinValue;
            double minLon = double.MaxValue, maxLon = double.MinValue;
            foreach (var m in markers)
            {
                minLat = Math.Min(minLat, m.Lat); maxLat = Math.Max(maxLat, m.Lat);
                minLon = Math.Min(minLon, m.Lon); maxLon = Math.Max(maxLon, m.Lon);
            }

            _centerLat = ClampLat((minLat + maxLat) / 2.0);
            _centerLon = (minLon + maxLon) / 2.0;

            double w = Bounds.Width > 0 ? Bounds.Width : 600;
            double h = Bounds.Height > 0 ? Bounds.Height : 400;

            int best = MinZoom;
            for (int z = MaxZoom; z >= MinZoom; z--)
            {
                double spanX = Math.Abs(LonToX(maxLon, z) - LonToX(minLon, z));
                double spanY = Math.Abs(LatToY(minLat, z) - LatToY(maxLat, z));
                if (spanX <= w * 0.85 && spanY <= h * 0.85) { best = z; break; }
            }
            _zoom = best;
        }

        return Math.Abs(oldLat - _centerLat) > 1e-9
            || Math.Abs(oldLon - _centerLon) > 1e-9
            || oldZoom != _zoom;
    }

    // -- Persistence --------------------------------------------------------

    /// <summary>Restores viewport and map preferences. Marks the view
    /// user-moved so the auto-fit doesn't override the restored position.</summary>
    public void LoadFromSettings(AppSettings settings)
    {
        _clusterNodes = settings.MapClusterNodes;
        // A saved "Auto" (this app's old default, or MeshRF.App's) is no
        // longer an option here — fall back to Dark.
        var theme = settings.MapTileTheme;
        SetTileTheme(MapTileThemeOptions.Contains(theme) ? theme! : DefaultTileTheme);

        if (settings.MapCenterLat is double lat && settings.MapCenterLon is double lon
            && settings.MapZoom >= MinZoom && settings.MapZoom <= MaxZoom)
        {
            _centerLat = ClampLat(lat);
            _centerLon = lon;
            _zoom = settings.MapZoom;
            _userMovedView = true;
        }
        InvalidateVisual();
    }

    public void SaveToSettings(AppSettings settings)
    {
        settings.MapCenterLat = _centerLat;
        settings.MapCenterLon = _centerLon;
        settings.MapZoom = _zoom;
        settings.MapClusterNodes = _clusterNodes;
        settings.MapTileTheme = s_mapTileTheme;
    }

    public bool ClusterNodes => _clusterNodes;
    public string TileTheme => s_mapTileTheme;
}
