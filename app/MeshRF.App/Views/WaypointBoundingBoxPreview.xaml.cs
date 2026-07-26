// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MeshRF.App;
using Path = System.IO.Path;

namespace MeshRF.App.Views;

/// <summary>
/// Small, self-contained slippy-map preview used by <see cref="WaypointEditWindow"/>
/// to show a waypoint's location and let the user redraw its rectangular
/// geofence by clicking two opposite corners. Deliberately independent of
/// <c>MapView</c> (own tile fetch/cache, own pan/zoom) so a bug here can't
/// affect the main map, but follows the same map tile theme (<see
/// cref="ThemeManager.MapTileTheme"/>, including "Auto" following the app
/// theme) and shares the same on-disk tile cache directory, tile providers,
/// and dark-tile gamma correction as <c>MapView</c>/<c>LocationHistoryWindow</c>.
/// </summary>
public partial class WaypointBoundingBoxPreview : UserControl
{
    private const int TileSize = 256;
    private const int MinZoom = 3;
    private const int MaxZoom = 19;

    private readonly record struct TileProvider(
        string Id, string UrlTemplate, string Subdomains,
        double Brightness = 1.0, double Gamma = 1.0);

    private static readonly TileProvider LightTiles = new(
        "osm",
        "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
        "abc");

    private static readonly TileProvider LightCartoTiles = new(
        "cartopositron",
        "https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png",
        "abcd");

    private static readonly TileProvider VoyagerTiles = new(
        "cartovoyager",
        "https://{s}.basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png",
        "abcd");

    private static readonly TileProvider DarkTiles = new(
        "cartodark",
        "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",
        "abcd",
        // Gamma correction lifts the low-contrast CARTO dark palette: roads and
        // labels become clearly readable while the dark background is preserved.
        Gamma: 1.8);

    private string _mapTileTheme = "Auto";

    private TileProvider CurrentTiles => _mapTileTheme switch
    {
        "Light"         => LightTiles,
        "Light (CARTO)" => LightCartoTiles,
        "Voyager"       => VoyagerTiles,
        "Dark"          => DarkTiles,
        _               => ThemeManager.IsDark ? DarkTiles : LightTiles, // "Auto"
    };

    private static readonly HttpClient s_http = CreateHttpClient();
    private static readonly string s_cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeshRF", "tiles");
    private static readonly ConcurrentDictionary<string, BitmapSource> s_memCache = new();

    private double _centerLat;
    private double _centerLon;
    private int _zoom = 15;

    private double _markerLat;
    private double _markerLon;

    private double? _bboxWest, _bboxSouth, _bboxEast, _bboxNorth;
    private (double Lat, double Lon)? _pendingCorner;

    private bool _dragging;
    private Point _lastMouse;

    public double? BboxWest => _bboxWest;
    public double? BboxSouth => _bboxSouth;
    public double? BboxEast => _bboxEast;
    public double? BboxNorth => _bboxNorth;

    /// <summary>Raised whenever the box is redrawn or cleared (not on pan/zoom).</summary>
    public event EventHandler? BoundingBoxChanged;

    public WaypointBoundingBoxPreview()
    {
        InitializeComponent();
        Directory.CreateDirectory(s_cacheDir);
        _mapTileTheme = ThemeManager.MapTileTheme;
        ThemeManager.ThemeChanged += OnThemeChanged;
        ThemeManager.MapTileThemeChanged += OnMapTileThemeChanged;
        Loaded += (_, _) => Render();
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.MapTileThemeChanged -= OnMapTileThemeChanged;
        };
        SizeChanged += (_, _) => Render();
    }

    private void OnThemeChanged() => Render();

    private void OnMapTileThemeChanged()
    {
        _mapTileTheme = ThemeManager.MapTileTheme;
        Render();
    }

    /// <summary>Sets the fixed waypoint marker position and the initial
    /// bounding box (any/all null = none), centering/zooming the preview to
    /// show the box if present, otherwise the marker itself.</summary>
    public void Initialize(double markerLat, double markerLon,
                           double? west, double? south, double? east, double? north)
    {
        _markerLat = markerLat;
        _markerLon = markerLon;
        _bboxWest = west; _bboxSouth = south; _bboxEast = east; _bboxNorth = north;

        if (west is double w && south is double s && east is double e && north is double n)
        {
            _centerLat = ClampLat((s + n) / 2.0);
            _centerLon = (w + e) / 2.0;
            FitToBounds(w, s, e, n);
        }
        else
        {
            _centerLat = ClampLat(markerLat);
            _centerLon = markerLon;
            _zoom = 15;
        }

        if (IsLoaded) Render();
    }

    private void FitToBounds(double west, double south, double east, double north)
    {
        double w = ActualWidth > 0 ? ActualWidth : 320;
        double h = ActualHeight > 0 ? ActualHeight : 220;
        int best = MinZoom;
        for (int z = MaxZoom; z >= MinZoom; z--)
        {
            double spanX = Math.Abs(LonToX(east, z) - LonToX(west, z));
            double spanY = Math.Abs(LatToY(south, z) - LatToY(north, z));
            if (spanX <= w * 0.7 && spanY <= h * 0.7) { best = z; break; }
        }
        _zoom = Math.Max(MinZoom, best - 1); // step out one level so the box isn't edge-to-edge
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (RedrawButton.IsChecked == true)
        {
            var p = e.GetPosition(OverlayCanvas);
            double w = OverlayCanvas.ActualWidth, h = OverlayCanvas.ActualHeight;
            double originX = LonToX(_centerLon, _zoom) - w / 2.0;
            double originY = LatToY(_centerLat, _zoom) - h / 2.0;
            double lon = XToLon(originX + p.X, _zoom);
            double lat = ClampLat(YToLat(originY + p.Y, _zoom));
            lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

            if (_pendingCorner is (double cLat, double cLon))
            {
                _pendingCorner = null;
                _bboxWest = Math.Min(cLon, lon);
                _bboxEast = Math.Max(cLon, lon);
                _bboxSouth = Math.Min(cLat, lat);
                _bboxNorth = Math.Max(cLat, lat);
                RedrawButton.IsChecked = false;
                BoundingBoxChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _pendingCorner = (lat, lon);
            }
            Render();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _lastMouse = e.GetPosition(this);
        OverlayCanvas.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        var dx = p.X - _lastMouse.X;
        var dy = p.Y - _lastMouse.Y;
        _lastMouse = p;

        double cx = LonToX(_centerLon, _zoom) - dx;
        double cy = LatToY(_centerLat, _zoom) - dy;
        _centerLon = XToLon(cx, _zoom);
        _centerLat = ClampLat(YToLat(cy, _zoom));
        Render();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        OverlayCanvas.ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 1 : -1), MinZoom, MaxZoom);
        Render();
        e.Handled = true;
    }

    private void OnRedrawToggled(object sender, RoutedEventArgs e) => _pendingCorner = null;

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _bboxWest = _bboxSouth = _bboxEast = _bboxNorth = null;
        _pendingCorner = null;
        RedrawButton.IsChecked = false;
        Render();
        BoundingBoxChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Render()
    {
        if (!IsLoaded) return;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        TileCanvas.Children.Clear();
        OverlayCanvas.Children.Clear();

        double originX = LonToX(_centerLon, _zoom) - w / 2.0;
        double originY = LatToY(_centerLat, _zoom) - h / 2.0;

        int firstTileX = (int)Math.Floor(originX / TileSize);
        int firstTileY = (int)Math.Floor(originY / TileSize);
        int tilesX = (int)Math.Ceiling(w / TileSize) + 2;
        int tilesY = (int)Math.Ceiling(h / TileSize) + 2;
        int maxTile = 1 << _zoom;

        for (int ty = 0; ty < tilesY; ty++)
        {
            int tileY = firstTileY + ty;
            if (tileY < 0 || tileY >= maxTile) continue;
            for (int tx = 0; tx < tilesX; tx++)
            {
                int tileX = ((firstTileX + tx) % maxTile + maxTile) % maxTile;
                double left = (firstTileX + tx) * TileSize - originX;
                double top = tileY * TileSize - originY;
                PlaceTile(tileX, tileY, _zoom, left, top);
            }
        }

        double markerX = LonToX(_markerLon, _zoom) - originX;
        double markerY = LatToY(_markerLat, _zoom) - originY;
        var marker = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brushes.OrangeRed,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(marker, markerX - 5);
        Canvas.SetTop(marker, markerY - 5);
        Panel.SetZIndex(marker, 20);
        OverlayCanvas.Children.Add(marker);

        if (_bboxWest is double bw && _bboxSouth is double bs && _bboxEast is double be && _bboxNorth is double bn)
        {
            double x1 = LonToX(bw, _zoom) - originX;
            double x2 = LonToX(be, _zoom) - originX;
            double y1 = LatToY(bn, _zoom) - originY; // north = smaller Y
            double y2 = LatToY(bs, _zoom) - originY; // south = larger Y

            var rect = new Rectangle
            {
                Width = Math.Max(1, x2 - x1),
                Height = Math.Max(1, y2 - y1),
                Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x2e, 0x7d, 0x32)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x2e, 0x7d, 0x32)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, y1);
            Panel.SetZIndex(rect, 5);
            OverlayCanvas.Children.Add(rect);
        }

        if (_pendingCorner is (double pcLat, double pcLon))
        {
            double px = LonToX(pcLon, _zoom) - originX;
            double py = LatToY(pcLat, _zoom) - originY;
            var corner = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.Transparent,
                Stroke = Brushes.Orange,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(corner, px - 5);
            Canvas.SetTop(corner, py - 5);
            Panel.SetZIndex(corner, 25);
            OverlayCanvas.Children.Add(corner);
        }
    }

    private void PlaceTile(int x, int y, int zoom, double left, double top)
    {
        var img = new Image { Width = TileSize, Height = TileSize, SnapsToDevicePixels = true };
        Canvas.SetLeft(img, left);
        Canvas.SetTop(img, top);
        TileCanvas.Children.Add(img);

        var provider = CurrentTiles;
        var key = $"{provider.Id}/{zoom}/{x}/{y}";
        img.Tag = key;
        if (s_memCache.TryGetValue(key, out var cached))
        {
            img.Source = cached;
            return;
        }
        _ = LoadTileAsync(key, provider, x, y, zoom, img);
    }

    private static async Task LoadTileAsync(string key, TileProvider provider, int x, int y, int zoom, Image target)
    {
        try
        {
            var bmp = await GetTileBitmapAsync(key, provider, x, y, zoom);
            if (bmp is null) return;
            s_memCache[key] = bmp;
            // The tile may have started loading under a different theme; only
            // apply it if the image still wants this exact tile.
            if (Equals(target.Tag, key))
                target.Source = bmp;
        }
        catch { /* tile fetch failed; leave blank */ }
    }

    private static async Task<BitmapSource?> GetTileBitmapAsync(string key, TileProvider provider, int x, int y, int zoom)
    {
        var file = Path.Combine(s_cacheDir, $"{provider.Id}_{zoom}_{x}_{y}.png");
        byte[] bytes;
        if (File.Exists(file))
        {
            bytes = await File.ReadAllBytesAsync(file);
        }
        else
        {
            var server = provider.Subdomains[(x + y) % provider.Subdomains.Length];
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

        return (provider.Brightness == 1.0 && provider.Gamma == 1.0)
            ? bmp
            : PostProcessTile(bmp, provider.Brightness, provider.Gamma);
    }

    private static BitmapSource PostProcessTile(BitmapSource src, double brightness, double gamma)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[h * stride];
        bgra.CopyPixels(pixels, stride, 0);

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
            pixels[i]     = lut[pixels[i]];
            pixels[i + 1] = lut[pixels[i + 1]];
            pixels[i + 2] = lut[pixels[i + 2]];
        }

        var wb = new WriteableBitmap(w, h, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
        wb.Freeze();
        return wb;
    }

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRF/1.0 (SDR receiver)");
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    private static double ClampLat(double lat) => Math.Clamp(lat, -85.05112878, 85.05112878);

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
}
