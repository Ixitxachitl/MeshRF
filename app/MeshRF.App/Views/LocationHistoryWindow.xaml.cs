// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows;
using MeshRF.App.ViewModels;
using Path = System.IO.Path;

namespace MeshRF.App.Views;

public partial class LocationHistoryWindow : Window
{
    private const int TileSize = 256;
    private const int MinZoom = 2;
    private const int MaxZoom = 18;
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
        Gamma: 1.8);

    private string _mapTileTheme = "Auto";

    private TileProvider CurrentTiles => _mapTileTheme switch
    {
        "Light"         => LightTiles,
        "Light (CARTO)" => LightCartoTiles,
        "Voyager"       => VoyagerTiles,
        "Dark"          => DarkTiles,
        _               => ThemeManager.IsDark ? DarkTiles : LightTiles,
    };

    private static readonly HttpClient s_http = CreateHttpClient();
    private static readonly string s_cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeshRF", "tiles");
    private static readonly Dictionary<string, BitmapSource> s_memCache = new();

    private ConversationViewModel? _conversation;
    private bool _hasView;
    private bool _followLatest;
    private bool _dragging;
    private Point _lastMouse;
    private double _centerLat;
    private double _centerLon;
    private int _zoom = 14;

    public LocationHistoryWindow(ConversationViewModel conversation)
    {
        InitializeComponent();
        Directory.CreateDirectory(s_cacheDir);
        _mapTileTheme = ThemeManager.MapTileTheme;
        ApplySavedLayout();
        _conversation = conversation;
        DataContext = conversation;
        Title = $"Location History - {conversation.TabHeader}";
        conversation.LocationHistory.CollectionChanged += LocationHistory_CollectionChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        ThemeManager.MapTileThemeChanged += OnMapTileThemeChanged;
        Closed += (_, _) =>
        {
            SaveLayout();
            conversation.LocationHistory.CollectionChanged -= LocationHistory_CollectionChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.MapTileThemeChanged -= OnMapTileThemeChanged;
        };
        FitMapToHistory();
    }

    private void LocationHistory_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnLocationHistoryChanged();

    private void MiniMapCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawMiniMap();

    private void ApplySavedLayout()
    {
        var settings = AppSettings.Load();
        if (settings.LocationHistoryWindowWidth is double width && width >= MinWidth)
            Width = width;
        if (settings.LocationHistoryWindowHeight is double height && height >= MinHeight)
            Height = height;
        if (settings.LocationHistoryLeftPaneWidth is double leftWidth && leftWidth >= MapPaneColumn.MinWidth)
            MapPaneColumn.Width = new GridLength(leftWidth, GridUnitType.Pixel);
    }

    private void SaveLayout()
    {
        var settings = AppSettings.Load();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.LocationHistoryWindowWidth = Math.Max(MinWidth, bounds.Width);
        settings.LocationHistoryWindowHeight = Math.Max(MinHeight, bounds.Height);
        settings.LocationHistoryLeftPaneWidth = Math.Max(MapPaneColumn.MinWidth, MapPaneColumn.ActualWidth);
        settings.Save();
    }

    private void FitMap_Click(object sender, RoutedEventArgs e)
    {
        _followLatest = false;
        UpdateFollowButton();
        FitMapToHistory();
    }

    private void FollowLatest_Click(object sender, RoutedEventArgs e)
    {
        _followLatest = FollowLatestButton.IsChecked == true;
        UpdateFollowButton();
        if (_followLatest)
            CenterOnLatest(keepZoom: _hasView);
        DrawMiniMap();
    }

    private void MiniMapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _followLatest = false;
        UpdateFollowButton();
        _lastMouse = e.GetPosition(MiniMapCanvas);
        MiniMapCanvas.CaptureMouse();
        MiniMapCanvas.Focus();
    }

    private void MiniMapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !_hasView || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(MiniMapCanvas);
        var delta = current - _lastMouse;
        _lastMouse = current;
        double centerX = LonToX(_centerLon, _zoom) - delta.X;
        double centerY = LatToY(_centerLat, _zoom) - delta.Y;
        _centerLon = XToLon(centerX, _zoom);
        _centerLat = YToLat(centerY, _zoom);
        DrawMiniMap();
    }

    private void MiniMapCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        MiniMapCanvas.ReleaseMouseCapture();
    }

    private void MiniMapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_hasView)
            return;

        _followLatest = false;
        UpdateFollowButton();
        int newZoom = Math.Clamp(_zoom + (e.Delta > 0 ? 1 : -1), MinZoom, MaxZoom);
        if (newZoom == _zoom)
            return;

        var mouse = e.GetPosition(MiniMapCanvas);
        double width = Math.Max(1.0, MiniMapCanvas.ActualWidth);
        double height = Math.Max(1.0, MiniMapCanvas.ActualHeight);
        double originX = LonToX(_centerLon, _zoom) - width / 2.0;
        double originY = LatToY(_centerLat, _zoom) - height / 2.0;
        double mouseLon = XToLon(originX + mouse.X, _zoom);
        double mouseLat = YToLat(originY + mouse.Y, _zoom);

        _zoom = newZoom;
        double newMouseX = LonToX(mouseLon, _zoom);
        double newMouseY = LatToY(mouseLat, _zoom);
        _centerLon = XToLon(newMouseX - mouse.X + width / 2.0, _zoom);
        _centerLat = YToLat(newMouseY - mouse.Y + height / 2.0, _zoom);
        DrawMiniMap();
    }

    private void OnThemeChanged() => DrawMiniMap();

    private void OnMapTileThemeChanged()
    {
        _mapTileTheme = ThemeManager.MapTileTheme;
        DrawMiniMap();
    }

    private void OnLocationHistoryChanged()
    {
        if (!_hasView)
            FitMapToHistory();
        else if (_followLatest)
            CenterOnLatest(keepZoom: true);
        DrawMiniMap();
    }

    private void FitMapToHistory()
    {
        if (_conversation is null || MiniMapCanvas is null)
            return;

        var samples = _conversation.LocationHistory.ToList();
        if (samples.Count == 0)
        {
            _hasView = false;
            DrawMiniMap();
            return;
        }

        double width = Math.Max(1.0, MiniMapCanvas.ActualWidth);
        double height = Math.Max(1.0, MiniMapCanvas.ActualHeight);
        var viewport = CalculateViewport(samples, width, height);
        _centerLat = viewport.CenterLat;
        _centerLon = viewport.CenterLon;
        _zoom = viewport.Zoom;
        _hasView = true;
        DrawMiniMap();
    }

    private void CenterOnLatest(bool keepZoom)
    {
        if (_conversation is null)
            return;

        var latest = _conversation.LocationHistory.LastOrDefault();
        if (latest is null)
            return;

        _centerLat = latest.Latitude;
        _centerLon = latest.Longitude;
        if (!keepZoom)
            _zoom = 15;
        _hasView = true;
    }

    private void UpdateFollowButton()
    {
        if (FollowLatestButton is not null)
            FollowLatestButton.IsChecked = _followLatest;
    }

    private void DrawMiniMap()
    {
        if (_conversation is null || MiniMapCanvas is null)
            return;

        MiniMapCanvas.Children.Clear();
        var samples = _conversation.LocationHistory.ToList();
        double width = MiniMapCanvas.ActualWidth;
        double height = MiniMapCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        if (samples.Count == 0 || !_hasView)
            return;

        DrawTiles(_centerLat, _centerLon, _zoom, width, height);

        double originX = LonToX(_centerLon, _zoom) - width / 2.0;
        double originY = LatToY(_centerLat, _zoom) - height / 2.0;
        Point Project(LocationHistoryPoint point) => new(
            LonToX(point.Longitude, _zoom) - originX,
            LatToY(point.Latitude, _zoom) - originY);

        if (samples.Count > 1)
        {
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(30, 144, 255)),
                StrokeThickness = 2.0,
            };
            foreach (var sample in samples)
                polyline.Points.Add(Project(sample));
            MiniMapCanvas.Children.Add(polyline);
        }

        DrawPoint(Project(samples[0]), Color.FromRgb(46, 204, 113));
        DrawPoint(Project(samples[^1]), Color.FromRgb(255, 193, 7));
    }

    private void DrawTiles(double centerLat, double centerLon, int zoom, double width, double height)
    {
        double centerX = LonToX(centerLon, zoom);
        double centerY = LatToY(centerLat, zoom);
        double originX = centerX - width / 2.0;
        double originY = centerY - height / 2.0;
        int firstTileX = (int)Math.Floor(originX / TileSize);
        int firstTileY = (int)Math.Floor(originY / TileSize);
        int tilesX = (int)Math.Ceiling(width / TileSize) + 2;
        int tilesY = (int)Math.Ceiling(height / TileSize) + 2;
        int tileLimit = 1 << zoom;

        for (int tx = firstTileX; tx < firstTileX + tilesX; tx++)
        {
            int wrappedX = ((tx % tileLimit) + tileLimit) % tileLimit;
            for (int ty = firstTileY; ty < firstTileY + tilesY; ty++)
            {
                if (ty < 0 || ty >= tileLimit) continue;
                double left = tx * TileSize - originX;
                double top = ty * TileSize - originY;
                PlaceTile(wrappedX, ty, zoom, left, top);
            }
        }
    }

    private void PlaceTile(int x, int y, int zoom, double left, double top)
    {
        var image = new Image
        {
            Width = TileSize,
            Height = TileSize,
            Stretch = Stretch.Fill,
        };
        Canvas.SetLeft(image, left);
        Canvas.SetTop(image, top);
        MiniMapCanvas.Children.Add(image);

        var provider = CurrentTiles;
        string key = $"{provider.Id}/{zoom}/{x}/{y}";
        image.Tag = key;
        if (s_memCache.TryGetValue(key, out var cached))
        {
            image.Source = cached;
            return;
        }

        _ = LoadTileAsync(key, provider, x, y, zoom, image);
    }

    private static async Task LoadTileAsync(string key, TileProvider provider, int x, int y, int zoom, Image target)
    {
        try
        {
            var bitmap = await GetTileBitmapAsync(key, provider, x, y, zoom);
            if (bitmap is not null && Equals(target.Tag, key))
                target.Source = bitmap;
        }
        catch { }
    }

    private static async Task<BitmapSource?> GetTileBitmapAsync(string key, TileProvider provider, int x, int y, int zoom)
    {
        string file = Path.Combine(s_cacheDir, $"{provider.Id}_{zoom}_{x}_{y}.png");
        if (!File.Exists(file))
        {
            char subdomain = provider.Subdomains[(x + y) % provider.Subdomains.Length];
            string url = provider.UrlTemplate
                .Replace("{s}", subdomain.ToString())
                .Replace("{z}", zoom.ToString())
                .Replace("{x}", x.ToString())
                .Replace("{y}", y.ToString());
            var bytes = await s_http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(file, bytes);
        }

        await using var stream = File.OpenRead(file);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        BitmapSource result = (provider.Brightness == 1.0 && provider.Gamma == 1.0)
            ? bitmap
            : PostProcessTile(bitmap, provider.Brightness, provider.Gamma);
        s_memCache[key] = result;
        return result;
    }

    private static BitmapSource PostProcessTile(BitmapSource src, double brightness, double gamma)
    {
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
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

        var wb = new System.Windows.Media.Imaging.WriteableBitmap(
            w, h, bgra.DpiX, bgra.DpiY, System.Windows.Media.PixelFormats.Bgra32, null);
        wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, stride, 0);
        wb.Freeze();
        return wb;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRF/1.0 location-history-popup");
        return client;
    }

    private static (double CenterLat, double CenterLon, int Zoom) CalculateViewport(
        IReadOnlyList<LocationHistoryPoint> samples,
        double width,
        double height)
    {
        double minLat = samples.Min(p => p.Latitude);
        double maxLat = samples.Max(p => p.Latitude);
        double minLon = samples.Min(p => p.Longitude);
        double maxLon = samples.Max(p => p.Longitude);
        double centerLat = (minLat + maxLat) / 2.0;
        double centerLon = (minLon + maxLon) / 2.0;
        const double pad = 28.0;
        double usableW = Math.Max(32.0, width - pad * 2.0);
        double usableH = Math.Max(32.0, height - pad * 2.0);

        for (int zoom = MaxZoom; zoom >= MinZoom; zoom--)
        {
            double xSpan = Math.Abs(LonToX(maxLon, zoom) - LonToX(minLon, zoom));
            double ySpan = Math.Abs(LatToY(maxLat, zoom) - LatToY(minLat, zoom));
            if (xSpan <= usableW && ySpan <= usableH)
                return (centerLat, centerLon, zoom);
        }

        return (centerLat, centerLon, MinZoom);
    }

    private static double LonToX(double lon, int zoom) =>
        (lon + 180.0) / 360.0 * (1 << zoom) * TileSize;

    private static double XToLon(double x, int zoom) =>
        x / ((1 << zoom) * TileSize) * 360.0 - 180.0;

    private static double LatToY(double lat, int zoom)
    {
        double clamped = Math.Clamp(lat, -85.05112878, 85.05112878);
        double sin = Math.Sin(clamped * Math.PI / 180.0);
        return (0.5 - Math.Log((1.0 + sin) / (1.0 - sin)) / (4.0 * Math.PI))
            * (1 << zoom) * TileSize;
    }

    private static double YToLat(double y, int zoom)
    {
        double n = 1 << zoom;
        double t = Math.PI * (1.0 - 2.0 * y / (n * TileSize));
        return Math.Atan(Math.Sinh(t)) * 180.0 / Math.PI;
    }

    private void DrawPoint(Point center, Color color)
    {
        const double size = 7.0;
        var dot = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(color),
            Stroke = Brushes.Black,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(dot, center.X - size / 2.0);
        Canvas.SetTop(dot, center.Y - size / 2.0);
        MiniMapCanvas.Children.Add(dot);
    }
}