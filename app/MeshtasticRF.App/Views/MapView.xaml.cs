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
using MeshtasticRF.App.ViewModels;
using Path = System.IO.Path;
namespace MeshtasticRF.App.Views;

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
        "© OpenStreetMap contributors  ·  right-click to set home");

    private static readonly TileProvider DarkTiles = new(
        "cartodark",
        "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",
        "abcd",
        "© OpenStreetMap · © CARTO  ·  right-click to set home",
        // CARTO dark_all renders roads/labels very dark; lift them so they read
        // against the dark theme.
        Brightness: 1.7);

    private static TileProvider CurrentTiles =>
        ThemeManager.IsDark ? DarkTiles : LightTiles;

    private static readonly HttpClient s_http = CreateHttpClient();
    private static readonly string s_cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeshtasticRF", "tiles");
    private static readonly ConcurrentDictionary<string, BitmapSource> s_memCache = new();

    private MainViewModel? _vm;

    // View state: geographic center + integer zoom.
    private double _centerLat = 39.5;
    private double _centerLon = -98.35;
    private int _zoom = 4;

    private bool _dragging;
    private Point _lastMouse;
    private bool _userMovedView;

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
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeshtasticRF/1.0 (SDR receiver)");
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
        if (!_userMovedView) FitToMarkers();
        Render();
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
        var w = MapCanvas.ActualWidth;
        var h = MapCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        MapCanvas.Children.Clear();

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
        MapCanvas.Children.Add(img);

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
        foreach (var mk in _vm.GetMapMarkers())
        {
            double px = LonToX(mk.Lon, _zoom) - originX;
            double py = LatToY(mk.Lat, _zoom) - originY;

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
                MapCanvas.Children.Add(home);
            }
            else
            {
                var dot = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(Color.FromRgb(0x2d, 0x8c, 0xff)),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    ToolTip = BuildNodeToolTip(mk.Title),
                };
                ToolTipService.SetInitialShowDelay(dot, 250);
                ToolTipService.SetShowDuration(dot, 60000);
                Canvas.SetLeft(dot, px - 6);
                Canvas.SetTop(dot, py - 6);
                MapCanvas.Children.Add(dot);
            }

            var label = new TextBlock
            {
                Text = mk.Label,
                FontSize = 11,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
                Padding = new Thickness(2, 0, 2, 0),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, px + 8);
            Canvas.SetTop(label, py - 8);
            MapCanvas.Children.Add(label);
        }
    }

    /// <summary>Wraps the multi-line marker description in a ToolTip that stays
    /// visible while the pointer hovers the node.</summary>
    private static ToolTip BuildNodeToolTip(string text) => new()
    {
        Content = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontFamily = new FontFamily("Segoe UI"),
        },
    };

    // -- Interaction --------------------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _lastMouse = e.GetPosition(MapCanvas);
        MapCanvas.CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        MapCanvas.ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(MapCanvas);
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
        Render();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(MapCanvas);
        ZoomAt(p, e.Delta > 0 ? 1 : -1);
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        var p = e.GetPosition(MapCanvas);
        double w = MapCanvas.ActualWidth, h = MapCanvas.ActualHeight;
        double originX = LonToX(_centerLon, _zoom) - w / 2.0;
        double originY = LatToY(_centerLat, _zoom) - h / 2.0;
        double lon = XToLon(originX + p.X, _zoom);
        double lat = YToLat(originY + p.Y, _zoom);
        // Normalize longitude to [-180, 180] in case the view wrapped.
        lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        _vm.SetHomeLocation(ClampLat(lat), lon);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) =>
        ZoomAt(new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2), 1);

    private void OnZoomOut(object sender, RoutedEventArgs e) =>
        ZoomAt(new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2), -1);

    private void OnRecenter(object sender, RoutedEventArgs e)
    {
        _userMovedView = false;
        FitToMarkers();
        Render();
    }

    private void ZoomAt(Point anchor, int delta)
    {
        int newZoom = Math.Clamp(_zoom + delta, MinZoom, MaxZoom);
        if (newZoom == _zoom) return;

        // Keep the geographic point under the cursor fixed across the zoom.
        double w = MapCanvas.ActualWidth, h = MapCanvas.ActualHeight;
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

    /// <summary>Center / zoom so all markers fit, or default if there are none.</summary>
    private void FitToMarkers()
    {
        var markers = _vm?.GetMapMarkers();
        if (markers is null || markers.Count == 0) return;

        // If a home location is set, anchor the initial view on it, zoomed in.
        var home = markers.FirstOrDefault(m => m.IsHome);
        if (home is not null)
        {
            _centerLat = ClampLat(home.Lat);
            _centerLon = home.Lon;
            _zoom = 12;
            return;
        }

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

        double w = MapCanvas.ActualWidth > 0 ? MapCanvas.ActualWidth : 600;
        double h = MapCanvas.ActualHeight > 0 ? MapCanvas.ActualHeight : 400;

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
