// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MeshRF.Map;

/// <summary>What a lookup came back with, and whether it actually happened.
///
/// An empty index means one of two very different things — nobody has mapped
/// any buildings here, or the service could not be reached — and a prediction
/// that quietly drops the buildings it was asked for should say which.
/// </summary>
public sealed record BuildingExtract(BuildingIndex Index, bool LookupFailed)
{
    public static readonly BuildingExtract None = new(BuildingIndex.Empty, false);

    public static readonly BuildingExtract Unavailable = new(BuildingIndex.Empty, true);

    public int Count => Index.Count;
}

/// <summary>
/// Fetches building footprints from OpenStreetMap through Overpass.
///
/// MeshLab RF reads Overture's building theme and falls back to Overpass;
/// Overture means cloud-hosted parquet, which is a large dependency for the
/// same polygons OSM already has. Overpass is a public, shared, rate-limited
/// service, so this asks for one bounding box at a time, caches what comes
/// back, and refuses to ask for an area big enough to be rude.
/// </summary>
public sealed class OverpassBuildings : IDisposable
{
    private const string Endpoint = "https://overpass-api.de/api/interpreter";

    public const string Attribution = "Buildings: © OpenStreetMap contributors (via Overpass)";

    /// <summary>Largest square this will ask for, in metres of half-width. A
    /// city's buildings are tens of thousands of polygons and the service is
    /// shared; past this the answer is to zoom in rather than to wait.</summary>
    public const double MaxRadiusM = 6_000;

    /// <summary>How long a cached extract is trusted. Buildings change slowly,
    /// and a week is what MeshLab RF settled on for the same data.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly FetchBackoff _backoff = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));

    public OverpassBuildings(string? cacheDirectory = null, HttpClient? http = null)
    {
        _cacheDir = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeshRF", "buildings");

        // Overpass can take a while to answer a large box, and answering slowly
        // is normal rather than a fault.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRF/1.0 (+https://github.com/meshrf)");
    }

    /// <summary>
    /// Footprints within a radius of a point, from the cache when it is fresh
    /// and from Overpass otherwise. Returns an empty index rather than throwing
    /// when the service is unreachable: a prediction without buildings is worse
    /// but still a prediction, and the caller is told which it got.
    /// </summary>
    public async Task<BuildingExtract> AroundAsync(
        GeoPoint centre, double radiusM, CancellationToken ct = default)
    {
        if (radiusM <= 0) return BuildingExtract.None;
        radiusM = Math.Min(radiusM, MaxRadiusM);

        double dLat = radiusM / 111_320.0;
        double dLon = radiusM / (111_320.0 * Math.Max(0.01, Math.Cos(centre.Lat * Math.PI / 180)));

        double south = centre.Lat - dLat, north = centre.Lat + dLat;
        double west = centre.Lon - dLon, east = centre.Lon + dLon;

        var file = Path.Combine(_cacheDir, CacheName(south, west, north, east));
        if (Fresh(file) && ReadCache(file) is { } cached) return new BuildingExtract(cached, false);

        // Still inside a backoff from an earlier refusal, which is a failure
        // that has not stopped being one just because it is not being retried.
        var key = file;
        if (!_backoff.ShouldTry(key, DateTimeOffset.UtcNow)) return BuildingExtract.Unavailable;

        string json;
        try
        {
            // Ways and relations both, since anything larger than a house is
            // usually a multipolygon. "geom" asks Overpass to inline the
            // coordinates so there is no second round trip for node ids.
            var query =
                $"[out:json][timeout:60];(way[\"building\"]({south:F6},{west:F6},{north:F6},{east:F6});" +
                $"relation[\"building\"]({south:F6},{west:F6},{north:F6},{east:F6}););out geom;";

            using var body = new StringContent(query);
            using var response = await _http.PostAsync(Endpoint, body, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _backoff.Succeeded(key);
        }
        catch (HttpRequestException)
        {
            _backoff.Failed(key, DateTimeOffset.UtcNow);
            return BuildingExtract.Unavailable;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _backoff.Failed(key, DateTimeOffset.UtcNow);
            return BuildingExtract.Unavailable;
        }

        try
        {
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllTextAsync(file, json, ct).ConfigureAwait(false);
        }
        catch (IOException) { /* the cache is an optimisation */ }
        catch (UnauthorizedAccessException) { /* the cache is an optimisation */ }

        return new BuildingExtract(new BuildingIndex(Parse(json)), false);
    }

    /// <summary>
    /// Reads Overpass's JSON. Anything without enough geometry to be a ring is
    /// skipped: an extract of a whole town always contains a few, and one bad
    /// polygon is no reason to lose the rest.
    /// </summary>
    public static IReadOnlyList<Footprint> Parse(string json)
    {
        var footprints = new List<Footprint>();

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("elements", out var elements)) return footprints;
        if (elements.ValueKind != JsonValueKind.Array) return footprints;

        foreach (var element in elements.EnumerateArray())
        {
            // A way carries its ring directly; a relation carries members, of
            // which the outers are the building and the inners are courtyards.
            if (element.TryGetProperty("geometry", out var geometry))
            {
                if (Ring(geometry) is { } ring) footprints.Add(ring);
                continue;
            }

            if (!element.TryGetProperty("members", out var members)) continue;
            if (members.ValueKind != JsonValueKind.Array) continue;

            foreach (var member in members.EnumerateArray())
            {
                if (!member.TryGetProperty("role", out var role)) continue;
                if (role.GetString() != "outer") continue;
                if (!member.TryGetProperty("geometry", out var memberGeometry)) continue;

                if (Ring(memberGeometry) is { } ring) footprints.Add(ring);
            }
        }

        return footprints;
    }

    private static Footprint? Ring(JsonElement geometry)
    {
        if (geometry.ValueKind != JsonValueKind.Array) return null;

        var points = new List<GeoPoint>();
        foreach (var node in geometry.EnumerateArray())
        {
            if (!node.TryGetProperty("lat", out var lat) || !node.TryGetProperty("lon", out var lon))
                continue;
            if (!lat.TryGetDouble(out double latitude) || !lon.TryGetDouble(out double longitude))
                continue;

            points.Add(new GeoPoint(latitude, longitude));
        }

        // Overpass closes its ways by repeating the first node; the crossing
        // test wraps on its own, so the duplicate is dropped.
        if (points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);

        return points.Count >= 3 ? new Footprint(points) : null;
    }

    private bool Fresh(string file)
    {
        try
        {
            return File.Exists(file)
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < CacheLifetime;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static BuildingIndex? ReadCache(string file)
    {
        try { return new BuildingIndex(Parse(File.ReadAllText(file))); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Cache file per bounding box, rounded so that nudging the map a
    /// few metres reuses the extract rather than fetching a new one.</summary>
    private static string CacheName(double south, double west, double north, double east) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "osm_{0:F3}_{1:F3}_{2:F3}_{3:F3}.json", south, west, north, east);

    public void Dispose() => _http.Dispose();
}
