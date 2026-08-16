// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using MeshRF.Mesh;

namespace MeshRF.Nodes;

/// <summary>
/// Builds <see cref="NodeTelemetryHistoryRecord"/> rows from a decoded
/// telemetry payload, including the "kind" tag and the value signature the
/// store uses to avoid recording a row that repeats the previous one verbatim.
///
/// Centralised so every call site classifies and de-duplicates history
/// identically — a signature computed differently anywhere would make rows
/// already in the store look new.
/// </summary>
public static class TelemetryHistoryFactory
{
    /// <summary>Which metric groups the payload carried, as a short tag.
    /// Signatures are compared within a kind, so a device-metrics packet never
    /// suppresses an environment one that happens to follow it.</summary>
    public static string Kind(MeshTelemetry t) =>
        (t.HasDeviceMetrics, t.HasEnvironmentMetrics, t.HasAirQualityMetrics, t.HasPowerMetrics) switch
        {
            (true, true, true, true) => "DEAP",
            (true, true, true, false) => "DEA",
            (true, true, false, true) => "DEP",
            (true, false, true, true) => "DAP",
            (false, true, true, true) => "EAP",
            (true, true, false, false) => "DE",
            (true, false, true, false) => "DA",
            (false, true, true, false) => "EA",
            (true, false, false, true) => "DP",
            (false, true, false, true) => "EP",
            (false, false, true, true) => "AP",
            (true, false, false, false) => "D",
            (false, true, false, false) => "E",
            (false, false, true, false) => "A",
            (false, false, false, true) => "P",
            _ => string.Empty,
        };

    public static bool HasAnyMetrics(MeshTelemetry t) =>
        t.HasDeviceMetrics || t.HasEnvironmentMetrics || t.HasAirQualityMetrics || t.HasPowerMetrics;

    public static string Signature(MeshTelemetry t) => string.Join("|",
        Kind(t),
        t.HasDeviceMetrics ? V(t.BatteryLevel) : string.Empty,
        t.HasDeviceMetrics ? V(t.Voltage) : string.Empty,
        t.HasDeviceMetrics ? V(t.ChannelUtilization) : string.Empty,
        t.HasDeviceMetrics ? V(t.AirUtilTx) : string.Empty,
        t.HasDeviceMetrics ? V(t.UptimeSeconds) : string.Empty,
        t.HasEnvironmentMetrics ? V(t.TemperatureC) : string.Empty,
        t.HasEnvironmentMetrics ? V(t.RelativeHumidityPct) : string.Empty,
        t.HasEnvironmentMetrics ? V(t.BarometricPressureHpa) : string.Empty,
        t.HasEnvironmentMetrics ? V(t.GasResistanceMohm) : string.Empty,
        t.HasEnvironmentMetrics ? V(t.Iaq) : string.Empty,
        t.HasAirQualityMetrics ? V(t.Pm25Standard) : string.Empty,
        t.HasAirQualityMetrics ? V(t.Pm100Standard) : string.Empty,
        t.HasPowerMetrics ? V(t.Ch1VoltageV) : string.Empty,
        t.HasPowerMetrics ? V(t.Ch1CurrentMa) : string.Empty);

    /// <summary>Builds the row. Only fields whose metric group is present are
    /// filled — a missing group stays null rather than recording a zero, so a
    /// chart can tell "not reported" from "reported as 0".</summary>
    public static NodeTelemetryHistoryRecord Build(uint nodeNum, DateTime timestampUtc, MeshTelemetry t) =>
        new(0,
            nodeNum,
            timestampUtc,
            t.HasDeviceMetrics ? t.BatteryLevel : null,
            t.HasDeviceMetrics ? t.Voltage : null,
            t.HasDeviceMetrics ? t.ChannelUtilization : null,
            t.HasDeviceMetrics ? t.AirUtilTx : null,
            t.HasDeviceMetrics ? t.UptimeSeconds : null,
            t.HasEnvironmentMetrics ? t.TemperatureC : null,
            t.HasEnvironmentMetrics ? t.RelativeHumidityPct : null,
            t.HasEnvironmentMetrics ? t.BarometricPressureHpa : null,
            t.HasEnvironmentMetrics ? t.GasResistanceMohm : null,
            t.HasEnvironmentMetrics ? t.Iaq : null,
            t.HasAirQualityMetrics ? t.Pm10Standard : null,
            t.HasAirQualityMetrics ? t.Pm25Standard : null,
            t.HasAirQualityMetrics ? t.Pm100Standard : null,
            t.HasAirQualityMetrics ? t.Pm10Environmental : null,
            t.HasAirQualityMetrics ? t.Pm25Environmental : null,
            t.HasAirQualityMetrics ? t.Pm100Environmental : null,
            t.HasPowerMetrics ? t.Ch1VoltageV : null,
            t.HasPowerMetrics ? t.Ch1CurrentMa : null,
            t.HasPowerMetrics ? t.Ch2VoltageV : null,
            t.HasPowerMetrics ? t.Ch2CurrentMa : null,
            t.HasPowerMetrics ? t.Ch3VoltageV : null,
            t.HasPowerMetrics ? t.Ch3CurrentMa : null,
            Signature(t));

    private static string V<T>(T? value) where T : struct, IFormattable =>
        value.HasValue ? value.Value.ToString(null, CultureInfo.InvariantCulture) : string.Empty;
}
