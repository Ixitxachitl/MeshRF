// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MeshRF.Map;

/// <summary>What a lookup came back with, and whether it actually happened.
///
/// An empty index means one of two very different things — nobody has mapped
/// any buildings here, or the service could not be reached — and a prediction
/// that quietly drops the buildings it was asked for should say which.
/// </summary>
public sealed record BuildingExtract(
    BuildingIndex Index,
    bool LookupFailed,
    BuildingLookupFailure Failure = BuildingLookupFailure.None,
    TimeSpan? RetryAfter = null)
{
    public static readonly BuildingExtract None = new(BuildingIndex.Empty, false);

    public static readonly BuildingExtract Unavailable =
        new(BuildingIndex.Empty, true, BuildingLookupFailure.Unknown);

    public static BuildingExtract Failed(BuildingLookupFailure why, TimeSpan? retryAfter = null) =>
        new(BuildingIndex.Empty, true, why, retryAfter);

    public int Count => Index.Count;

    /// <summary>Why the lookup did not happen, in words, or null when it did.
    /// </summary>
    /// <remarks>"Could not be reached" covers a service that is rate-limiting
    /// us, one that is overloaded, and a machine with no network at all. Those
    /// call for waiting, retrying later and checking the connection
    /// respectively, so collapsing them into one message tells the user to do
    /// nothing in particular.</remarks>
    public string? Explanation => Failure switch
    {
        BuildingLookupFailure.None => null,

        BuildingLookupFailure.RateLimited =>
            "OpenStreetMap is rate-limiting this machine" +
            Wait(" — try again in ", string.Empty) +
            ". Overpass is a shared free service that allows each user a few queries at a time",

        BuildingLookupFailure.ServerBusy =>
            "the OpenStreetMap building service is busy and shed the query" +
            Wait(" — try again in ", "; a smaller radius asks less of it"),

        BuildingLookupFailure.TimedOut =>
            "the OpenStreetMap building query timed out — a smaller radius asks for less",

        BuildingLookupFailure.Offline =>
            "OpenStreetMap could not be reached — check this machine's network connection",

        BuildingLookupFailure.Refused =>
            "OpenStreetMap refused the building query",

        BuildingLookupFailure.CoolingOff =>
            "not retried yet after an earlier OpenStreetMap failure" +
            Wait(" — retrying in ", string.Empty),

        _ => "OpenStreetMap could not be reached",
    };

    /// <summary>The wait in words, when the service said how long.</summary>
    private string Wait(string prefix, string otherwise) =>
        RetryAfter is { } wait && wait > TimeSpan.Zero
            ? prefix + (wait.TotalMinutes >= 1.5
                ? $"{wait.TotalMinutes:0} minutes"
                : $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} seconds")
            : otherwise;
}

/// <summary>Why a building lookup produced nothing.</summary>
public enum BuildingLookupFailure
{
    /// <summary>It succeeded, whether or not it found anything.</summary>
    None,

    /// <summary>Overpass is enforcing its per-user limit.</summary>
    RateLimited,

    /// <summary>Overpass is up but shed the query.</summary>
    ServerBusy,

    /// <summary>The query ran past the client's own patience.</summary>
    TimedOut,

    /// <summary>Nothing answered: no route, no DNS, no network.</summary>
    Offline,

    /// <summary>Answered, but refused the query itself.</summary>
    Refused,

    /// <summary>Not attempted — an earlier failure is still in its cool-off.
    /// </summary>
    CoolingOff,

    /// <summary>Failed in a way not worth its own message.</summary>
    Unknown,
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
        var now = DateTimeOffset.UtcNow;
        if (!_backoff.ShouldTry(key, now))
            return BuildingExtract.Failed(BuildingLookupFailure.CoolingOff, _backoff.RetryIn(key, now));

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

            // Inspected rather than thrown on: the status and any Retry-After
            // are the whole diagnosis, and EnsureSuccessStatusCode drops the
            // header on its way out.
            if (!response.IsSuccessStatusCode)
            {
                _backoff.Failed(key, DateTimeOffset.UtcNow);
                return BuildingExtract.Failed(Classify(response.StatusCode), RetryAfter(response));
            }

            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _backoff.Succeeded(key);
        }
        catch (HttpRequestException)
        {
            // Nothing answered at all: no route, no DNS, connection refused.
            _backoff.Failed(key, DateTimeOffset.UtcNow);
            return BuildingExtract.Failed(BuildingLookupFailure.Offline);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _backoff.Failed(key, DateTimeOffset.UtcNow);
            return BuildingExtract.Failed(BuildingLookupFailure.TimedOut);
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

    /// <summary>What an unsuccessful status says about why.</summary>
    /// <remarks>Overpass answers 429 when a user is over their slot allowance
    /// and 504 when the query was too heavy for it at that moment; 509 is the
    /// older bandwidth-limit answer some mirrors still send.</remarks>
    public static BuildingLookupFailure Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => BuildingLookupFailure.RateLimited,
        (HttpStatusCode)509 => BuildingLookupFailure.RateLimited,
        HttpStatusCode.GatewayTimeout => BuildingLookupFailure.ServerBusy,
        HttpStatusCode.ServiceUnavailable => BuildingLookupFailure.ServerBusy,
        HttpStatusCode.RequestTimeout => BuildingLookupFailure.TimedOut,
        _ => BuildingLookupFailure.Refused,
    };

    /// <summary>How long the service asked us to wait, if it said.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
