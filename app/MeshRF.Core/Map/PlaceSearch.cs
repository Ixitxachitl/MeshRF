// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MeshRF.Map;

/// <summary>Somewhere the map can be sent to.</summary>
public sealed record Place(string Name, GeoPoint At);

/// <summary>
/// Looks a place up by name through OpenStreetMap's Nominatim.
///
/// Nominatim asks for an identifying user agent, no more than one request a
/// second, and that results are not hammered for bulk work — all of which suits
/// a search box a person types into, and none of which would suit anything
/// automatic. So this is only ever driven by a keystroke, and it holds a
/// minimum gap between calls itself rather than trusting the caller to.
/// </summary>
public sealed class PlaceSearch : IDisposable
{
    private const string Endpoint = "https://nominatim.openstreetmap.org/search";

    /// <summary>The gap Nominatim's usage policy asks for.</summary>
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(1);

    public const string Attribution = "Search: OpenStreetMap Nominatim";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    public PlaceSearch(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpIdentity.UserAgent);
    }

    /// <summary>
    /// Finds places matching a query, best first. An empty result and a failed
    /// lookup are the same thing to a search box, so a refusal or a dropped
    /// connection comes back empty rather than throwing.
    /// </summary>
    public async Task<IReadOnlyList<Place>> FindAsync(
        string query, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var since = DateTimeOffset.UtcNow - _lastCall;
            if (since < MinimumGap) await Task.Delay(MinimumGap - since, ct).ConfigureAwait(false);
            _lastCall = DateTimeOffset.UtcNow;

            var url = $"{Endpoint}?format=jsonv2&limit={limit}&q={Uri.EscapeDataString(query.Trim())}";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch (HttpRequestException) { return []; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return []; }
        catch (JsonException) { return []; }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads Nominatim's jsonv2 array. Anything missing a name or a
    /// usable coordinate is skipped rather than failing the batch.</summary>
    public static IReadOnlyList<Place> Parse(string json)
    {
        var places = new List<Place>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return places;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("display_name", out var name)) continue;
            if (!item.TryGetProperty("lat", out var lat) || !item.TryGetProperty("lon", out var lon)) continue;

            // Coordinates arrive as strings, and the service is invariant
            // whatever the machine's locale is.
            if (!double.TryParse(lat.GetString(), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double latitude)) continue;
            if (!double.TryParse(lon.GetString(), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double longitude)) continue;

            if (name.GetString() is not { Length: > 0 } label) continue;

            places.Add(new Place(label, new GeoPoint(latitude, longitude)));
        }

        return places;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
