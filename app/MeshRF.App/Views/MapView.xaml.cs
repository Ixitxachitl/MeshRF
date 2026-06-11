// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MeshRF.App;
using MeshRF.App.ViewModels;
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

    // Tile providers. The dark basemap (CARTO dark_all) is used while the app
    // is in a dark theme so the map blends in instead of glowing white; the
    // standard OSM raster is used in light mode. Both are free, key-less, and
    // attributed to OpenStreetMap.
    private readonly record struct TileProvider(
        string Id, string UrlTemplate, string Subdomains, string Attribution,
        double Brightness = 1.0);

    private static readonly TileProvider LightTiles = new(
        "osm",
        "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
        "abc",
        "© OpenStreetMap contributors  ·  Ctrl+left-click send waypoint  ·  Ctrl+right-click set home");

    private static readonly TileProvider DarkTiles = new(
        "cartodark",
        "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",
        "abcd",
        "© OpenStreetMap · © CARTO  ·  Ctrl+left-click send waypoint  ·  Ctrl+right-click set home",
        // CARTO dark_all renders roads/labels very dark; lift them so they read
        // against the dark theme.
        Brightness: 1.7);

    private static TileProvider CurrentTiles =>
        ThemeManager.IsDark ? DarkTiles : LightTiles;

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
    private long _lastDragRenderTick;
    private bool _userMovedView;

    // Temporary elements added when a cluster of stacked nodes is "spidered"
    // out on hover; removed on collapse or on the next full Render.
    private readonly List<UIElement> _spiderElements = new();
    private string? _activeSpiderClusterKey;
    private bool _pendingMarkerRefresh;
    private int _openNodeToolTips;
    private bool _followHome;

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
        Directory.CreateDirectory(s_cacheDir);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Render();
        DataContextChanged += OnDataContextChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        AttributionText.Text = CurrentTiles.Attribution;
    }

    private void OnThemeChanged()
    {
        // Tile provider follows the theme; drop the on-screen tiles and redraw
        // with the new basemap. Disk/mem caches are keyed by provider id, so
        // they don't collide.
        AttributionText.Text = CurrentTiles.Attribution;
        Render();
    }

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient();
        // OSM tile usage policy requires a descriptive User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRF/1.0 (SDR receiver)");
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as MainViewModel;
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
        FitToMarkers();
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        if (_vm is null) return;
        _vm.MapDataChanged -= OnMapDataChanged; // avoid double subscription
        _vm.MapDataChanged += OnMapDataChanged;
    }

    private void Unsubscribe()
    {
        if (_vm is not null) _vm.MapDataChanged -= OnMapDataChanged;
    }

    private void OnMapDataChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) OnMarkersChanged();
        else Dispatcher.BeginInvoke(new Action(OnMarkersChanged));
    }

    private void OnMarkersChanged()
    {
        if (_activeSpiderClusterKey is not null || _openNodeToolTips > 0)
        {
            _pendingMarkerRefresh = true;
            return;
        }

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
            // Auto-center: prefer home if available, otherwise fit all.
            var markers = _vm?.GetMapMarkers();
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
                FitToMarkers(markers);
                viewportChanged = true;
            }
        }

        if (viewportChanged)
            Render();
        else
            RenderMarkersOnly();
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

    // -- Rendering ----------------------------------------------------------

    private void Render()
    {
        var w = MarkerCanvas.ActualWidth;
        var h = MarkerCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        TileCanvas.Children.Clear();
        MarkerCanvas.Children.Clear();

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
    }

    /// <summary>Redraws only the marker layer without touching the tile layer.
    /// Called when node data changes but the viewport (center/zoom) is unchanged
    /// so the tile canvas is left in place and no blink occurs.</summary>
    private void RenderMarkersOnly()
    {
        var w = MarkerCanvas.ActualWidth;
        var h = MarkerCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double originX = LonToX(_centerLon, _zoom) - w / 2.0;
        double originY = LatToY(_centerLat, _zoom) - h / 2.0;
        MarkerCanvas.Children.Clear();
        DrawMarkers(originX, originY);
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

        if (provider.Brightness == 1.0) return bmp;
        return Brighten(bmp, provider.Brightness);
    }

    /// <summary>Returns a copy of <paramref name="src"/> with each RGB channel
    /// scaled by <paramref name="factor"/> (clamped to 255). Used to lift the
    /// very dark roads/labels of the dark basemap so they remain readable.</summary>
    private static BitmapSource Brighten(BitmapSource src, double factor)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[h * stride];
        bgra.CopyPixels(pixels, stride, 0);

        // Precompute the channel lookup table.
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Min(255.0, i * factor);

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
        SpiderCollapseImmediate();
        bool restoredActiveSpider = false;
        double viewportW = MarkerCanvas.ActualWidth;
        double viewportH = MarkerCanvas.ActualHeight;
        // Keep a small margin so edge markers/labels do not pop in late.
        const double cullMarginPx = 48;

        DrawLocationHistory(originX, originY, viewportW, viewportH);

        // Node markers are collected and clustered; the home marker is drawn
        // immediately since it never stacks with nodes.
        var nodes = new List<(MainViewModel.MapMarker mk, double px, double py)>();

        foreach (var mk in _vm.GetMapMarkers())
        {
            double px = LonToX(mk.Lon, _zoom) - originX;
            double py = LatToY(mk.Lat, _zoom) - originY;
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
                    ToolTip = "Home",
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
                nodes.Add((mk, px, py));
            }
        }

        // Group node markers that land on (nearly) the same screen pixel so
        // stacked nodes don't hide each other's dot and tooltip.
        const double clusterRadiusPx = 14;
        var clusters = new List<List<(MainViewModel.MapMarker mk, double px, double py)>>();
        foreach (var nm in nodes)
        {
            List<(MainViewModel.MapMarker mk, double px, double py)>? hit = null;
            foreach (var c in clusters)
            {
                double dx = nm.px - c[0].px;
                double dy = nm.py - c[0].py;
                if (dx * dx + dy * dy <= clusterRadiusPx * clusterRadiusPx) { hit = c; break; }
            }
            if (hit is null) { hit = new(); clusters.Add(hit); }
            hit.Add(nm);
        }

        foreach (var c in clusters)
        {
            if (c.Count == 1)
            {
                AddNodeDot(c[0].mk, c[0].px, c[0].py);
                AddNodeLabel(c[0].mk.Label, c[0].px, c[0].py);
            }
            else
            {
                AddCluster(c);
                if (string.Equals(_activeSpiderClusterKey, GetClusterKey(c), StringComparison.Ordinal))
                {
                    SpiderExpand(c, c.Average(m => m.px), c.Average(m => m.py), persistSelection: false);
                    restoredActiveSpider = true;
                }
            }
        }

        if (!restoredActiveSpider)
            _activeSpiderClusterKey = null;
    }

    private void DrawLocationHistory(double originX, double originY, double viewportW, double viewportH)
    {
        if (_vm is null) return;

        const double cullMarginPx = 96;
        foreach (var polyline in _vm.GetMapPolylines())
        {
            if (polyline.Points.Count < 2)
                continue;

            Point? prev = null;
            bool anyVisible = false;
            var segment = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x8C, 0x2D)),
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
                            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x8C, 0x2D)),
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
        var fill = mk.IsWaypoint
            ? new SolidColorBrush(mk.IsExpired
                ? Color.FromRgb(0xc6, 0x28, 0x28)
                : Color.FromRgb(0x2e, 0x7d, 0x32))
            : new SolidColorBrush(Color.FromRgb(0x2d, 0x8c, 0xff));
        var dot = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = fill,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            ToolTip = BuildNodeToolTip(mk.Title),
        };
        ToolTipService.SetInitialShowDelay(dot, 250);
        ToolTipService.SetShowDuration(dot, 60000);
        Canvas.SetLeft(dot, px - 6);
        Canvas.SetTop(dot, py - 6);
        AttachNodeInteraction(dot, mk);
        MarkerCanvas.Children.Add(dot);
    }

    /// <summary>Draws a small caption to the right of a marker.</summary>
    private void AddNodeLabel(string text, double px, double py)
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
        Canvas.SetLeft(label, px + 8);
        Canvas.SetTop(label, py - 8);
        MarkerCanvas.Children.Add(label);
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
            Fill = new SolidColorBrush(Color.FromRgb(0xff, 0x8c, 0x2d)),
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
        Panel.SetZIndex(badge, 10);

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

    /// <summary>Fans a stacked group of nodes out around their shared point and
    /// keeps them open via a transparent hover hull.</summary>
    private void SpiderExpand(
        List<(MainViewModel.MapMarker mk, double px, double py)> members, double cx, double cy,
        bool persistSelection = true)
    {
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
            var toolTip = BuildNodeToolTip(members[i].mk.Title);

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
    private ToolTip BuildNodeToolTip(string text)
    {
        var toolTip = new ToolTip
        {
            Content = new Emoji.Wpf.TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
            },
        };

        toolTip.Opened += (_, _) => _openNodeToolTips++;
        toolTip.Closed += (_, _) =>
        {
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

            _ = _vm.SendWaypointFromMapAsync(ClampLat(lat), lon);
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
        _lastDragRenderTick = 0;
        _lastMouse = e.GetPosition(MarkerCanvas);
        MarkerCanvas.CaptureMouse();
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
        Render();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(MarkerCanvas);
        double dx = p.X - _lastMouse.X;
        double dy = p.Y - _lastMouse.Y;
        _lastMouse = p;
        if (dx == 0 && dy == 0) return;

        // Shift the center by the dragged pixel delta.
        double cx = LonToX(_centerLon, _zoom) - dx;
        double cy = LatToY(_centerLat, _zoom) - dy;
        _centerLon = XToLon(cx, _zoom);
        _centerLat = ClampLat(YToLat(cy, _zoom));
        _userMovedView = true;

        // Throttle full renders while dragging to keep interaction responsive.
        long nowTick = Environment.TickCount64;
        if (nowTick - _lastDragRenderTick >= 16)
        {
            _lastDragRenderTick = nowTick;
            Render();
        }
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
            _userMovedView = false;
            FitToMarkers();
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

        // Recompute the center so the anchor stays under the cursor.
        double ax = LonToX(anchorLon, _zoom);
        double ay = LatToY(anchorLat, _zoom);
        double cx = ax + (w / 2.0 - anchor.X);
        double cy = ay + (h / 2.0 - anchor.Y);
        _centerLon = XToLon(cx, _zoom);
        _centerLat = ClampLat(YToLat(cy, _zoom));
        _userMovedView = true;
        Render();
    }

    private static double ClampLat(double lat) => Math.Clamp(lat, -85.05, 85.05);

    /// <summary>Restores the map viewport (center + zoom) from persisted
    /// settings. Marks the view as user-moved so auto-center logic does not
    /// override the restored position on the first <see cref="OnMarkersChanged"/> call.</summary>
    public void LoadFromSettings(AppSettings settings)
    {
        if (settings.MapCenterLat is double lat && settings.MapCenterLon is double lon
            && settings.MapZoom >= MinZoom && settings.MapZoom <= MaxZoom)
        {
            _centerLat = ClampLat(lat);
            _centerLon = lon;
            _zoom = settings.MapZoom;
            _userMovedView = true;
        }
    }

    /// <summary>Persists the current map viewport (center + zoom) to settings.</summary>
    public void SaveToSettings(AppSettings settings)
    {
        settings.MapCenterLat = _centerLat;
        settings.MapCenterLon = _centerLon;
        settings.MapZoom = _zoom;
    }

    /// <summary>Center / zoom so all markers fit, or default if there are none.</summary>
    /// <summary>Fits all visible markers into the viewport. Does not special-case home.</summary>
    private void FitToMarkers() => FitToMarkers(_vm?.GetMapMarkers());

    private void FitToMarkers(IReadOnlyList<MainViewModel.MapMarker>? markers)
    {
        if (markers is null || markers.Count == 0) return;

        if (markers.Count == 1)
        {
            _centerLat = ClampLat(markers[0].Lat);
            _centerLon = markers[0].Lon;
            _zoom = 13;
            return;
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
    }
}
