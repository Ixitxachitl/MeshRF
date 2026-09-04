// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http;

namespace MeshRF.Map;

/// <summary>Ground elevations sampled along a path, with what it took to get
/// them. <paramref name="Complete"/> is false when a tile could not be fetched
/// and the gap it left was bridged from its neighbours, which makes the profile
/// indicative rather than measured across that stretch.</summary>
public sealed record TerrainPath(
    IReadOnlyList<(double DistanceM, double GroundM)> Ground,
    int Zoom,
    int TileCount,
    bool Complete);

/// <summary>
/// Fetches Terrarium elevation tiles and samples ground height along a path.
///
/// The tiles are an ordinary Web-Mercator pyramid served from AWS Open Data,
/// keyless and free, so this needs no account and no API key — the same terms
/// the basemap tiles come on. They are also immutable, which is why the disk
/// cache has no expiry: a tile once fetched is correct forever, and a link
/// profile re-run over the same ground costs nothing.
///
/// The zoom is chosen from the length of the path rather than fixed. A short
/// link is read at a deep zoom where the pixels are metres across; a long one
/// steps back so that reading it costs a few dozen tiles instead of hundreds.
/// </summary>
public sealed class TerrainTiles : IDisposable
{
    private const string UrlTemplate =
        "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";

    public const string Attribution =
        "Elevation: Terrarium tiles · Mapzen / AWS Open Data (SRTM, NED and other public sources)";

    /// <summary>Points sampled along the path. Fixed rather than scaled with
    /// distance: it is what the chart can draw, and the zoom is picked to match
    /// it so the samples land roughly one per terrain pixel either way.</summary>
    public const int PathSamples = 512;

    /// <summary>How many tiles one profile may pull. A path long enough to
    /// exceed this is read at a shallower zoom instead — coarser terrain is a
    /// far better trade than a few hundred requests.</summary>
    private const int MaxTilesPerPath = 48;

    private static readonly TimeSpan CacheStaleAfter = TimeSpan.FromDays(1);
    private const long CacheMaxBytes = 256L * 1024 * 1024;
    private const long CacheTargetBytes = 192L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly FetchBackoff _backoff = new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
    private readonly SemaphoreSlim _fetchGate = new(6, 6);
    private int _writesSinceTrim;

    public TerrainTiles(string? cacheDirectory = null, HttpClient? http = null)
    {
        _cacheDir = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeshRF", "terrain");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRF/1.0 (+https://github.com/meshrf)");
    }

    /// <summary>Samples the ground between two points, fetching whatever tiles
    /// the line crosses. Returns null when the path is degenerate or no tile
    /// along it could be read at all.</summary>
    public async Task<TerrainPath?> SampleAsync(GeoPoint a, GeoPoint b, CancellationToken ct = default)
    {
        double distance = Geodesy.DistanceM(a, b);
        if (distance <= 0 || double.IsNaN(distance)) return null;

        var path = new GeoPoint[PathSamples];
        for (int i = 0; i < PathSamples; i++)
            path[i] = Geodesy.Interpolate(a, b, i / (double)(PathSamples - 1));

        double midLat = (a.Lat + b.Lat) / 2;
        int zoom = TerrainGrid.ZoomForSpacing(distance / (PathSamples - 1), midLat);

        // Step back until the line fits the tile budget. Each step down halves
        // the resolution and roughly halves the tiles crossed.
        HashSet<(int X, int Y)> needed;
        while (true)
        {
            needed = TilesAlong(path, zoom);
            if (needed.Count <= MaxTilesPerPath || zoom <= 7) break;
            zoom--;
        }

        var tiles = await FetchAsync(needed, zoom, ct).ConfigureAwait(false);
        if (tiles.Count == 0) return null;

        var grid = new TerrainGrid(zoom, tiles);
        var ground = new (double DistanceM, double GroundM)[PathSamples];
        bool anyHole = false;

        for (int i = 0; i < PathSamples; i++)
        {
            double d = distance * i / (PathSamples - 1);
            if (grid.ElevationAt(path[i].Lat, path[i].Lon) is double e)
            {
                ground[i] = (d, e);
            }
            else
            {
                ground[i] = (d, double.NaN);
                anyHole = true;
            }
        }

        if (anyHole && !FillHoles(ground)) return null;

        return new TerrainPath(ground, zoom, tiles.Count, !anyHole);
    }

    /// <summary>The distinct tiles a sampled line passes through.</summary>
    private static HashSet<(int X, int Y)> TilesAlong(IReadOnlyList<GeoPoint> path, int zoom)
    {
        int span = 1 << zoom;
        var tiles = new HashSet<(int X, int Y)>();
        foreach (var p in path)
        {
            // The neighbours too: sampling is bilinear, so a point near a tile
            // edge reads a pixel from the tile next door.
            var (x, y) = TerrainGrid.TileFor(p.Lat, p.Lon, zoom);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= span) continue;
                tiles.Add(((((x + dx) % span) + span) % span, ny));
            }
        }
        return tiles;
    }

    private async Task<Dictionary<(int X, int Y), float[]>> FetchAsync(
        IEnumerable<(int X, int Y)> wanted, int zoom, CancellationToken ct)
    {
        var results = new Dictionary<(int X, int Y), float[]>();
        var gate = new object();

        var work = wanted.Select(async key =>
        {
            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await LoadTileAsync(zoom, key.X, key.Y, ct).ConfigureAwait(false) is { } tile)
                    lock (gate) results[key] = tile;
            }
            finally { _fetchGate.Release(); }
        });

        await Task.WhenAll(work).ConfigureAwait(false);
        TrimCacheIfDue();
        return results;
    }

    private async Task<float[]?> LoadTileAsync(int zoom, int x, int y, CancellationToken ct)
    {
        var file = Path.Combine(_cacheDir, $"terrarium_{zoom}_{x}_{y}.png");
        byte[] bytes;

        if (File.Exists(file))
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                TileDiskCache.MarkUsed(file, CacheStaleAfter);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        else
        {
            var key = $"{zoom}/{x}/{y}";
            if (!_backoff.ShouldTry(key, DateTimeOffset.UtcNow)) return null;

            var url = UrlTemplate
                .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
            try
            {
                bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                _backoff.Succeeded(key);
            }
            catch (HttpRequestException)
            {
                _backoff.Failed(key, DateTimeOffset.UtcNow);
                return null;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _backoff.Failed(key, DateTimeOffset.UtcNow);
                return null;
            }

            try
            {
                Directory.CreateDirectory(_cacheDir);
                await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _writesSinceTrim);
            }
            catch (IOException) { /* the cache is an optimisation */ }
            catch (UnauthorizedAccessException) { /* the cache is an optimisation */ }
        }

        try
        {
            return TerrainGrid.DecodeTerrarium(PngImage.Decode(bytes));
        }
        catch (InvalidDataException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>Bridges samples whose tile was missing, so one unreachable tile
    /// leaves a straight stretch rather than discarding the whole profile.
    /// Says whether anything was left to interpolate from.</summary>
    private static bool FillHoles((double DistanceM, double GroundM)[] ground)
    {
        int first = Array.FindIndex(ground, g => !double.IsNaN(g.GroundM));
        if (first < 0) return false;
        int last = Array.FindLastIndex(ground, g => !double.IsNaN(g.GroundM));

        for (int i = 0; i < first; i++) ground[i].GroundM = ground[first].GroundM;
        for (int i = last + 1; i < ground.Length; i++) ground[i].GroundM = ground[last].GroundM;

        for (int i = first + 1; i < last; i++)
        {
            if (!double.IsNaN(ground[i].GroundM)) continue;

            int gapEnd = i;
            while (double.IsNaN(ground[gapEnd].GroundM)) gapEnd++;

            double from = ground[i - 1].GroundM, to = ground[gapEnd].GroundM;
            for (int j = i; j < gapEnd; j++)
                ground[j].GroundM = from + (to - from) * (j - i + 1) / (double)(gapEnd - i + 1);

            i = gapEnd;
        }
        return true;
    }

    /// <summary>Keeps the cache directory bounded, checked every so many writes
    /// rather than on each one: the sweep stats every file in the directory,
    /// which is far more work than the tile that triggered it.</summary>
    private void TrimCacheIfDue()
    {
        if (Interlocked.Exchange(ref _writesSinceTrim, 0) < 200) return;
        TileDiskCache.Trim(_cacheDir, CacheMaxBytes, CacheTargetBytes);
    }

    public void Dispose()
    {
        _http.Dispose();
        _fetchGate.Dispose();
    }
}
