// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MeshRF.Telemetry;

/// <summary>Current weather at a point, as reported by Open-Meteo.</summary>
public sealed record WeatherSnapshot(
    float TemperatureC,
    float RelativeHumidityPct,
    float BarometricPressureHpa,
    DateTime FetchedUtc,
    string Source);

/// <summary>Current particulate readings at a point. Both values are µg/m³ and
/// either may be missing, but never both (the fetch fails instead).</summary>
public sealed record AirQualitySnapshot(
    uint? Pm25Standard,
    uint? Pm100Standard,
    DateTime FetchedUtc,
    string Source);

/// <summary>
/// Fetches the current weather and air quality used to fill environment and
/// air-quality telemetry payloads, from Open-Meteo's key-less public APIs.
///
/// Results are cached per instance with a TTL, and concurrent callers for the
/// same kind are serialised, so a burst of auto-report ticks makes one request
/// rather than several. Status strings are surfaced through the events so a
/// view model can show what the last fetch did without owning the HTTP.
/// </summary>
public sealed class OpenMeteoClient : IDisposable
{
    private static readonly TimeSpan WeatherCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AirQualityCacheTtl = TimeSpan.FromMinutes(20);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _weatherGate = new(1, 1);
    private readonly SemaphoreSlim _airQualityGate = new(1, 1);

    private WeatherSnapshot? _weather;
    private AirQualitySnapshot? _airQuality;

    /// <summary>Raised with a human-readable description of the latest weather
    /// fetch (fetching / OK with values / the failure).</summary>
    public event Action<string>? WeatherStatusChanged;

    /// <summary>Raised with a human-readable description of the latest air
    /// quality fetch.</summary>
    public event Action<string>? AirQualityStatusChanged;

    public async Task<WeatherSnapshot?> GetWeatherAsync(
        double latitude, double longitude, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _weather is { } cached &&
            DateTime.UtcNow - cached.FetchedUtc <= WeatherCacheTtl)
            return cached;

        await _weatherGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate: whoever we queued behind may have just
            // refreshed it.
            if (!forceRefresh && _weather is { } fresh &&
                DateTime.UtcNow - fresh.FetchedUtc <= WeatherCacheTtl)
                return fresh;

            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={Coord(latitude)}&longitude={Coord(longitude)}" +
                      "&current=temperature_2m,relative_humidity_2m,surface_pressure";

            WeatherStatusChanged?.Invoke("Weather telemetry: fetching...");

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                WeatherStatusChanged?.Invoke($"Weather telemetry: fetch failed ({(int)response.StatusCode})");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!json.RootElement.TryGetProperty("current", out var current))
            {
                WeatherStatusChanged?.Invoke("Weather telemetry: missing current values.");
                return null;
            }

            if (!TryReadFloat(current, "temperature_2m", out var temperatureC) ||
                !TryReadFloat(current, "relative_humidity_2m", out var humidityPct) ||
                !TryReadFloat(current, "surface_pressure", out var pressureHpa))
            {
                WeatherStatusChanged?.Invoke("Weather telemetry: weather fields unavailable.");
                return null;
            }

            var snapshot = new WeatherSnapshot(temperatureC, humidityPct, pressureHpa,
                                               DateTime.UtcNow, "Open-Meteo");
            _weather = snapshot;
            WeatherStatusChanged?.Invoke(
                $"Weather telemetry: OK {snapshot.FetchedUtc.ToLocalTime():h:mm:ss tt} " +
                $"({snapshot.TemperatureC:F1} C, {snapshot.RelativeHumidityPct:F0}% RH, {snapshot.BarometricPressureHpa:F1} hPa)");
            return snapshot;
        }
        catch (Exception ex)
        {
            WeatherStatusChanged?.Invoke($"Weather telemetry: fetch failed ({ex.Message})");
            return null;
        }
        finally { _weatherGate.Release(); }
    }

    public async Task<AirQualitySnapshot?> GetAirQualityAsync(
        double latitude, double longitude, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _airQuality is { } cached &&
            DateTime.UtcNow - cached.FetchedUtc <= AirQualityCacheTtl)
            return cached;

        await _airQualityGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _airQuality is { } fresh &&
                DateTime.UtcNow - fresh.FetchedUtc <= AirQualityCacheTtl)
                return fresh;

            var url = "https://air-quality-api.open-meteo.com/v1/air-quality" +
                      $"?latitude={Coord(latitude)}&longitude={Coord(longitude)}" +
                      "&current=pm2_5,pm10";

            AirQualityStatusChanged?.Invoke("Air quality telemetry: fetching...");

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AirQualityStatusChanged?.Invoke($"Air quality telemetry: fetch failed ({(int)response.StatusCode})");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!json.RootElement.TryGetProperty("current", out var current))
            {
                AirQualityStatusChanged?.Invoke("Air quality telemetry: missing current values.");
                return null;
            }

            // pm2_5 -> Pm25Standard (proto field 2), pm10 -> Pm100Standard (field 3).
            uint? pm25 = TryReadFloat(current, "pm2_5", out var pm25f)
                ? (uint)Math.Round(Math.Max(0, pm25f)) : null;
            uint? pm10 = TryReadFloat(current, "pm10", out var pm10f)
                ? (uint)Math.Round(Math.Max(0, pm10f)) : null;

            if (pm25 is null && pm10 is null)
            {
                AirQualityStatusChanged?.Invoke("Air quality telemetry: PM fields unavailable.");
                return null;
            }

            var snapshot = new AirQualitySnapshot(pm25, pm10, DateTime.UtcNow, "Open-Meteo AQ");
            _airQuality = snapshot;

            var pm25Str = pm25 is uint p25 ? $"{p25} µg/m³ PM2.5" : string.Empty;
            var pm10Str = pm10 is uint p10 ? $"{(pm25 is null ? "" : ", ")}{p10} µg/m³ PM10" : string.Empty;
            AirQualityStatusChanged?.Invoke(
                $"Air quality telemetry: OK {snapshot.FetchedUtc.ToLocalTime():h:mm:ss tt} ({pm25Str}{pm10Str})");
            return snapshot;
        }
        catch (Exception ex)
        {
            AirQualityStatusChanged?.Invoke($"Air quality telemetry: fetch failed ({ex.Message})");
            return null;
        }
        finally { _airQualityGate.Release(); }
    }

    private static string Coord(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool TryReadFloat(JsonElement parent, string name, out float value)
    {
        value = 0f;
        if (!parent.TryGetProperty(name, out var element)) return false;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out var d):
                value = (float)d;
                return true;
            case JsonValueKind.String when float.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _weatherGate.Dispose();
        _airQualityGate.Dispose();
    }
}
