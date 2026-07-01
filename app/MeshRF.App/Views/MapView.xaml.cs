// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeshRF.App;
using MeshRF.App.ViewModels;
using MeshRF.Channels;
using Path = System.IO.Path;
namespace MeshRF.App.Views;

/// <summary>
/// A self-contained OpenStreetMap "slippy map" rendered directly onto a WPF
/// <see cref="Canvas"/>. Standard 256px Web-Mercator tiles are fetched over
/// HTTP and cached on disk, so the control needs no browser runtime — only an
/// internet connection for first-time tile loads. Plots the user's home
/// location plus every node that reports a position.
/// </summary>
public partial class MapView : UserControl
{
    private const int TileSize = 256;
    private const int MinZoom = 2;
    private const int MaxZoom = 19;

    // Tile providers. Multiple basemaps are available; the active one is chosen
    // by the on-map "Map tiles" selector or by the app theme when set to Auto.
    // All providers are free, key-less, and attributed to OpenStreetMap / CARTO.
    private readonly record struct TileProvider(
        string Id, string UrlTemplate, string Subdomains, string Attribution,
        double Brightness = 1.0, double Gamma = 1.0);

    private static readonly TileProvider LightTiles = new(
        "osm",
        "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
        "abc",
        "© OpenStreetMap contributors  ·  Ctrl+left-click send waypoint  ·  Ctrl+right-click set location");

    private static readonly TileProvider LightCartoTiles = new(
        "cartopositron",
        "https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png",
        "abcd",
        "© OpenStreetMap · © CARTO  ·  Ctrl+left-click send waypoint  ·  Ctrl+right-click set location");

    private static readonly TileProvider VoyagerTiles = new(
        "cartovoyager",
        "https://{s}.basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png",
        "abcd",
        "© OpenStreetMap · © CARTO  ·  Ctrl+left-click send waypoint  ·  Ctrl+right-click set location");

    private static readonly TileProvider DarkTiles = new(
        "cartodark",
        "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",
        "abcd",
        "© OpenStreetMap · © CARTO  ·  Ctrl+left-click send waypoint  ·  Ctrl+right-click set location",
        // Gamma correction lifts the low-contrast CARTO dark palette: roads and
        // labels become clearly readable while the dark background is preserved.
        Gamma: 1.8);

    public static readonly IReadOnlyList<string> MapTileThemeOptions =
        ["Auto", "Light", "Light (CARTO)", "Voyager", "Dark"];

    private string _mapTileTheme = "Auto";

    private TileProvider CurrentTiles => _mapTileTheme switch
    {
        "Light"        => LightTiles,
        "Light (CARTO)" => LightCartoTiles,
        "Voyager"      => VoyagerTiles,
        "Dark"         => DarkTiles,
        _              => ThemeManager.IsDark ? DarkTiles : LightTiles, // "Auto"
    };

    private static readonly HttpClient s_http = CreateHttpClient();
    private static readonly string s_cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeshRF", "tiles");
    private static readonly ConcurrentDictionary<string, BitmapSource> s_memCache = new();

    private MainViewModel? _vm;

    // View state: geographic center + integer zoom.
    private double _centerLat = 39.5;
    private double _centerLon = -98.35;
    private int _zoom = 4;

    private bool _dragging;
    private Point _lastMouse;
    private Vector _dragOffset;
    private readonly TranslateTransform _tileDragTransform = new();
    private readonly TranslateTransform _markerDragTransform = new();
    private bool _userMovedView;

    // Temporary elements added when a cluster of stacked nodes is "spidered"
    // out on hover; removed on collapse or on the next full Render.
    private readonly List<UIElement> _spiderElements = new();
    private string? _activeSpiderClusterKey;
    private bool _pendingMarkerRefresh;
    private int _openNodeToolTips;
    private bool _followHome;
    private bool _clusterNodes = true;
    private bool _hasRestoredViewport;
    private readonly HashSet<uint> _clusteredNodeNums = new();
    private readonly Dictionary<uint, (double Lat, double Lon)> _lastNodeMarkerCoords = new();
    private readonly Dictionary<uint, (double X, double Y, int BucketX, int BucketY)> _nodeVisualLayout = new();
    private readonly Dictionary<long, HashSet<uint>> _nodeVisualBuckets = new();
    private readonly HashSet<uint> _pendingNodeMarkerNums = new();
    private readonly DispatcherTimer _nodeMarkerUpdateTimer;
    private readonly DispatcherTimer _fullMarkerRefreshTimer;
    private readonly DispatcherTimer _renderThrottleTimer;
    private readonly DispatcherTimer _liveToolTipTimer;
    private readonly HashSet<ToolTip> _liveToolTips = new();
    private bool _fullMarkerRefreshPending;
    // Increased from 32 to 256 now that coordinates are cached and don't require
    // expensive Web-Mercator projection calculations per update.
    private const int MaxNodeMarkerUpdatesPerTick = 256;
    private static readonly long MapRenderMinIntervalTicks = (long)Math.Ceiling(Stopwatch.Frequency / 60.0);
    private static readonly long DragPreviewMinIntervalTicks = (long)Math.Ceiling(Stopwatch.Frequency / 60.0);
    private static readonly long DragCommitMinIntervalTicks = (long)Math.Ceiling(Stopwatch.Frequency / 5.0);
    private const double DragCommitMinPixels = 48.0;
    private const double ClusterRadiusPx = 14;
    private const double ClusterBucketSizePx = 48;
    private long _lastDragPreviewTick;
    private long _lastDragCommitTick;
    private long _lastMapRenderTick;
    private bool _mapRenderQueued;
    private bool _mapFullRenderQueued;

    private static readonly SolidColorBrush NodeFillBrush = CreateFrozenBrush(Color.FromRgb(0x2d, 0x8c, 0xff));
    private static readonly SolidColorBrush WaypointFillBrush = CreateFrozenBrush(Color.FromRgb(0x2e, 0x7d, 0x32));
    private static readonly SolidColorBrush WaypointExpiredFillBrush = CreateFrozenBrush(Color.FromRgb(0xc6, 0x28, 0x28));
    private static readonly SolidColorBrush ClusterBadgeFillBrush = CreateFrozenBrush(Color.FromRgb(0xff, 0x8c, 0x2d));
    private static readonly SolidColorBrush LocationHistoryStrokeBrush =
        CreateFrozenBrush(Color.FromArgb(0xC0, 0xFF, 0x8C, 0x2D));

    private sealed record NodeVisual(Ellipse Dot, FrameworkElement Label);
    private readonly Dictionary<uint, NodeVisual> _nodeVisuals = new();
    private readonly List<MainViewModel.MapMarker> _cachedMapMarkers = new();
    // Coordinate cache: key is marker index, value is (screenX, screenY) in world coordinates
    private readonly Dictionary<int, (double X, double Y)> _cachedMarkerScreenCoords = new();
    private int _lastZoomForCoordCache = -1;  // Invalidate cache when zoom changes
    private double _lastCenterLonForCoordCache = double.NaN;
    private double _lastCenterLatForCoordCache = double.NaN;
    private bool _coordCacheValid;
    private CancellationTokenSource? _coordCacheCts;
    private readonly Dictionary<uint, int> _cachedNodeMarkerIndices = new();
    private readonly List<MainViewModel.MapPolyline> _cachedPolylines = new();
    private bool _mapMarkerCacheValid;
    private bool _mapPolylineCacheValid;

    private static readonly bool MapPerfLoggingEnabled = false;
    private readonly Stopwatch _perfStopwatch = Stopwatch.StartNew();
    private TimeSpan _perfWindowStart = TimeSpan.Zero;
    private int _perfMapDataChangedEvents;
    private int _perfNodeMarkersChangedEvents;
    private int _perfNodeMarkersChangedNodes;
    private int _perfOnMarkersChangedCalls;
    private int _perfRenderRequests;
    private int _perfRenderRequestsFull;
    private int _perfRenderRequestsMarkersOnly;
    private int _perfRenderNowCalls;
    private int _perfRenderMarkersOnlyNowCalls;
    private long _perfRenderNowTicks;
    private long _perfRenderMarkersOnlyNowTicks;
    private int _perfUpdateNodeMarkersCalls;
    private int _perfUpdateNodeMarkersNodes;
    private int _perfNodeMarkerTimerTicks;
    private int _perfNodeMarkerTimerBatches;
    private int _perfNodeMarkerTimerNodesProcessed;
    private int _perfFullMarkerRefreshTimerTicks;
    private int _perfQueueFullRefreshCalls;
    private int _perfPendingNodeMax;
    private readonly Dictionary<string, int> _perfQueueFullRefreshReasons =
        new(StringComparer.Ordinal);

    // Always-on (cheap) render accounting so the host window can surface map
    // render cost alongside the waterfall perf line. Separate from the gated
    // [MapPerf] logging above so it is available without a debug rebuild.
    private int _liveRenderCount;
    private long _liveRenderTicks;

    /// <summary>Drains the render counters accumulated since the last call.
    /// Returns the number of map renders (full + markers-only) and the total
    /// time spent in them, then resets both counters.</summary>
    public (int renders, double totalMs) DrainRenderStats()
    {
        int renders = _liveRenderCount;
        double ms = _liveRenderTicks * 1000.0 / Stopwatch.Frequency;
        _liveRenderCount = 0;
        _liveRenderTicks = 0;
        return (renders, ms);
    }

    /// <summary>Fired when the user double-clicks a node marker on the map.
    /// Opens the same conversation tab as double-clicking the node list.</summary>
    public event Action<MeshRF.Nodes.NodeRecord>? NodeDoubleClicked;

    /// <summary>Fired when the user right-clicks a node marker on the map.
    /// Passes the node so the caller can show a context menu.</summary>
    public event Action<MeshRF.Nodes.NodeRecord>? NodeRightClicked;

    /// <summary>Fired when the user right-clicks a waypoint marker on the map.</summary>
    public event Action<MeshRF.Waypoints.WaypointRecord>? WaypointRightClicked;

    public MapView()
    {
        InitializeComponent();
        TileCanvas.RenderTransform = _tileDragTransform;
        MarkerCanvas.RenderTransform = _markerDragTransform;
        // ContextIdle can starve while the user is actively dragging (many
        // MouseMove events), which makes marker updates appear deferred until
        // mouse-up. Background keeps updates flowing during interaction.
        _nodeMarkerUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _nodeMarkerUpdateTimer.Tick += OnNodeMarkerUpdateTimerTick;
        _fullMarkerRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _fullMarkerRefreshTimer.Tick += OnFullMarkerRefreshTimerTick;
        _renderThrottleTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1),
        };
        _renderThrottleTimer.Tick += OnRenderThrottleTimerTick;
        _liveToolTipTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _liveToolTipTimer.Tick += OnLiveToolTipTimerTick;
        Directory.CreateDirectory(s_cacheDir);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Render();
        DataContextChanged += OnDataContextChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        MapTileThemeCombo.ItemsSource = MapTileThemeOptions;
        MapTileThemeCombo.SelectedItem = _mapTileTheme;
        AttributionText.Text = CurrentTiles.Attribution;
    }

    private void OnThemeChanged()
    {
        // When map tiles is "Auto", tile provider follows the app theme; drop
        // on-screen tiles and redraw with the new basemap. In explicit-theme
        // mode this only updates the attribution text (provider unchanged).
        AttributionText.Text = CurrentTiles.Attribution;
        Render();
    }

    private void OnMapTileThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapTileThemeCombo.SelectedItem is string theme)
        {
            _mapTileTheme = theme;
            AttributionText.Text = CurrentTiles.Attribution;
            Render();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient();
        // OSM tile usage policy requires a descriptive User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRF/1.0 (SDR receiver)");
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as MainViewModel;
        InvalidateMapDataCache();
        Subscribe();
        FitToMarkers();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Re-subscribe on every load: when this control lives on an inactive
        // TabItem it gets Unloaded (and we unsubscribe) on tab switch, but
        // DataContextChanged does NOT fire again when returning, so without
        // this the map would silently stop receiving MapDataChanged updates
        // (home/node markers wouldn't refresh until a manual pan/zoom).
        if (_vm is null) _vm = DataContext as MainViewModel;
        Subscribe();
        if (_hasRestoredViewport)
        {
            Render();
            return;
        }

        if (FitToMarkers())
            _userMovedView = true;
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderThrottleTimer.Stop();
        _liveToolTipTimer.Stop();
        _liveToolTips.Clear();
        _mapRenderQueued = false;
        _mapFullRenderQueued = false;
        Unsubscribe();
        PerfMaybeLog("unloaded");
    }

    private void OnLiveToolTipTimerTick(object? sender, EventArgs e)
    {
        if (_liveToolTips.Count == 0)
        {
            _liveToolTipTimer.Stop();
            return;
        }

        var stale = new List<ToolTip>();
        foreach (var tip in _liveToolTips)
        {
            if (!tip.IsOpen)
            {
                stale.Add(tip);
                continue;
            }

            if (tip.Tag is Func<string> resolve)
                UpdateToolTipContent(tip, resolve());
        }

        if (stale.Count > 0)
            foreach (var tip in stale)
                _liveToolTips.Remove(tip);

        if (_liveToolTips.Count == 0)
            _liveToolTipTimer.Stop();
    }

    private void OnRenderThrottleTimerTick(object? sender, EventArgs e)
    {
        _renderThrottleTimer.Stop();
        if (!_mapRenderQueued && !_mapFullRenderQueued)
            return;

        _mapRenderQueued = false;
        bool fullRender = _mapFullRenderQueued;
        _mapFullRenderQueued = false;
        _lastMapRenderTick = Stopwatch.GetTimestamp();

        if (fullRender)
            RenderNow();
        else
            RenderMarkersOnlyNow();
    }

    private void RequestRender(bool fullRender)
    {
        if (MapPerfLoggingEnabled)
        {
            _perfRenderRequests++;
            if (fullRender) _perfRenderRequestsFull++;
            else _perfRenderRequestsMarkersOnly++;
        }

        _mapFullRenderQueued |= fullRender;

        long nowTicks = Stopwatch.GetTimestamp();
        long elapsedTicks = nowTicks - _lastMapRenderTick;
        if (elapsedTicks >= MapRenderMinIntervalTicks)
        {
            _renderThrottleTimer.Stop();
            _mapRenderQueued = false;

            bool doFullRender = _mapFullRenderQueued;
            _mapFullRenderQueued = false;
            _lastMapRenderTick = nowTicks;

            if (doFullRender)
                RenderNow();
            else
                RenderMarkersOnlyNow();
            return;
        }

        if (_mapRenderQueued)
            return;

        _mapRenderQueued = true;
        long remainingTicks = MapRenderMinIntervalTicks - elapsedTicks;
        int remainingMs = Math.Max(1, (int)Math.Ceiling(remainingTicks * 1000.0 / Stopwatch.Frequency));
        _renderThrottleTimer.Interval = TimeSpan.FromMilliseconds(remainingMs);
        if (!_renderThrottleTimer.IsEnabled)
            _renderThrottleTimer.Start();
    }

    private void OnNodeMarkerUpdateTimerTick(object? sender, EventArgs e)
    {
        if (MapPerfLoggingEnabled)
            _perfNodeMarkerTimerTicks++;

        if (_pendingNodeMarkerNums.Count == 0)
        {
            _nodeMarkerUpdateTimer.Stop();
            PerfMaybeLog("node-timer-empty");
            return;
        }

        // Process marker updates in bounded chunks so large telemetry bursts
        // don't monopolize the UI thread and stutter spectrum/waterfall draws.
        var changed = _pendingNodeMarkerNums
            .Take(MaxNodeMarkerUpdatesPerTick)
            .ToArray();
        foreach (var nodeNum in changed)
            _pendingNodeMarkerNums.Remove(nodeNum);

        if (MapPerfLoggingEnabled)
        {
            _perfNodeMarkerTimerBatches++;
            _perfNodeMarkerTimerNodesProcessed += changed.Length;
        }

        OnNodeMarkersChangedCore(changed);

        if (_pendingNodeMarkerNums.Count == 0)
        {
            _nodeMarkerUpdateTimer.Stop();
            PerfMaybeLog("node-timer-drained");
            return;
        }

        if (!_nodeMarkerUpdateTimer.IsEnabled)
            _nodeMarkerUpdateTimer.Start();

        PerfMaybeLog("node-timer");
    }

    private void OnFullMarkerRefreshTimerTick(object? sender, EventArgs e)
    {
        if (MapPerfLoggingEnabled)
            _perfFullMarkerRefreshTimerTicks++;

        _fullMarkerRefreshTimer.Stop();
        if (!_fullMarkerRefreshPending) return;

        _fullMarkerRefreshPending = false;
        RenderMarkersOnly();
        PerfMaybeLog("full-refresh-applied");
    }

    private void Subscribe()
    {
        if (_vm is null) return;
        _vm.MapDataChanged -= OnMapDataChanged; // avoid double subscription
        _vm.MapDataChanged += OnMapDataChanged;
        _vm.NodeMarkersChanged -= OnNodeMarkersChanged;
        _vm.NodeMarkersChanged += OnNodeMarkersChanged;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.MapDataChanged -= OnMapDataChanged;
            _vm.NodeMarkersChanged -= OnNodeMarkersChanged;
        }
    }

    private void OnNodeMarkersChanged(IReadOnlyCollection<uint> nodeNums)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnNodeMarkersChanged(nodeNums)));
            return;
        }

        foreach (var nodeNum in nodeNums)
            _pendingNodeMarkerNums.Add(nodeNum);

        if (MapPerfLoggingEnabled)
        {
            _perfNodeMarkersChangedEvents++;
            _perfNodeMarkersChangedNodes += nodeNums.Count;
            if (_pendingNodeMarkerNums.Count > _perfPendingNodeMax)
                _perfPendingNodeMax = _pendingNodeMarkerNums.Count;
        }

        if (!_nodeMarkerUpdateTimer.IsEnabled)
            _nodeMarkerUpdateTimer.Start();

        PerfMaybeLog("node-event");
    }

    private void OnNodeMarkersChangedCore(IReadOnlyCollection<uint> nodeNums)
    {
        if (nodeNums.Count == 0) return;
        var changedMarkers = _vm?.GetNodeMapMarkers(nodeNums)
            ?? new Dictionary<uint, MainViewModel.MapMarker>();
        UpdateCachedNodeMarkers(nodeNums, changedMarkers);

        // Any auto-viewport behavior needs a full marker pass.
        if (_followHome || !_userMovedView)
        {
            OnMarkersChanged();
            return;
        }

        if (!_clusterNodes)
        {
            UpdateNodeMarkers(nodeNums, changedMarkers);
            return;
        }

        UpdateNodeMarkers(nodeNums, changedMarkers);
    }

    private void OnMapDataChanged(object? sender, EventArgs e)
    {
        if (MapPerfLoggingEnabled)
            _perfMapDataChangedEvents++;

        InvalidateMapDataCache();
        if (Dispatcher.CheckAccess()) OnMarkersChanged();
        else Dispatcher.BeginInvoke(new Action(OnMarkersChanged));

        PerfMaybeLog("map-data");
    }

    private void OnMarkersChanged()
    {
        if (MapPerfLoggingEnabled)
            _perfOnMarkersChangedCalls++;

        if (_dragging)
        {
            // Keep marker-only refreshes flowing while dragging; tile redraws
            // are still handled by coarse drag commits.
            _pendingMarkerRefresh = true;
            RenderMarkersOnly();
            PerfMaybeLog("markers-dragging-markers-only");
            return;
        }

        _fullMarkerRefreshPending = false;
        if (_fullMarkerRefreshTimer.IsEnabled)
            _fullMarkerRefreshTimer.Stop();

        bool viewportChanged = false;

        if (_followHome && _vm?.HomeLatitude is double flat && _vm.HomeLongitude is double flon)
        {
            double newLat = ClampLat(flat);
            if (Math.Abs(newLat - _centerLat) > 1e-9 || Math.Abs(flon - _centerLon) > 1e-9)
            {
                _centerLat = newLat;
                _centerLon = flon;
                viewportChanged = true;
            }
        }
        else if (!_userMovedView)
        {
            if (_hasRestoredViewport)
                return;

            // Auto-center: prefer home if available, otherwise fit all.
            var markers = GetCachedMapMarkers();
            bool hasMarkers = markers is { Count: > 0 };
            var home = markers?.FirstOrDefault(m => m.IsHome);
            if (home is not null)
            {
                double newLat = ClampLat(home.Lat);
                if (Math.Abs(newLat - _centerLat) > 1e-9 || Math.Abs(home.Lon - _centerLon) > 1e-9 || _zoom < 10)
                {
                    _centerLat = newLat;
                    _centerLon = home.Lon;
                    if (_zoom < 10) _zoom = 12;
                    viewportChanged = true;
                }
            }
            else
            {
                viewportChanged = FitToMarkers(markers);
            }

            // Treat auto-viewport as a one-time startup/default behavior.
            // Continuous recentering/refitting under high update rate causes
            // visible stutter when many nodes are on screen.
            if (hasMarkers)
                _userMovedView = true;
        }

        if (viewportChanged)
            Render();
        else
            RenderMarkersOnly();

        PerfMaybeLog(viewportChanged ? "markers-full" : "markers-only");
    }

    // -- Web-Mercator projection helpers ------------------------------------

    private static double LonToX(double lon, int zoom) =>
        (lon + 180.0) / 360.0 * (1 << zoom) * TileSize;

    private static double LatToY(double lat, int zoom)
    {
        var rad = lat * Math.PI / 180.0;
        var n = 1 << zoom;
        return (1.0 - Math.Log(Math.Tan(rad) + 1.0 / Math.Cos(rad)) / Math.PI)
               / 2.0 * n * TileSize;
    }

    private static double XToLon(double x, int zoom) =>
        x / ((1 << zoom) * TileSize) * 360.0 - 180.0;

    private static double YToLat(double y, int zoom)
    {
        var n = 1 << zoom;
        var t = Math.PI * (1.0 - 2.0 * y / (n * TileSize));
        return Math.Atan(Math.Sinh(t)) * 180.0 / Math.PI;
    }

    // -- Coordinate caching for performance with large marker counts --------

    /// <summary>Pre-computes and caches screen coordinates for all markers on a background thread.
    /// This avoids expensive Web-Mercator calculations (Math.Log, Math.Tan, Math.Cos) per marker
    /// during every render, which was causing drag lag with 500+ nodes.</summary>
    private async void InvalidateAndRefreshCoordinateCache()
    {
        _coordCacheValid = false;
        _coordCacheCts?.Cancel();
        _coordCacheCts = new CancellationTokenSource();
        var cts = _coordCacheCts;

        // Pre-compute all marker screen coordinates on background thread
        await Task.Run(() =>
        {
            if (cts.Token.IsCancellationRequested) return;

            var newCache = new Dictionary<int, (double X, double Y)>(_cachedMapMarkers.Count);
            for (int i = 0; i < _cachedMapMarkers.Count; i++)
            {
                if (cts.Token.IsCancellationRequested)
                    return;

                var mk = _cachedMapMarkers[i];
                double x = LonToX(mk.Lon, _zoom);
                double y = LatToY(mk.Lat, _zoom);
                newCache[i] = (x, y);
            }

            if (cts.Token.IsCancellationRequested)
                return;

            // Update cache on UI thread
            Dispatcher.InvokeAsync(() =>
            {
                if (cts.Token.IsCancellationRequested)
                    return;

                _cachedMarkerScreenCoords.Clear();
                foreach (var kvp in newCache)
                    _cachedMarkerScreenCoords[kvp.Key] = kvp.Value;

                _lastZoomForCoordCache = _zoom;
                _lastCenterLonForCoordCache = _centerLon;
                _lastCenterLatForCoordCache = _centerLat;
                _coordCacheValid = true;
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, cts.Token).ConfigureAwait(true);
    }

    private void InvalidateCoordinateCache()
    {
        _coordCacheValid = false;
        _coordCacheCts?.Cancel();
    }

    /// <summary>Gets pre-computed screen coordinates for a marker, or computes on-demand if cache miss.</summary>
    private (double X, double Y) GetMarkerScreenCoords(int markerIndex, MainViewModel.MapMarker mk)
    {
        if (_coordCacheValid && _cachedMarkerScreenCoords.TryGetValue(markerIndex, out var coords))
            return coords;

        // Cache miss or invalid: compute on-demand (slower path, but rare during drag)
        return (LonToX(mk.Lon, _zoom), LatToY(mk.Lat, _zoom));
    }

    // -- Rendering ----------------------------------------------------------

    private void Render() => RequestRender(fullRender: true);

    private void RenderNow()
    {
        long liveStart = Stopwatch.GetTimestamp();
        long startTicks = MapPerfLoggingEnabled ? liveStart : 0;
        var w = MarkerCanvas.ActualWidth;
        var h = MarkerCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        TileCanvas.Children.Clear();
        MarkerCanvas.Children.Clear();
        _nodeVisuals.Clear();
        _nodeVisualLayout.Clear();
        _nodeVisualBuckets.Clear();
        _clusteredNodeNums.Clear();
        _lastNodeMarkerCoords.Clear();

        // World-pixel coordinate of the viewport center.
        double cx = LonToX(_centerLon, _zoom);
        double cy = LatToY(_centerLat, _zoom);

        // Top-left world-pixel of the viewport.
        double originX = cx - w / 2.0;
        double originY = cy - h / 2.0;

        int n = 1 << _zoom;
        int firstTileX = (int)Math.Floor(originX / TileSize);
        int firstTileY = (int)Math.Floor(originY / TileSize);
        int tilesX = (int)Math.Ceiling(w / TileSize) + 2;
        int tilesY = (int)Math.Ceiling(h / TileSize) + 2;

        for (int tx = firstTileX; tx < firstTileX + tilesX; tx++)
        {
            for (int ty = firstTileY; ty < firstTileY + tilesY; ty++)
            {
                if (ty < 0 || ty >= n) continue;
                int wrappedX = ((tx % n) + n) % n; // wrap horizontally
                double left = tx * TileSize - originX;
                double top = ty * TileSize - originY;
                PlaceTile(wrappedX, ty, _zoom, left, top);
            }
        }

        DrawMarkers(originX, originY);

        _liveRenderCount++;
        _liveRenderTicks += Stopwatch.GetTimestamp() - liveStart;
        if (MapPerfLoggingEnabled)
        {
            _perfRenderNowCalls++;
            _perfRenderNowTicks += Stopwatch.GetTimestamp() - startTicks;
            PerfMaybeLog("render-now");
        }
    }

    /// <summary>Redraws only the marker layer without touching the tile layer.
    /// Called when node data changes but the viewport (center/zoom) is unchanged
    /// so the tile canvas is left in place and no blink occurs.</summary>
    private void RenderMarkersOnly() => RequestRender(fullRender: false);

    private void RenderMarkersOnlyNow()
    {
        long liveStart = Stopwatch.GetTimestamp();
        long startTicks = MapPerfLoggingEnabled ? liveStart : 0;
        var w = MarkerCanvas.ActualWidth;
        var h = MarkerCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double originX = LonToX(_centerLon, _zoom) - w / 2.0;
        double originY = LatToY(_centerLat, _zoom) - h / 2.0;
        MarkerCanvas.Children.Clear();
        _nodeVisuals.Clear();
        _nodeVisualLayout.Clear();
        _nodeVisualBuckets.Clear();
        _clusteredNodeNums.Clear();
        _lastNodeMarkerCoords.Clear();
        DrawMarkers(originX, originY);

        _liveRenderCount++;
        _liveRenderTicks += Stopwatch.GetTimestamp() - liveStart;
        if (MapPerfLoggingEnabled)
        {
            _perfRenderMarkersOnlyNowCalls++;
            _perfRenderMarkersOnlyNowTicks += Stopwatch.GetTimestamp() - startTicks;
            PerfMaybeLog("render-markers");
        }
    }

    private void UpdateNodeMarkers(
        IReadOnlyCollection<uint> nodeNums,
        IReadOnlyDictionary<uint, MainViewModel.MapMarker>? markerUpdates = null)
    {
        if (MapPerfLoggingEnabled)
        {
            _perfUpdateNodeMarkersCalls++;
            _perfUpdateNodeMarkersNodes += nodeNums.Count;
        }

        if (_vm is null) return;

        double w = MarkerCanvas.ActualWidth;
        double h = MarkerCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double originX = LonToX(_centerLon, _zoom) - w / 2.0;
        double originY = LatToY(_centerLat, _zoom) - h / 2.0;
        const double cullMarginPx = 48;
        double clusterRadiusSq = ClusterRadiusPx * ClusterRadiusPx;

        var changedMarkers = markerUpdates ?? _vm.GetNodeMapMarkers(nodeNums);

        if (!_clusterNodes)
        {
            foreach (var nodeNum in nodeNums)
            {
                if (!changedMarkers.TryGetValue(nodeNum, out var mk))
                {
                    RemoveNodeVisual(nodeNum);
                    continue;
                }

                // Find marker index in cache for coordinate lookup
                int markerIndex = _cachedMapMarkers.FindIndex(m => m.NodeNum == nodeNum);
                var (worldX, worldY) = markerIndex >= 0
                    ? GetMarkerScreenCoords(markerIndex, mk)
                    : (LonToX(mk.Lon, _zoom), LatToY(mk.Lat, _zoom));
                double px = worldX - originX;
                double py = worldY - originY;
                bool isOnScreen =
                    px >= -cullMarginPx && px <= w + cullMarginPx &&
                    py >= -cullMarginPx && py <= h + cullMarginPx;

                if (!isOnScreen)
                {
                    RemoveNodeVisual(nodeNum);
                    continue;
                }

                AddOrUpdateNodeVisual(mk, px, py, updateTooltip: true);
            }
            return;
        }

        foreach (var nodeNum in nodeNums)
        {
            // If this node was or is part of a cluster, cluster badge geometry
            // may change, so rebuild markers via the normal cluster path.
            if (_clusteredNodeNums.Contains(nodeNum))
            {
                if (changedMarkers.TryGetValue(nodeNum, out var clusteredMk) &&
                    _lastNodeMarkerCoords.TryGetValue(nodeNum, out var prevCoords) &&
                    (AreCoordsEquivalent(prevCoords, clusteredMk) ||
                     IsSmallScreenMotion(prevCoords, clusteredMk, maxPixels: 1.5)))
                {
                    // Telemetry-only update while node stays clustered:
                    // no geometry change, so avoid a full marker rebuild.
                    continue;
                }

                QueueFullMarkerRefresh("clustered-node-motion");
                return;
            }

            if (!changedMarkers.TryGetValue(nodeNum, out var mk))
            {
                RemoveNodeVisual(nodeNum);
                continue;
            }

            // Find marker index in cache for coordinate lookup
            int markerIndex = _cachedMapMarkers.FindIndex(m => m.NodeNum == nodeNum);
            var (worldX, worldY) = markerIndex >= 0
                ? GetMarkerScreenCoords(markerIndex, mk)
                : (LonToX(mk.Lon, _zoom), LatToY(mk.Lat, _zoom));
            double px = worldX - originX;
            double py = worldY - originY;
            bool isOnScreen =
                px >= -cullMarginPx && px <= w + cullMarginPx &&
                py >= -cullMarginPx && py <= h + cullMarginPx;

            if (!isOnScreen)
            {
                RemoveNodeVisual(nodeNum);
                continue;
            }

            bool geometryUnchanged =
                _lastNodeMarkerCoords.TryGetValue(nodeNum, out var existingCoords)
                && AreCoordsEquivalent(existingCoords, mk);
            if (geometryUnchanged)
            {
                // Telemetry-only updates are common; avoid forcing cluster
                // geometry checks/rebuilds when marker position is unchanged.
                AddOrUpdateNodeVisual(mk, px, py, updateTooltip: true);
                continue;
            }

            // If this update would create a stacked-node cluster, rebuild via
            // the normal cluster path so expansion badges stay correct.
            if (HasNearbyVisibleNode(nodeNum, px, py, clusterRadiusSq))
            {
                QueueFullMarkerRefresh("nearby-node-would-cluster");
                return;
            }

            AddOrUpdateNodeVisual(mk, px, py, updateTooltip: true);
        }
    }

    private static long GetBucketKey(int bucketX, int bucketY) =>
        ((long)bucketX << 32) | (uint)bucketY;

    private static (int BucketX, int BucketY) GetBucket(double px, double py) =>
        ((int)Math.Floor(px / ClusterBucketSizePx), (int)Math.Floor(py / ClusterBucketSizePx));

    private void AddToBucket(int bucketX, int bucketY, uint nodeNum)
    {
        long key = GetBucketKey(bucketX, bucketY);
        if (!_nodeVisualBuckets.TryGetValue(key, out var members))
        {
            members = new HashSet<uint>();
            _nodeVisualBuckets[key] = members;
        }
        members.Add(nodeNum);
    }

    private void RemoveFromBucket(int bucketX, int bucketY, uint nodeNum)
    {
        long key = GetBucketKey(bucketX, bucketY);
        if (!_nodeVisualBuckets.TryGetValue(key, out var members))
            return;

        members.Remove(nodeNum);
        if (members.Count == 0)
            _nodeVisualBuckets.Remove(key);
    }

    private void UpdateNodeVisualSpatialIndex(uint nodeNum, double px, double py)
    {
        var (bucketX, bucketY) = GetBucket(px, py);
        if (_nodeVisualLayout.TryGetValue(nodeNum, out var old))
        {
            if (old.BucketX != bucketX || old.BucketY != bucketY)
            {
                RemoveFromBucket(old.BucketX, old.BucketY, nodeNum);
                AddToBucket(bucketX, bucketY, nodeNum);
            }
        }
        else
        {
            AddToBucket(bucketX, bucketY, nodeNum);
        }

        _nodeVisualLayout[nodeNum] = (px, py, bucketX, bucketY);
    }

    private void RemoveNodeVisualSpatialIndex(uint nodeNum)
    {
        if (!_nodeVisualLayout.Remove(nodeNum, out var old))
            return;

        RemoveFromBucket(old.BucketX, old.BucketY, nodeNum);
    }

    private bool HasNearbyVisibleNode(uint nodeNum, double px, double py, double clusterRadiusSq)
    {
        var (bucketX, bucketY) = GetBucket(px, py);
        for (int bx = bucketX - 1; bx <= bucketX + 1; bx++)
        {
            for (int by = bucketY - 1; by <= bucketY + 1; by++)
            {
                long key = GetBucketKey(bx, by);
                if (!_nodeVisualBuckets.TryGetValue(key, out var members))
                    continue;

                foreach (var otherNodeNum in members)
                {
                    if (otherNodeNum == nodeNum) continue;
                    if (!_nodeVisualLayout.TryGetValue(otherNodeNum, out var other)) continue;

                    double dx = px - other.X;
                    double dy = py - other.Y;
                    if (dx * dx + dy * dy <= clusterRadiusSq)
                        return true;
                }
            }
        }

        return false;
    }

    private static bool AreCoordsEquivalent((double Lat, double Lon) a, MainViewModel.MapMarker b) =>
        Math.Abs(a.Lat - b.Lat) < 1e-7 && Math.Abs(a.Lon - b.Lon) < 1e-7;

    private bool IsSmallScreenMotion((double Lat, double Lon) a, MainViewModel.MapMarker b, double maxPixels)
    {
        double ax = LonToX(a.Lon, _zoom);
        double ay = LatToY(a.Lat, _zoom);
        double bx = LonToX(b.Lon, _zoom);
        double by = LatToY(b.Lat, _zoom);
        double dx = bx - ax;
        double dy = by - ay;
        return dx * dx + dy * dy <= maxPixels * maxPixels;
    }

    private void InvalidateMapDataCache(bool markers = true, bool polylines = true)
    {
        if (markers)
            _mapMarkerCacheValid = false;
        if (polylines)
            _mapPolylineCacheValid = false;
    }

    private IReadOnlyList<MainViewModel.MapMarker> GetCachedMapMarkers()
    {
        EnsureMapDataCache();
        return _cachedMapMarkers;
    }

    private IReadOnlyList<MainViewModel.MapPolyline> GetCachedPolylines()
    {
        EnsureMapDataCache();
        return _cachedPolylines;
    }

    private void EnsureMapDataCache()
    {
        if (_vm is null)
        {
            _cachedMapMarkers.Clear();
            _cachedNodeMarkerIndices.Clear();
            _cachedPolylines.Clear();
            _mapMarkerCacheValid = true;
            _mapPolylineCacheValid = true;
            return;
        }

        if (!_mapMarkerCacheValid)
        {
            _cachedMapMarkers.Clear();
            _cachedMapMarkers.AddRange(_vm.GetMapMarkers());
            RebuildCachedNodeMarkerIndex();
                InvalidateAndRefreshCoordinateCache();  // Re-compute coords for updated markers
            _mapMarkerCacheValid = true;
        }

        if (!_mapPolylineCacheValid)
        {
            _cachedPolylines.Clear();
            _cachedPolylines.AddRange(_vm.GetMapPolylines());
            _mapPolylineCacheValid = true;
        }
    }

    private void RebuildCachedNodeMarkerIndex()
    {
        _cachedNodeMarkerIndices.Clear();
        for (int i = 0; i < _cachedMapMarkers.Count; i++)
            if (_cachedMapMarkers[i].NodeNum is uint nodeNum)
                _cachedNodeMarkerIndices[nodeNum] = i;
    }

    private void UpdateCachedNodeMarkers(
        IReadOnlyCollection<uint> nodeNums,
        IReadOnlyDictionary<uint, MainViewModel.MapMarker> changedMarkers)
    {
        if (!_mapMarkerCacheValid)
            return;

        foreach (var nodeNum in nodeNums)
        {
            bool hadExisting = _cachedNodeMarkerIndices.TryGetValue(nodeNum, out int existingIndex);

            if (changedMarkers.TryGetValue(nodeNum, out var updated))
            {
                if (hadExisting)
                {
                    _cachedMapMarkers[existingIndex] = updated;
                }
                else
                {
                    _cachedNodeMarkerIndices[nodeNum] = _cachedMapMarkers.Count;
                    _cachedMapMarkers.Add(updated);
                }

                continue;
            }

            if (!hadExisting)
                continue;

            int lastIndex = _cachedMapMarkers.Count - 1;
            var swappedMarker = _cachedMapMarkers[lastIndex];
            _cachedMapMarkers[existingIndex] = swappedMarker;
            _cachedMapMarkers.RemoveAt(lastIndex);
            _cachedNodeMarkerIndices.Remove(nodeNum);

            if (existingIndex < _cachedMapMarkers.Count && swappedMarker.NodeNum is uint swappedNodeNum)
                _cachedNodeMarkerIndices[swappedNodeNum] = existingIndex;
        }
    }

    private void QueueFullMarkerRefresh(string reason = "unknown")
    {
        if (MapPerfLoggingEnabled)
        {
            _perfQueueFullRefreshCalls++;
            _perfQueueFullRefreshReasons[reason] =
                _perfQueueFullRefreshReasons.GetValueOrDefault(reason) + 1;
        }

        _fullMarkerRefreshPending = true;

        if (!_fullMarkerRefreshTimer.IsEnabled)
            _fullMarkerRefreshTimer.Start();

        PerfMaybeLog("queue-full");
    }

    private void PerfMaybeLog(string source)
    {
        if (!MapPerfLoggingEnabled)
            return;

        var now = _perfStopwatch.Elapsed;
        var window = now - _perfWindowStart;
        if (window < TimeSpan.FromSeconds(1))
            return;

        double windowSec = Math.Max(window.TotalSeconds, 0.001);
        double renderNowAvgMs = _perfRenderNowCalls == 0
            ? 0
            : (_perfRenderNowTicks * 1000.0 / Stopwatch.Frequency) / _perfRenderNowCalls;
        double renderMarkersAvgMs = _perfRenderMarkersOnlyNowCalls == 0
            ? 0
            : (_perfRenderMarkersOnlyNowTicks * 1000.0 / Stopwatch.Frequency) / _perfRenderMarkersOnlyNowCalls;

        string queueReasons = _perfQueueFullRefreshReasons.Count == 0
            ? "-"
            : string.Join(",", _perfQueueFullRefreshReasons
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => $"{kvp.Key}:{kvp.Value}"));

        string perfLine =
            $"[MapPerf] src={source} dt={windowSec:0.00}s " +
            $"mapData={_perfMapDataChangedEvents} nodeEvt={_perfNodeMarkersChangedEvents}/{_perfNodeMarkersChangedNodes} " +
            $"onMarkers={_perfOnMarkersChangedCalls} updMarkers={_perfUpdateNodeMarkersCalls}/{_perfUpdateNodeMarkersNodes} " +
            $"req={_perfRenderRequests} fullReq={_perfRenderRequestsFull} markerReq={_perfRenderRequestsMarkersOnly} " +
            $"fullNow={_perfRenderNowCalls}@{renderNowAvgMs:0.00}ms markerNow={_perfRenderMarkersOnlyNowCalls}@{renderMarkersAvgMs:0.00}ms " +
            $"nodeTimer={_perfNodeMarkerTimerTicks}/{_perfNodeMarkerTimerBatches}/{_perfNodeMarkerTimerNodesProcessed} " +
            $"fullTimer={_perfFullMarkerRefreshTimerTicks} queueFull={_perfQueueFullRefreshCalls} reasons={queueReasons} " +
            $"pendingMax={_perfPendingNodeMax}";

        Debug.WriteLine(perfLine);
        Trace.WriteLine(perfLine);

        _perfWindowStart = now;
        _perfMapDataChangedEvents = 0;
        _perfNodeMarkersChangedEvents = 0;
        _perfNodeMarkersChangedNodes = 0;
        _perfOnMarkersChangedCalls = 0;
        _perfRenderRequests = 0;
        _perfRenderRequestsFull = 0;
        _perfRenderRequestsMarkersOnly = 0;
        _perfRenderNowCalls = 0;
        _perfRenderMarkersOnlyNowCalls = 0;
        _perfRenderNowTicks = 0;
        _perfRenderMarkersOnlyNowTicks = 0;
        _perfUpdateNodeMarkersCalls = 0;
        _perfUpdateNodeMarkersNodes = 0;
        _perfNodeMarkerTimerTicks = 0;
        _perfNodeMarkerTimerBatches = 0;
        _perfNodeMarkerTimerNodesProcessed = 0;
        _perfFullMarkerRefreshTimerTicks = 0;
        _perfQueueFullRefreshCalls = 0;
        _perfPendingNodeMax = 0;
        _perfQueueFullRefreshReasons.Clear();
    }

    private void PlaceTile(int x, int y, int zoom, double left, double top)
    {
        var img = new Image
        {
            Width = TileSize,
            Height = TileSize,
            SnapsToDevicePixels = true,
        };
        Canvas.SetLeft(img, left);
        Canvas.SetTop(img, top);
        TileCanvas.Children.Add(img);

        var provider = CurrentTiles;
        var key = $"{provider.Id}/{zoom}/{x}/{y}";
        if (s_memCache.TryGetValue(key, out var cached))
        {
            img.Source = cached;
            return;
        }
        _ = LoadTileAsync(key, provider, x, y, zoom, img);
    }

    private async Task LoadTileAsync(string key, TileProvider provider, int x, int y, int zoom, Image target)
    {
        try
        {
            var bmp = await GetTileBitmapAsync(provider, x, y, zoom);
            if (bmp is null) return;
            s_memCache[key] = bmp;
            // The image may have been recycled by a re-render; only set if it's
            // still the tile we asked for (same canvas position key in Tag).
            target.Source = bmp;
        }
        catch { /* tile fetch failed; leave blank */ }
    }

    private static async Task<BitmapSource?> GetTileBitmapAsync(TileProvider provider, int x, int y, int zoom)
    {
        var file = Path.Combine(s_cacheDir, $"{provider.Id}_{zoom}_{x}_{y}.png");
        byte[] bytes;
        if (File.Exists(file))
        {
            bytes = await File.ReadAllBytesAsync(file);
        }
        else
        {
            // Rotate across the provider's tile subdomains.
            var subs = provider.Subdomains;
            var server = subs[(x + y) % subs.Length];
            var url = provider.UrlTemplate
                .Replace("{s}", server.ToString())
                .Replace("{z}", zoom.ToString())
                .Replace("{x}", x.ToString())
                .Replace("{y}", y.ToString());
            bytes = await s_http.GetByteArrayAsync(url);
            try { await File.WriteAllBytesAsync(file, bytes); } catch { /* cache best-effort */ }
        }

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();

        if (provider.Brightness == 1.0 && provider.Gamma == 1.0) return bmp;
        return PostProcessTile(bmp, provider.Brightness, provider.Gamma);
    }

    /// <summary>Returns a post-processed copy of <paramref name="src"/>: each RGB
    /// channel is gamma-corrected then brightness-scaled. Gamma &gt; 1 lifts
    /// the low-contrast CARTO dark palette so roads and labels read clearly
    /// against the dark background without blowing out bright areas.</summary>
    private static BitmapSource PostProcessTile(BitmapSource src, double brightness, double gamma)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[h * stride];
        bgra.CopyPixels(pixels, stride, 0);

        // Precompute the combined gamma + brightness lookup table.
        // gamma > 1 raises midtones (roads/labels visible); brightness trims overall level.
        var lut = new byte[256];
        double gammaInv = (gamma > 0.0 && gamma != 1.0) ? (1.0 / gamma) : 1.0;
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            if (gammaInv != 1.0) v = Math.Pow(v, gammaInv);
            lut[i] = (byte)Math.Min(255.0, v * brightness * 255.0);
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i]     = lut[pixels[i]];     // B
            pixels[i + 1] = lut[pixels[i + 1]]; // G
            pixels[i + 2] = lut[pixels[i + 2]]; // R
            // alpha (i + 3) left unchanged
        }

        var wb = new WriteableBitmap(w, h, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
        wb.Freeze();
        return wb;
    }

    private void DrawMarkers(double originX, double originY)
    {
        if (_vm is null) return;
        EnsureMapDataCache();
        SpiderCollapseImmediate();
        _nodeVisuals.Clear();
        _nodeVisualLayout.Clear();
        _nodeVisualBuckets.Clear();
        _clusteredNodeNums.Clear();
        bool restoredActiveSpider = false;
        double viewportW = MarkerCanvas.ActualWidth;
        double viewportH = MarkerCanvas.ActualHeight;
        // Keep a small margin so edge markers/labels do not pop in late.
        const double cullMarginPx = 48;

        DrawLocationHistory(originX, originY, viewportW, viewportH);

        // Node markers are collected and clustered; the home marker is drawn
        // immediately since it never stacks with nodes.
        var nodes = new List<(MainViewModel.MapMarker mk, double px, double py)>();

        // Use cached screen coordinates instead of expensive Web-Mercator calculations
        foreach (var (markerIndex, mk) in _cachedMapMarkers.Select((m, i) => (i, m)))
        {
            var (worldX, worldY) = GetMarkerScreenCoords(markerIndex, mk);
            double px = worldX - originX;
            double py = worldY - originY;
            bool isOnScreen =
                px >= -cullMarginPx && px <= viewportW + cullMarginPx &&
                py >= -cullMarginPx && py <= viewportH + cullMarginPx;

            if (!isOnScreen)
                continue;

            if (mk.IsHome)
            {
                var home = new TextBlock
                {
                    Text = "\u2302",
                    FontSize = 20,
                    Foreground = Brushes.Gold,
                    ToolTip = mk.Title,
                };
                Canvas.SetLeft(home, px - 8);
                Canvas.SetTop(home, py - 12);
                MarkerCanvas.Children.Add(home);
                AddNodeLabel(mk.Label, px, py);
            }
            else if (mk.IsWaypoint)
            {
                AddNodeDot(mk, px, py);
                AddNodeLabel(mk.Label, px, py);
            }
            else
            {
                if (mk.NodeNum is uint nodeNum)
                    _lastNodeMarkerCoords[nodeNum] = (mk.Lat, mk.Lon);
                nodes.Add((mk, px, py));
            }
        }

        if (!_clusterNodes)
        {
            foreach (var n in nodes)
                AddOrUpdateNodeVisual(n.mk, n.px, n.py);
            _activeSpiderClusterKey = null;
            return;
        }

        // Group node markers that land on (nearly) the same screen pixel so
        // stacked nodes don't hide each other's dot and tooltip.
        const double clusterRadiusPx = ClusterRadiusPx;
        var clusters = BuildMarkerClusters(nodes, clusterRadiusPx);

        var singletons = new List<(MainViewModel.MapMarker mk, double px, double py)>();
        foreach (var c in clusters)
        {
            if (c.Count == 1)
            {
                singletons.Add(c[0]);
            }
            else
            {
                foreach (var member in c)
                    if (member.mk.NodeNum is uint clustered)
                        _clusteredNodeNums.Add(clustered);
                AddCluster(c);
                if (string.Equals(_activeSpiderClusterKey, GetClusterKey(c), StringComparison.Ordinal))
                {
                    SpiderExpand(c, c.Average(m => m.px), c.Average(m => m.py), persistSelection: false);
                    restoredActiveSpider = true;
                }
            }
        }

        // Keep individual node markers above cluster badges.
        foreach (var s in singletons)
            AddOrUpdateNodeVisual(s.mk, s.px, s.py);

        if (!restoredActiveSpider)
            _activeSpiderClusterKey = null;
    }

    private void DrawLocationHistory(double originX, double originY, double viewportW, double viewportH)
    {
        if (_vm is null) return;
        EnsureMapDataCache();

        const double cullMarginPx = 96;
        foreach (var polyline in _cachedPolylines)
        {
            if (polyline.Points.Count < 2)
                continue;

            Point? prev = null;
            bool anyVisible = false;
            var segment = new Polyline
            {
                Stroke = LocationHistoryStrokeBrush,
                StrokeThickness = 2.0,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
                ToolTip = $"Location history: {polyline.Label}",
            };

            foreach (var point in polyline.Points)
            {
                double px = LonToX(point.Lon, _zoom) - originX;
                double py = LatToY(point.Lat, _zoom) - originY;
                var current = new Point(px, py);

                bool currentVisible =
                    px >= -cullMarginPx && px <= viewportW + cullMarginPx &&
                    py >= -cullMarginPx && py <= viewportH + cullMarginPx;
                anyVisible |= currentVisible;

                if (prev is Point prior)
                {
                    // If points are far apart in pixel space, split to avoid
                    // drawing a wrap-around jump across the whole world.
                    if (Math.Abs(current.X - prior.X) > 2048 || Math.Abs(current.Y - prior.Y) > 2048)
                    {
                        if (segment.Points.Count >= 2 && anyVisible)
                            MarkerCanvas.Children.Add(segment);

                        segment = new Polyline
                        {
                            Stroke = LocationHistoryStrokeBrush,
                            StrokeThickness = 2.0,
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                            IsHitTestVisible = false,
                            ToolTip = $"Location history: {polyline.Label}",
                        };
                        anyVisible = currentVisible;
                    }
                }

                segment.Points.Add(current);
                prev = current;
            }

            if (segment.Points.Count >= 2 && anyVisible)
                MarkerCanvas.Children.Add(segment);
        }
    }

    /// <summary>Draws a single marker dot with a hover tooltip.</summary>
    private void AddNodeDot(MainViewModel.MapMarker mk, double px, double py)
    {
        var dot = CreateNodeDot(mk);
        Canvas.SetLeft(dot, px - 6);
        Canvas.SetTop(dot, py - 6);
        Panel.SetZIndex(dot, 20);
        AttachNodeInteraction(dot, mk);
        MarkerCanvas.Children.Add(dot);
    }

    private Ellipse CreateNodeDot(MainViewModel.MapMarker mk)
    {
        var fill = mk.IsWaypoint
            ? (mk.IsExpired ? WaypointExpiredFillBrush : WaypointFillBrush)
            : NodeFillBrush;
        var liveText = BuildLiveToolTipResolver(mk);
        var dot = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = fill,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            ToolTip = BuildNodeToolTip(mk.Title, liveText),
        };
        ToolTipService.SetInitialShowDelay(dot, 250);
        ToolTipService.SetShowDuration(dot, 60000);
        return dot;
    }

    /// <summary>Draws a small caption to the right of a marker.</summary>
    private void AddNodeLabel(string text, double px, double py)
    {
        var label = CreateNodeLabel(text);
        Canvas.SetLeft(label, px + 8);
        Canvas.SetTop(label, py - 8);
        Panel.SetZIndex(label, 21);
        MarkerCanvas.Children.Add(label);
    }

    private FrameworkElement CreateNodeLabel(string text)
    {
        var label = new Emoji.Wpf.TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
            Padding = new Thickness(2, 0, 2, 0),
            IsHitTestVisible = false,
        };
        return label;
    }

    private void AddOrUpdateNodeVisual(
        MainViewModel.MapMarker mk,
        double px,
        double py,
        bool updateTooltip = true)
    {
        if (mk.NodeNum is not uint nodeNum) return;
        _lastNodeMarkerCoords[nodeNum] = (mk.Lat, mk.Lon);

        if (_nodeVisuals.TryGetValue(nodeNum, out var existing))
        {
            if (!ReferenceEquals(existing.Dot.Fill, NodeFillBrush))
                existing.Dot.Fill = NodeFillBrush;
            if (updateTooltip)
                UpdateNodeToolTip(existing.Dot, mk);
            if (existing.Label is Emoji.Wpf.TextBlock tb &&
                !string.Equals(tb.Text, mk.Label, StringComparison.Ordinal))
                tb.Text = mk.Label;
            Canvas.SetLeft(existing.Dot, px - 6);
            Canvas.SetTop(existing.Dot, py - 6);
            Canvas.SetLeft(existing.Label, px + 8);
            Canvas.SetTop(existing.Label, py - 8);
            UpdateNodeVisualSpatialIndex(nodeNum, px, py);
            return;
        }

        var dot = CreateNodeDot(mk);
        var label = CreateNodeLabel(mk.Label);
        Canvas.SetLeft(dot, px - 6);
        Canvas.SetTop(dot, py - 6);
        Canvas.SetLeft(label, px + 8);
        Canvas.SetTop(label, py - 8);
        Panel.SetZIndex(dot, 20);
        Panel.SetZIndex(label, 21);
        AttachNodeInteraction(dot, mk);
        MarkerCanvas.Children.Add(dot);
        MarkerCanvas.Children.Add(label);
        _nodeVisuals[nodeNum] = new NodeVisual(dot, label);
        UpdateNodeVisualSpatialIndex(nodeNum, px, py);
    }

    private Func<string>? BuildLiveToolTipResolver(MainViewModel.MapMarker mk)
    {
        if (mk.NodeNum is uint nodeNum)
            return () => _vm?.GetLiveNodeTooltip(nodeNum) ?? mk.Title;

        if (mk.WaypointRowId is long waypointId)
            return () => _vm?.GetLiveWaypointTooltip(waypointId) ?? mk.Title;

        return null;
    }

    private void UpdateNodeToolTip(FrameworkElement element, MainViewModel.MapMarker mk)
    {
        var liveText = BuildLiveToolTipResolver(mk);
        if (element.ToolTip is ToolTip tip && tip.Content is Emoji.Wpf.TextBlock tb)
        {
            if (!string.Equals(tb.Text, mk.Title, StringComparison.Ordinal))
                tb.Text = mk.Title;
            tip.Tag = liveText;
            if (tip.IsOpen && liveText is not null)
                UpdateToolTipContent(tip, liveText());
            return;
        }
        element.ToolTip = BuildNodeToolTip(mk.Title, liveText);
    }

    private void RemoveNodeVisual(uint nodeNum)
    {
        if (!_nodeVisuals.TryGetValue(nodeNum, out var visual)) return;
        MarkerCanvas.Children.Remove(visual.Dot);
        MarkerCanvas.Children.Remove(visual.Label);
        _nodeVisuals.Remove(nodeNum);
        RemoveNodeVisualSpatialIndex(nodeNum);
        _lastNodeMarkerCoords.Remove(nodeNum);
    }

    /// <summary>Draws a count badge for a group of overlapping nodes. Hovering
    /// the badge fans the members out radially ("spiderfies") so each can be
    /// inspected individually.</summary>
    private void AddCluster(List<(MainViewModel.MapMarker mk, double px, double py)> members)
    {
        double cx = members.Average(m => m.px);
        double cy = members.Average(m => m.py);

        var badge = new Grid { Width = 24, Height = 24, Cursor = Cursors.Hand };
        badge.Children.Add(new Ellipse
        {
            Width = 24,
            Height = 24,
            Fill = ClusterBadgeFillBrush,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
        });
        badge.Children.Add(new TextBlock
        {
            Text = members.Count.ToString(),
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        badge.ToolTip = $"{members.Count} nodes here \u2014 click to expand";
        Canvas.SetLeft(badge, cx - 12);
        Canvas.SetTop(badge, cy - 12);
        Panel.SetZIndex(badge, 5);

        badge.MouseLeftButtonDown += (_, e) =>
        {
            SpiderExpand(members, cx, cy);
            e.Handled = true;
        };
        MarkerCanvas.Children.Add(badge);
    }

    private static string GetClusterKey(List<(MainViewModel.MapMarker mk, double px, double py)> members) =>
        string.Join("\n", members
            .Select(m => $"{m.mk.Label}|{m.mk.Lat:F6}|{m.mk.Lon:F6}")
            .OrderBy(text => text, StringComparer.Ordinal));

    private static List<List<(MainViewModel.MapMarker mk, double px, double py)>> BuildMarkerClusters(
        IReadOnlyList<(MainViewModel.MapMarker mk, double px, double py)> nodes,
        double clusterRadiusPx)
    {
        var clusters = new List<List<(MainViewModel.MapMarker mk, double px, double py)>>();
        if (nodes.Count == 0)
            return clusters;

        double radiusSq = clusterRadiusPx * clusterRadiusPx;
        double cellSize = clusterRadiusPx;
        var clusterAnchors = new List<(double Px, double Py)>();
        var clusterBuckets = new Dictionary<long, List<int>>();

        foreach (var node in nodes)
        {
            int bucketX = (int)Math.Floor(node.px / cellSize);
            int bucketY = (int)Math.Floor(node.py / cellSize);
            int hitClusterIndex = -1;

            for (int bx = bucketX - 1; bx <= bucketX + 1 && hitClusterIndex < 0; bx++)
            {
                for (int by = bucketY - 1; by <= bucketY + 1 && hitClusterIndex < 0; by++)
                {
                    if (!clusterBuckets.TryGetValue(GetBucketKey(bx, by), out var candidateClusters))
                        continue;

                    foreach (var clusterIndex in candidateClusters)
                    {
                        var anchor = clusterAnchors[clusterIndex];
                        double dx = node.px - anchor.Px;
                        double dy = node.py - anchor.Py;
                        if (dx * dx + dy * dy <= radiusSq)
                        {
                            hitClusterIndex = clusterIndex;
                            break;
                        }
                    }
                }
            }

            if (hitClusterIndex >= 0)
            {
                clusters[hitClusterIndex].Add(node);
                continue;
            }

            int newClusterIndex = clusters.Count;
            clusters.Add(new List<(MainViewModel.MapMarker mk, double px, double py)> { node });
            clusterAnchors.Add((node.px, node.py));

            long bucketKey = GetBucketKey(bucketX, bucketY);
            if (!clusterBuckets.TryGetValue(bucketKey, out var bucketMembers))
            {
                bucketMembers = new List<int>();
                clusterBuckets[bucketKey] = bucketMembers;
            }

            bucketMembers.Add(newClusterIndex);
        }

        return clusters;
    }

    /// <summary>Fans a stacked group of nodes out around their shared point and
    /// keeps them open via a transparent hover hull.</summary>
    private void SpiderExpand(
        List<(MainViewModel.MapMarker mk, double px, double py)> members, double cx, double cy,
        bool persistSelection = true)
    {
        if (!_clusterNodes) return;
        SpiderCollapseImmediate();
        if (persistSelection)
            _activeSpiderClusterKey = GetClusterKey(members);

        double legLen = Math.Max(34, 14 + members.Count * 4);

        // All spider parts live in one transparent container Canvas. Handling
        // MouseLeave on the container (rather than on a sibling hull) means
        // moving the pointer between the badge, legs and dots never counts as
        // "leaving" — the spider only collapses when the pointer exits the
        // whole fanned-out region, which stops it vanishing before you can
        // hover a node.
        double hullR = legLen + 16;
        var spider = new Canvas
        {
            Width = hullR * 2,
            Height = hullR * 2,
            Background = Brushes.Transparent,
        };
        Canvas.SetLeft(spider, cx - hullR);
        Canvas.SetTop(spider, cy - hullR);
        Panel.SetZIndex(spider, 9);
        MarkerCanvas.Children.Add(spider);
        _spiderElements.Add(spider);

        // Local center within the container.
        double lcx = hullR;
        double lcy = hullR;

        for (int i = 0; i < members.Count; i++)
        {
            double angle = 2 * Math.PI * i / members.Count - Math.PI / 2;
            double mx = lcx + legLen * Math.Cos(angle);
            double my = lcy + legLen * Math.Sin(angle);
            var toolTip = BuildNodeToolTip(members[i].mk.Title, BuildLiveToolTipResolver(members[i].mk));

            var leg = new Line
            {
                X1 = lcx,
                Y1 = lcy,
                X2 = mx,
                Y2 = my,
                Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0xff, 0xff, 0xff)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            spider.Children.Add(leg);

            var dot = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(Color.FromRgb(0x2d, 0x8c, 0xff)),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                ToolTip = toolTip,
            };
            ToolTipService.SetInitialShowDelay(dot, 100);
            ToolTipService.SetShowDuration(dot, 60000);
            Canvas.SetLeft(dot, mx - 6);
            Canvas.SetTop(dot, my - 6);
            AttachNodeInteraction(dot, members[i].mk);
            spider.Children.Add(dot);

            var label = new Emoji.Wpf.TextBlock
            {
                Text = members[i].mk.Label,
                FontSize = 11,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)),
                Padding = new Thickness(2, 0, 2, 0),
                ToolTip = toolTip,
            };
            ToolTipService.SetInitialShowDelay(label, 100);
            ToolTipService.SetShowDuration(label, 60000);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelSize = label.DesiredSize;
            bool labelOnRight = Math.Cos(angle) >= 0;
            double labelLeft = labelOnRight ? mx + 10 : mx - 10 - labelSize.Width;
            Canvas.SetLeft(label, labelLeft);
            Canvas.SetTop(label, my - labelSize.Height / 2.0);
            spider.Children.Add(label);
        }
    }

    /// <summary>Removes the temporary spider elements added on hover.</summary>
    private void SpiderCollapse()
    {
        _activeSpiderClusterKey = null;
        SpiderCollapseImmediate();
        if (_pendingMarkerRefresh)
        {
            _pendingMarkerRefresh = false;
            OnMarkersChanged();
        }
    }

    private void SpiderCollapseImmediate()
    {
        foreach (var el in _spiderElements)
            MarkerCanvas.Children.Remove(el);
        _spiderElements.Clear();
    }

    /// <summary>Wraps the multi-line marker description in a ToolTip that stays
    /// visible while the pointer hovers the node.</summary>
    private static void UpdateToolTipContent(ToolTip toolTip, string text)
    {
        if (toolTip.Content is Emoji.Wpf.TextBlock tb &&
            !string.Equals(tb.Text, text, StringComparison.Ordinal))
            tb.Text = text;
    }

    private ToolTip BuildNodeToolTip(string text, Func<string>? liveText = null)
    {
        var toolTip = new ToolTip
        {
            Content = new Emoji.Wpf.TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
            },
            Tag = liveText,
        };

        toolTip.Opened += (_, _) =>
        {
            _openNodeToolTips++;
            if (toolTip.Tag is Func<string> resolve)
            {
                UpdateToolTipContent(toolTip, resolve());
                _liveToolTips.Add(toolTip);
                if (!_liveToolTipTimer.IsEnabled)
                    _liveToolTipTimer.Start();
            }
        };
        toolTip.Closed += (_, _) =>
        {
            _liveToolTips.Remove(toolTip);
            if (_liveToolTips.Count == 0 && _liveToolTipTimer.IsEnabled)
                _liveToolTipTimer.Stop();

            if (_openNodeToolTips > 0)
                _openNodeToolTips--;

            if (_openNodeToolTips == 0 && _activeSpiderClusterKey is null && _pendingMarkerRefresh)
            {
                _pendingMarkerRefresh = false;
                OnMarkersChanged();
            }
        };

        return toolTip;
    }

    // -- Interaction --------------------------------------------------------

    /// <summary>Attaches double-click and right-click handlers to a node dot
    /// so it behaves like a row in the node list.</summary>
    private void AttachNodeInteraction(FrameworkElement element, MainViewModel.MapMarker mk)
    {
        if (mk.NodeNum is null && mk.WaypointRowId is null) return;
        element.MouseLeftButtonDown += (_, e) =>
        {
            if (mk.NodeNum is null) return;
            if (e.ClickCount != 2) return;
            var node = _vm?.Nodes.FirstOrDefault(n => n.NodeNum == mk.NodeNum);
            if (node is not null) NodeDoubleClicked?.Invoke(node);
            e.Handled = true;
        };
        element.MouseRightButtonUp += (_, e) =>
        {
            if (mk.NodeNum is not null)
            {
                var node = _vm?.Nodes.FirstOrDefault(n => n.NodeNum == mk.NodeNum);
                if (node is not null) NodeRightClicked?.Invoke(node);
            }
            else if (mk.WaypointRowId is long waypointId)
            {
                var wp = _vm?.Waypoints.FirstOrDefault(w => w.Id == waypointId);
                if (wp is not null) WaypointRightClicked?.Invoke(wp);
            }
            e.Handled = true;
        };
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is not null && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var p = e.GetPosition(MarkerCanvas);
            double w = MarkerCanvas.ActualWidth, h = MarkerCanvas.ActualHeight;
            double originX = LonToX(_centerLon, _zoom) - w / 2.0;
            double originY = LatToY(_centerLat, _zoom) - h / 2.0;
            double lon = XToLon(originX + p.X, _zoom);
            double lat = YToLat(originY + p.Y, _zoom);
            lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

            var channel = PromptForWaypointChannel(_vm);
            if (channel is null)
            {
                e.Handled = true;
                return;
            }

            _ = _vm.SendWaypointFromMapAsync(ClampLat(lat), lon, channel);
            e.Handled = true;
            return;
        }

        bool withinSpider = IsWithinSpider(e.OriginalSource as DependencyObject);
        if (_spiderElements.Count > 0)
        {
            if (!withinSpider)
                SpiderCollapse();
            else
                return;
        }

        // Dragging breaks follow mode.
        if (_followHome)
        {
            _followHome = false;
            FollowHomeButton.IsChecked = false;
        }

        _dragging = true;
        // Measure drag in control-space (not MarkerCanvas-space) so the
        // RenderTransform preview doesn't move the coordinate frame itself.
        _lastMouse = e.GetPosition(this);
        _dragOffset = default;
        _tileDragTransform.X = 0;
        _tileDragTransform.Y = 0;
        _markerDragTransform.X = 0;
        _markerDragTransform.Y = 0;
        _lastDragPreviewTick = 0;
        _lastDragCommitTick = 0;
        MarkerCanvas.CaptureMouse();
    }

    private ChannelConfig? PromptForWaypointChannel(MainViewModel vm)
    {
        var channels = vm.Channels.ToList();
        if (channels.Count == 0)
        {
            vm.Status = "No channel to send waypoint on.";
            return null;
        }

        var selectedChannel = vm.SelectedChannel;
        int preferredIndex = selectedChannel is not null &&
            (selectedChannel.Config.Role == ChannelRole.Primary || selectedChannel.Config.Role == ChannelRole.Secondary)
            ? selectedChannel.Config.Index
            : channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary)?.Config.Index
                ?? channels[0].Config.Index;

        var picker = new ChannelPickerWindow(channels, preferredIndex,
            "Send waypoint on which channel?")
        {
            Owner = Window.GetWindow(this),
        };

        return picker.ShowDialog() == true
            ? picker.SelectedChannel?.Config
            : null;
    }

    private bool IsWithinSpider(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is UIElement element && _spiderElements.Contains(element))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        MarkerCanvas.ReleaseMouseCapture();

        bool committed = CommitDragOffsetToCenter();

        _tileDragTransform.X = 0;
        _tileDragTransform.Y = 0;
        _markerDragTransform.X = 0;
        _markerDragTransform.Y = 0;

        if (committed)
            Render();

        _dragOffset = default;
        _lastDragCommitTick = 0;

        if (_pendingNodeMarkerNums.Count > 0)
        {
            var changed = _pendingNodeMarkerNums.ToArray();
            _pendingNodeMarkerNums.Clear();
            OnNodeMarkersChangedCore(changed);
        }

        if (_pendingMarkerRefresh)
        {
            _pendingMarkerRefresh = false;
            OnMarkersChanged();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        double dx = p.X - _lastMouse.X;
        double dy = p.Y - _lastMouse.Y;
        _lastMouse = p;
        if (dx == 0 && dy == 0) return;

        // Preview drag using a cheap transform; commit center + one render on mouse-up.
        _dragOffset.X += dx;
        _dragOffset.Y += dy;
        var nowTicks = Stopwatch.GetTimestamp();
        if (nowTicks - _lastDragPreviewTick < DragPreviewMinIntervalTicks)
            return;
        _lastDragPreviewTick = nowTicks;
        _tileDragTransform.X = _dragOffset.X;
        _tileDragTransform.Y = _dragOffset.Y;
        _markerDragTransform.X = _dragOffset.X;
        _markerDragTransform.Y = _dragOffset.Y;

        // Periodically sync the logical viewport while dragging so newly
        // exposed tiles/markers can start loading, while still spending most
        // frames on the cheap transform preview path.
        if (_dragOffset.LengthSquared >= DragCommitMinPixels * DragCommitMinPixels &&
            (nowTicks - _lastDragCommitTick) >= DragCommitMinIntervalTicks)
        {
            if (CommitDragOffsetToCenter())
            {
                _tileDragTransform.X = 0;
                _tileDragTransform.Y = 0;
                _markerDragTransform.X = 0;
                _markerDragTransform.Y = 0;
                _dragOffset = default;
                _lastDragCommitTick = nowTicks;
                RequestRender(fullRender: true);
            }
        }
    }

    private bool CommitDragOffsetToCenter()
    {
        if (_dragOffset.X == 0 && _dragOffset.Y == 0)
            return false;

        double cx = LonToX(_centerLon, _zoom) - _dragOffset.X;
        double cy = LatToY(_centerLat, _zoom) - _dragOffset.Y;
        _centerLon = XToLon(cx, _zoom);
        _centerLat = ClampLat(YToLat(cy, _zoom));
        _userMovedView = true;
        return true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(MarkerCanvas);
        ZoomAt(p, e.Delta > 0 ? 1 : -1);
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var p = e.GetPosition(MarkerCanvas);
        double w = MarkerCanvas.ActualWidth, h = MarkerCanvas.ActualHeight;
        double originX = LonToX(_centerLon, _zoom) - w / 2.0;
        double originY = LatToY(_centerLat, _zoom) - h / 2.0;
        double lon = XToLon(originX + p.X, _zoom);
        double lat = YToLat(originY + p.Y, _zoom);
        // Normalize longitude to [-180, 180] in case the view wrapped.
        lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        _vm.SetHomeLocation(ClampLat(lat), lon);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) =>
        ZoomAt(new Point(MarkerCanvas.ActualWidth / 2, MarkerCanvas.ActualHeight / 2), 1);

    private void OnZoomOut(object sender, RoutedEventArgs e) =>
        ZoomAt(new Point(MarkerCanvas.ActualWidth / 2, MarkerCanvas.ActualHeight / 2), -1);

    private void OnGoHome(object sender, RoutedEventArgs e)
    {
        _followHome = false;
        FollowHomeButton.IsChecked = false;
        if (_vm?.HomeLatitude is double hlat && _vm.HomeLongitude is double hlon)
        {
            _centerLat = ClampLat(hlat);
            _centerLon = hlon;
            if (_zoom < 12) _zoom = 14;
            _userMovedView = true;
            Render();
        }
        else
        {
            // No home marker: perform a one-time fit to currently visible
            // markers without keeping auto-fit active for future updates.
            _userMovedView = FitToMarkers();
            Render();
        }
    }

    private void OnFitAll(object sender, RoutedEventArgs e)
    {
        _followHome = false;
        FollowHomeButton.IsChecked = false;
        _userMovedView = true;
        FitToMarkers();
        Render();
    }

    private void OnFollowHomeToggle(object sender, RoutedEventArgs e)
    {
        _followHome = FollowHomeButton.IsChecked == true;
        if (_followHome) OnMarkersChanged();
    }

    private void OnClusterToggle(object sender, RoutedEventArgs e)
    {
        _clusterNodes = ClusterNodesButton.IsChecked == true;
        if (!_clusterNodes)
            SpiderCollapse();
        RenderMarkersOnly();
    }

    private void ZoomAt(Point anchor, int delta)
    {
        int newZoom = Math.Clamp(_zoom + delta, MinZoom, MaxZoom);
        if (newZoom == _zoom) return;

        // Keep the geographic point under the cursor fixed across the zoom.
        double w = MarkerCanvas.ActualWidth, h = MarkerCanvas.ActualHeight;
        double originX = LonToX(_centerLon, _zoom) - w / 2.0;
        double originY = LatToY(_centerLat, _zoom) - h / 2.0;
        double anchorLon = XToLon(originX + anchor.X, _zoom);
        double anchorLat = YToLat(originY + anchor.Y, _zoom);

        _zoom = newZoom;
        InvalidateAndRefreshCoordinateCache();  // Pre-compute coords for all markers at new zoom level

        // Recompute the center so the anchor stays under the cursor.
        double ax = LonToX(anchorLon, _zoom);
        double ay = LatToY(anchorLat, _zoom);
        double cx = ax + (w / 2.0 - anchor.X);
        double cy = ay + (h / 2.0 - anchor.Y);
        _centerLon = XToLon(cx, _zoom);
        _centerLat = ClampLat(YToLat(cy, _zoom));
        _userMovedView = true;
        RequestRender(fullRender: true);
    }

    private static double ClampLat(double lat) => Math.Clamp(lat, -85.05, 85.05);

    /// <summary>Restores the map viewport (center + zoom) from persisted
    /// settings. Marks the view as user-moved so auto-center logic does not
    /// override the restored position on the first <see cref="OnMarkersChanged"/> call.</summary>
    public void LoadFromSettings(AppSettings settings)
    {
        _clusterNodes = settings.MapClusterNodes;
        ClusterNodesButton.IsChecked = _clusterNodes;

        _mapTileTheme = settings.MapTileTheme ?? "Auto";
        MapTileThemeCombo.SelectedItem = _mapTileTheme;
        AttributionText.Text = CurrentTiles.Attribution;

        if (settings.MapCenterLat is double lat && settings.MapCenterLon is double lon
            && settings.MapZoom >= MinZoom && settings.MapZoom <= MaxZoom)
        {
            _centerLat = ClampLat(lat);
            _centerLon = lon;
            _zoom = settings.MapZoom;
            _userMovedView = true;
            _hasRestoredViewport = true;
            InvalidateCoordinateCache();
            if (IsLoaded)
                Render();
        }
    }

    /// <summary>Persists the current map viewport (center + zoom) to settings.</summary>
    public void SaveToSettings(AppSettings settings)
    {
        settings.MapCenterLat = _centerLat;
        settings.MapCenterLon = _centerLon;
        settings.MapZoom = _zoom;
        settings.MapClusterNodes = _clusterNodes;
        settings.MapTileTheme = _mapTileTheme;
    }

    /// <summary>Centers the map on the given location and optionally zooms.</summary>
    public void CenterOn(double lat, double lon, int zoom = 14)
    {
        _centerLat = ClampLat(lat);
        _centerLon = lon;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _userMovedView = true;
        InvalidateCoordinateCache();
        Render();
    }

    /// <summary>Center / zoom so all markers fit, or default if there are none.</summary>
    /// <summary>Fits all visible markers into the viewport. Does not special-case home.</summary>
    private bool FitToMarkers() => FitToMarkers(_vm?.GetMapMarkers());

    private bool FitToMarkers(IReadOnlyList<MainViewModel.MapMarker>? markers)
    {
        if (markers is null || markers.Count == 0) return false;

        double oldLat = _centerLat;
        double oldLon = _centerLon;
        int oldZoom = _zoom;

        if (markers.Count == 1)
        {
            _centerLat = ClampLat(markers[0].Lat);
            _centerLon = markers[0].Lon;
            _zoom = 13;
            InvalidateCoordinateCache();
            return Math.Abs(oldLat - _centerLat) > 1e-9
                || Math.Abs(oldLon - _centerLon) > 1e-9
                || oldZoom != _zoom;
        }

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var m in markers)
        {
            minLat = Math.Min(minLat, m.Lat); maxLat = Math.Max(maxLat, m.Lat);
            minLon = Math.Min(minLon, m.Lon); maxLon = Math.Max(maxLon, m.Lon);
        }

        _centerLat = ClampLat((minLat + maxLat) / 2.0);
        _centerLon = (minLon + maxLon) / 2.0;

        double w = MarkerCanvas.ActualWidth > 0 ? MarkerCanvas.ActualWidth : 600;
        double h = MarkerCanvas.ActualHeight > 0 ? MarkerCanvas.ActualHeight : 400;

        int best = MinZoom;
        for (int z = MaxZoom; z >= MinZoom; z--)
        {
            double spanX = Math.Abs(LonToX(maxLon, z) - LonToX(minLon, z));
            double spanY = Math.Abs(LatToY(minLat, z) - LatToY(maxLat, z));
            if (spanX <= w * 0.85 && spanY <= h * 0.85) { best = z; break; }
        }
        _zoom = best;
        InvalidateCoordinateCache();

        return Math.Abs(oldLat - _centerLat) > 1e-9
            || Math.Abs(oldLon - _centerLon) > 1e-9
            || oldZoom != _zoom;
    }
}
