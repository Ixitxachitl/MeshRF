// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using MeshRF.App.ViewModels;
using MeshRF.Mqtt;

namespace MeshRF.App.Units;

public enum UnitSystem
{
    Metric,
    Imperial,
}

public static class DisplayUnits
{
    private const double KmPerMile = 1.609344;
    private const double FeetPerMeter = 3.28083989501312;
    private const double InchesMercuryPerHpa = 0.0295299830714;

    private static readonly (byte Bits, double RadiusMeters)[] s_positionPrecisions =
    [
        (10, 23000),
        (11, 12000),
        (12, 5800),
        (13, 2900),
        (14, 1500),
        (15, 700),
        (16, 350),
        (17, 200),
        (18, 90),
        (19, 50),
    ];

    public static UnitSystem Parse(string? value) =>
        string.Equals(value, nameof(UnitSystem.Imperial), StringComparison.OrdinalIgnoreCase)
            ? UnitSystem.Imperial
            : UnitSystem.Metric;

    public static bool IsImperial(UnitSystem unitSystem) => unitSystem == UnitSystem.Imperial;

    public static string DistanceUnitShort(UnitSystem unitSystem) =>
        IsImperial(unitSystem) ? "mi" : "km";

    public static string DistanceUnitLong(UnitSystem unitSystem) =>
        IsImperial(unitSystem) ? "miles" : "km";

    public static string AltitudeUnitShort(UnitSystem unitSystem) =>
        IsImperial(unitSystem) ? "ft" : "m";

    public static string TemperatureUnitShort(UnitSystem unitSystem) =>
        IsImperial(unitSystem) ? "\u00B0F" : "\u00B0C";

    public static string FormatTemperature(float temperatureC, UnitSystem unitSystem) =>
        IsImperial(unitSystem)
            ? $"{CelsiusToFahrenheit(temperatureC):F1} \u00B0F"
            : $"{temperatureC:F1} \u00B0C";

    public static string FormatPressure(float pressureHpa, UnitSystem unitSystem) =>
        IsImperial(unitSystem)
            ? $"{pressureHpa * InchesMercuryPerHpa:F2} inHg"
            : $"{pressureHpa:F0} hPa";

    public static string FormatPressure(double pressureHpa, UnitSystem unitSystem) =>
        FormatPressure((float)pressureHpa, unitSystem);

    public static string FormatAltitude(int altitudeMeters, UnitSystem unitSystem) =>
        IsImperial(unitSystem)
            ? $"{Math.Round(altitudeMeters * FeetPerMeter):F0} ft"
            : $"{altitudeMeters} m";

    public static string FormatAltitude(int? altitudeMeters, UnitSystem unitSystem) =>
        altitudeMeters is int altitude ? FormatAltitude(altitude, unitSystem) : string.Empty;

    public static string FormatAltitudeInput(int? altitudeMeters, UnitSystem unitSystem) =>
        altitudeMeters is int altitude
            ? (IsImperial(unitSystem)
                ? Math.Round(altitude * FeetPerMeter).ToString("F0", CultureInfo.InvariantCulture)
                : altitude.ToString(CultureInfo.InvariantCulture))
            : string.Empty;

    public static int? ParseAltitudeInput(string? text, UnitSystem unitSystem)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;

        return IsImperial(unitSystem)
            ? (int)Math.Round(parsed / FeetPerMeter)
            : (int)Math.Round(parsed);
    }

    public static double ConvertDistanceInputToKm(double distance, UnitSystem unitSystem) =>
        IsImperial(unitSystem) ? distance * KmPerMile : distance;

    public static string ConvertDistanceText(string? text, UnitSystem fromUnits, UnitSystem toUnits)
    {
        if (string.IsNullOrWhiteSpace(text) || fromUnits == toUnits) return text ?? string.Empty;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed))
            return text ?? string.Empty;

        var distanceKm = ConvertDistanceInputToKm(parsed, fromUnits);
        var converted = IsImperial(toUnits) ? distanceKm / KmPerMile : distanceKm;
        return converted.ToString("0.###", CultureInfo.CurrentCulture);
    }

    public static IReadOnlyList<PositionPrecisionOption> BuildPositionPrecisionOptions(UnitSystem unitSystem)
    {
        var options = new List<PositionPrecisionOption>
        {
            new(0, "Do not share location")
        };

        options.AddRange(s_positionPrecisions.Select(p =>
            new PositionPrecisionOption(p.Bits, $"Within {FormatRadius(p.RadiusMeters, unitSystem)}")));
        options.Add(new PositionPrecisionOption(32, "Precise"));
        return options;
    }

    /// <summary>Selectable MQTT MapReport position precisions — firmware
    /// restricts map-report fuzzing to 12-15 bits (~5.8 km down to ~700 m),
    /// unlike the wider 0-32 range channels offer for on-air position
    /// sharing.</summary>
    public static IReadOnlyList<PositionPrecisionOption> BuildMapReportPrecisionOptions(UnitSystem unitSystem) =>
        s_positionPrecisions
            .Where(p => p.Bits >= MqttPolicy.MinMapPositionPrecision && p.Bits <= MqttPolicy.MaxMapPositionPrecision)
            .Select(p => new PositionPrecisionOption(p.Bits, $"Within {FormatRadius(p.RadiusMeters, unitSystem)}"))
            .ToList();

    private static string FormatRadius(double radiusMeters, UnitSystem unitSystem)
    {
        if (IsImperial(unitSystem))
        {
            var feet = radiusMeters * FeetPerMeter;
            if (feet >= 5280)
                return $"{feet / 5280d:0.#} mi";
            return $"{Math.Round(feet / 10d) * 10d:0} ft";
        }

        if (radiusMeters >= 1000)
        {
            var kilometers = radiusMeters / 1000d;
            return kilometers >= 10 ? $"{kilometers:0} km" : $"{kilometers:0.#} km";
        }

        return $"{Math.Round(radiusMeters / 10d) * 10d:0} m";
    }

    private static float CelsiusToFahrenheit(float celsius) => (celsius * 9f / 5f) + 32f;
}
