// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// A self-contained OpenStreetMap "slippy map". Standard 256px Web-Mercator
/// tiles are fetched over HTTP and cached on disk, so the control needs no
/// browser runtime — only a connection for first-time tile loads. Plots the
/// home location, every node that reports a position, and every waypoint with
/// its circular and/or rectangular geofence.
///
/// Ported from MeshRF.App's MapView, but drawn immediate-mode into a single
/// <see cref="Render(DrawingContext)"/> rather than WPF's two Canvases of
/// retained Image/Ellipse/TextBlock visuals. That drops the whole apparatus
/// the WPF version needs to stay fast — per-node visual diffing, a spatial
/// bucket index, BitmapCache, render-throttle timers and a background
/// coordinate cache — because redrawing a few hundred markers into a
/// DrawingContext costs less than reconciling a visual tree. What it costs
/// instead is hit-testing: markers are not visuals, so pointer hits are
/// resolved against <see cref="_hitTargets"/>, rebuilt each render.
/// </summary>
public sealed class MapCanvas : Control
{
    private const int TileSize = 256;
    private const int MinZoom = 2;
    private const int MaxZoom = 19;

    // -- Tile providers -----------------------------------------------------

    private readonly record struct TileProvider(
        string Id, string UrlTemplate, string Subdomains, string Attribution,
        double Brightness = 1.0, double Gamma = 1.0);

    private const string GestureHint =
        "  ·  Ctrl+left-click send waypoint  ·  Ctrl+right-click set location";

    private static readonly TileProvider LightTiles = new(
        "osm", "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", "abc",
        "© OpenStreetMap contributors" + GestureHint);

    private static readonly TileProvider LightCartoTiles = new(
        "cartopositron", "https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png", "abcd",
        "© OpenStreetMap · © CARTO" + GestureHint);

    private static readonly TileProvider VoyagerTiles = new(
        "cartovoyager", "https://{s}.basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png", "abcd",
        "© OpenStreetMap · © CARTO" + GestureHint);

    private static readonly TileProvider DarkTiles = new(
        "cartodark", "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png", "abcd",
        "© OpenStreetMap · © CARTO" + GestureHint,
        // Gamma lifts the low-contrast CARTO dark palette so roads and labels
        // read clearly while the dark background survives.
        Gamma: 1.8);

    public static readonly IReadOnlyList<string> MapTileThemeOptions =
        ["Auto", "Light", "Light (CARTO)", "Voyager", "Dark"];

    private string _mapTileTheme = "Auto";

    private TileProvider CurrentTiles => _mapTileTheme switch
    {
        "Light" => LightTiles,
        "Light (CARTO)" => LightCartoTiles,
        "Voyager" => VoyagerTiles,
        "Dark" => DarkTiles,
        _ => DarkTiles, // "Auto" — this app's shell is dark-themed.
    };

    public string Attribution => CurrentTiles.Attribution;

    /// <summary>Raised when the tile provider changes, so the host can refresh
    /// its attribution line.</summary>
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

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
    private readonly record struct HitTarget(
        double X, double Y, double Radius, string Tooltip,
        List<(RadioViewModel.MapMarker mk, double px, double py)>? Cluster);

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
    }

    public void Attach(RadioViewModel vm)
    {
        if (_vm is not null) _vm.MapDataChanged -= OnMapDataChanged;
        _vm = vm;
        _vm.MapDataChanged += OnMapDataChanged;
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
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
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

        _hitTargets.Clear();
        if (_vm is null) return;

        DrawPendingBoundingBox(context, originX, originY);
        DrawMarkers(context, originX, originY, w, h);
        DrawSpider(context);
    }

    private void DrawTiles(DrawingContext context, double originX, double originY, double w, double h)
    {
        var provider = CurrentTiles;
        int n = 1 << _zoom;
        int firstTileX = (int)Math.Floor(originX / TileSize);
        int firstTileY = (int)Math.Floor(originY / TileSize);
        int lastTileX = (int)Math.Floor((originX + w) / TileSize);
        int lastTileY = (int)Math.Floor((originY + h) / TileSize);

        for (int ty = firstTileY; ty <= lastTileY; ty++)
        {
            if (ty < 0 || ty >= n) continue; // No wrapping vertically.
            for (int tx = firstTileX; tx <= lastTileX; tx++)
            {
                // Wrap horizontally so panning past the antimeridian works.
                int wrappedX = ((tx % n) + n) % n;
                double left = tx * (double)TileSize - originX;
                double top = ty * (double)TileSize - originY;

                var key = $"{provider.Id}/{_zoom}/{wrappedX}/{ty}";
                if (s_memCache.TryGetValue(key, out var bmp))
                {
                    context.DrawImage(bmp, new Rect(left, top, TileSize, TileSize));
                }
                else
                {
                    RequestTile(key, provider, wrappedX, ty, _zoom);
                }
            }
        }
    }

    private void RequestTile(string key, TileProvider provider, int x, int y, int zoom)
    {
        if (!_tilesInFlight.Add(key)) return;
        _ = LoadTileAsync(key, provider, x, y, zoom);
    }

    private async Task LoadTileAsync(string key, TileProvider provider, int x, int y, int zoom)
    {
        try
        {
            var bmp = await Task.Run(() => GetTileBitmapAsync(provider, x, y, zoom)).ConfigureAwait(true);
            if (bmp is null) return;
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
        catch { /* tile fetch failed; leave the background showing */ }
        finally { _tilesInFlight.Remove(key); }
    }

    private static async Task<Bitmap?> GetTileBitmapAsync(TileProvider provider, int x, int y, int zoom)
    {
        var file = System.IO.Path.Combine(s_cacheDir, $"{provider.Id}_{zoom}_{x}_{y}.png");
        byte[] bytes;
        if (System.IO.File.Exists(file))
        {
            bytes = await System.IO.File.ReadAllBytesAsync(file).ConfigureAwait(false);
        }
        else
        {
            // Rotate across the provider's tile subdomains.
            var subs = provider.Subdomains;
            var server = subs[(x + y) % subs.Length];
            var url = provider.UrlTemplate
                .Replace("{s}", server.ToString())
                .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
            bytes = await s_http.GetByteArrayAsync(url).ConfigureAwait(false);
            try { await System.IO.File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false); }
            catch { /* cache best-effort */ }
        }

        using var ms = new System.IO.MemoryStream(bytes);
        var bmp = new Bitmap(ms);
        if (provider.Brightness == 1.0 && provider.Gamma == 1.0) return bmp;

        using (bmp) return PostProcessTile(bmp, provider.Brightness, provider.Gamma);
    }

    /// <summary>Gamma-corrects then brightness-scales each RGB channel. Gamma
    /// &gt; 1 raises midtones so the CARTO dark basemap's roads and labels read
    /// clearly without blowing out bright areas.</summary>
    private static unsafe Bitmap PostProcessTile(Bitmap src, double brightness, double gamma)
    {
        var size = src.PixelSize;
        var target = new WriteableBitmap(size, src.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);

        var lut = new byte[256];
        double gammaInv = (gamma > 0.0 && gamma != 1.0) ? 1.0 / gamma : 1.0;
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            if (gammaInv != 1.0) v = Math.Pow(v, gammaInv);
            lut[i] = (byte)Math.Min(255.0, v * brightness * 255.0);
        }

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
                    px[0] = lut[px[0]]; // B
                    px[1] = lut[px[1]]; // G
                    px[2] = lut[px[2]]; // R
                    // alpha (px[3]) untouched
                }
            }
        }
        return target;
    }

    // -- Markers ------------------------------------------------------------

    private void DrawMarkers(DrawingContext context, double originX, double originY, double w, double h)
    {
        if (_vm is null) return;
        var markers = Markers;

        // A small margin keeps edge markers and their labels from popping in late.
        const double cullMargin = 48;
        var nodes = new List<(RadioViewModel.MapMarker mk, double px, double py)>();

        foreach (var mk in markers)
        {
            double px = LonToX(mk.Lon, _zoom) - originX;
            double py = LatToY(mk.Lat, _zoom) - originY;
            if (px < -cullMargin || px > w + cullMargin || py < -cullMargin || py > h + cullMargin)
                continue;

            if (mk.IsHome)
            {
                DrawHome(context, mk, px, py);
            }
            else if (mk.IsWaypoint)
            {
                if (mk.GeofenceRadiusM > 0) DrawGeofenceCircle(context, mk, px, py);
                if (mk.BboxWest is double bw && mk.BboxSouth is double bs &&
                    mk.BboxEast is double be && mk.BboxNorth is double bn)
                    DrawGeofenceRectangle(context, bw, bs, be, bn, originX, originY);
                DrawDot(context, mk.IsExpired ? WaypointExpiredFill : WaypointFill, px, py);
                DrawLabel(context, mk.Label, px, py);
                _hitTargets.Add(new HitTarget(px, py, MarkerRadiusPx + 2, mk.Title, null));
            }
            else
            {
                nodes.Add((mk, px, py));
            }
        }

        if (!_clusterNodes)
        {
            foreach (var n in nodes)
            {
                DrawDot(context, NodeFill, n.px, n.py);
                DrawLabel(context, n.mk.Label, n.px, n.py);
                _hitTargets.Add(new HitTarget(n.px, n.py, MarkerRadiusPx + 2, n.mk.Title, null));
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
                DrawDot(context, NodeFill, px, py);
                DrawLabel(context, mk.Label, px, py);
                _hitTargets.Add(new HitTarget(px, py, MarkerRadiusPx + 2, mk.Title, null));
            }
            else
            {
                DrawCluster(context, cluster);
            }
        }
    }

    private static void DrawDot(DrawingContext context, IBrush fill, double px, double py) =>
        context.DrawEllipse(fill, MarkerOutline, new Point(px, py), MarkerRadiusPx, MarkerRadiusPx);

    private void DrawHome(DrawingContext context, RadioViewModel.MapMarker mk, double px, double py)
    {
        var glyph = new FormattedText("⌂", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                      LabelTypeface, 20, Brushes.Gold);
        context.DrawText(glyph, new Point(px - glyph.Width / 2, py - glyph.Height / 2));
        DrawLabel(context, mk.Label, px, py);
        _hitTargets.Add(new HitTarget(px, py, 10, mk.Title, null));
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
                             List<(RadioViewModel.MapMarker mk, double px, double py)> members)
    {
        double cx = members.Average(m => m.px);
        double cy = members.Average(m => m.py);
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
    /// first existing cluster whose anchor is within the radius, checking only
    /// the 9 neighbouring buckets rather than every cluster.</summary>
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

            for (int bx = bucketX - 1; bx <= bucketX + 1 && hit < 0; bx++)
            {
                for (int by = bucketY - 1; by <= bucketY + 1 && hit < 0; by++)
                {
                    if (!buckets.TryGetValue(BucketKey(bx, by), out var candidates)) continue;
                    foreach (var ci in candidates)
                    {
                        double dx = node.px - anchors[ci].Px;
                        double dy = node.py - anchors[ci].Py;
                        if (dx * dx + dy * dy <= radiusSq) { hit = ci; break; }
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

            _hitTargets.Add(new HitTarget(mx, my, MarkerRadiusPx + 2, members[i].mk.Title, null));
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
            // Ctrl+right-click drops the home location here.
            if (_vm is not null && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var (lat, lon) = ScreenToGeo(p);
                _vm.SetHomeLocation(lat, lon);
                e.Handled = true;
            }
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

        // A click on a cluster badge fans it out instead of starting a drag.
        var hit = HitTest(p);
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

    /// <summary>Follow mode turned itself off (the user panned).</summary>
    public event Action<bool>? FollowHomeChanged;

    // -- Viewport commands --------------------------------------------------

    private void ZoomAt(Point anchor, int delta)
    {
        int newZoom = Math.Clamp(_zoom + delta, MinZoom, MaxZoom);
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
        _mapTileTheme = theme;
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
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
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
        _mapTileTheme = settings.MapTileTheme ?? "Auto";
        AttributionChanged?.Invoke();

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
        settings.MapTileTheme = _mapTileTheme;
    }

    public bool ClusterNodes => _clusterNodes;
    public string TileTheme => _mapTileTheme;
}
