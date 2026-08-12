// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using MeshRF.Nodes;

namespace MeshRF.AvaloniaApp;

/// <summary>One recorded position for a peer, with its display strings
/// pre-rendered. Ported from MeshRF.App's ConversationViewModel.</summary>
public sealed record LocationHistoryPoint(
    double Latitude, double Longitude, int? AltitudeM, string AltitudeDisplay, DateTime TimestampUtc)
{
    private const string UiDateTimeFormat = "M/d/yyyy h:mm:ss tt";

    public long Id { get; init; }

    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();

    public string Display =>
        $"{TimestampLocal.ToString(UiDateTimeFormat, CultureInfo.CurrentCulture)}  {Latitude:0.#####}, {Longitude:0.#####}"
        + (string.IsNullOrWhiteSpace(AltitudeDisplay) ? string.Empty : $"  {AltitudeDisplay}");
}

/// <summary>
/// One telemetry snapshot for a peer. Carries both the raw nullable values
/// (the graphs plot these) and formatted strings (the grids show these), so
/// neither has to re-derive the other. A null value means the metric was not
/// reported, which is distinct from a reported zero.
/// </summary>
public sealed record TelemetryHistoryPoint(
    DateTime TimestampUtc,
    double? BatteryPct, double? VoltageV, double? ChannelUtilPct, double? AirUtilTxPct, double? UptimeSeconds,
    double? TemperatureC, double? RelativeHumidityPct, double? BarometricPressureHpa,
    double? GasResistanceMohm, double? IaqValue,
    double? Pm10Standard, double? Pm25Standard, double? Pm100Standard,
    double? Pm10Environmental, double? Pm25Environmental, double? Pm100Environmental,
    double? Ch1VoltageV, double? Ch1CurrentMa, double? Ch2VoltageV, double? Ch2CurrentMa,
    double? Ch3VoltageV, double? Ch3CurrentMa,
    string Battery, string Voltage, string ChannelUtil, string AirUtilTx, string Uptime,
    string Temperature, string Humidity, string Pressure, string GasResistance, string AirQuality,
    string Pm10Std, string Pm25Std, string Pm100Std, string Pm10Env, string Pm25Env, string Pm100Env,
    string Ch1Voltage, string Ch1Current, string Ch2Voltage, string Ch2Current,
    string Ch3Voltage, string Ch3Current,
    string Signature)
{
    public long Id { get; init; }

    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();

    // Which panes this point belongs in. A packet carries one or more metric
    // groups, so a point appears only in the panes it actually has data for.
    public bool HasDeviceTelemetry =>
        BatteryPct.HasValue || VoltageV.HasValue || ChannelUtilPct.HasValue ||
        AirUtilTxPct.HasValue || UptimeSeconds.HasValue;

    public bool HasEnvironmentalTelemetry =>
        TemperatureC.HasValue || RelativeHumidityPct.HasValue ||
        BarometricPressureHpa.HasValue || GasResistanceMohm.HasValue || IaqValue.HasValue;

    public bool HasAirQualityTelemetry =>
        Pm10Standard.HasValue || Pm25Standard.HasValue || Pm100Standard.HasValue ||
        Pm10Environmental.HasValue || Pm25Environmental.HasValue || Pm100Environmental.HasValue;

    public bool HasPowerTelemetry =>
        Ch1VoltageV.HasValue || Ch1CurrentMa.HasValue || Ch2VoltageV.HasValue ||
        Ch2CurrentMa.HasValue || Ch3VoltageV.HasValue || Ch3CurrentMa.HasValue;
}

/// <summary>Turns stored history rows into display points. Temperature and
/// pressure go through the caller's formatters so they follow the app's unit
/// setting.</summary>
public static class TelemetryHistoryPointFactory
{
    public static TelemetryHistoryPoint FromRecord(
        NodeTelemetryHistoryRecord r,
        Func<float, string>? formatTemperature = null,
        Func<float, string>? formatPressure = null) =>
        new(r.TimestampUtc,
            r.BatteryPct, r.VoltageV, r.ChannelUtilPct, r.AirUtilTxPct, r.UptimeSeconds,
            r.TemperatureC, r.RelativeHumidityPct, r.BarometricPressureHpa,
            r.GasResistanceMohm, r.IaqValue,
            r.Pm10Standard, r.Pm25Standard, r.Pm100Standard,
            r.Pm10Environmental, r.Pm25Environmental, r.Pm100Environmental,
            r.Ch1VoltageV, r.Ch1CurrentMa, r.Ch2VoltageV, r.Ch2CurrentMa,
            r.Ch3VoltageV, r.Ch3CurrentMa,
            r.BatteryPct is double bat ? $"{bat:0}%" : string.Empty,
            r.VoltageV is double volt ? $"{volt:0.00} V" : string.Empty,
            r.ChannelUtilPct is double chUtil ? $"{chUtil:0.0}%" : string.Empty,
            r.AirUtilTxPct is double airUtil ? $"{airUtil:0.0}%" : string.Empty,
            r.UptimeSeconds is double up ? FormatUptime((uint)Math.Max(0, up)) : string.Empty,
            r.TemperatureC is double temp
                ? (formatTemperature?.Invoke((float)temp) ?? $"{temp:0.0} °C") : string.Empty,
            r.RelativeHumidityPct is double hum ? $"{hum:0.0}%" : string.Empty,
            r.BarometricPressureHpa is double pres
                ? (formatPressure?.Invoke((float)pres) ?? $"{pres:0.0} hPa") : string.Empty,
            r.GasResistanceMohm is double gas ? $"{gas:0.0} MΩ" : string.Empty,
            r.IaqValue is double iaq ? iaq.ToString("0", CultureInfo.InvariantCulture) : string.Empty,
            Pm(r.Pm10Standard), Pm(r.Pm25Standard), Pm(r.Pm100Standard),
            Pm(r.Pm10Environmental), Pm(r.Pm25Environmental), Pm(r.Pm100Environmental),
            r.Ch1VoltageV is double c1v ? $"{c1v:0.000} V" : string.Empty,
            r.Ch1CurrentMa is double c1i ? $"{c1i:0.0} mA" : string.Empty,
            r.Ch2VoltageV is double c2v ? $"{c2v:0.000} V" : string.Empty,
            r.Ch2CurrentMa is double c2i ? $"{c2i:0.0} mA" : string.Empty,
            r.Ch3VoltageV is double c3v ? $"{c3v:0.000} V" : string.Empty,
            r.Ch3CurrentMa is double c3i ? $"{c3i:0.0} mA" : string.Empty,
            r.Signature)
        { Id = r.Id };

    private static string Pm(double? value) =>
        value is double v ? $"{v:0} μg/m³" : string.Empty;

    private static string FormatUptime(uint seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{span.Seconds}s";
    }
}
