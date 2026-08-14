// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Per-peer telemetry history: four metric groups (device, environmental, air
/// quality, power), each a graph beside the table of samples. Ported from
/// MeshRF.App's TelemetryHistoryWindow.
/// </summary>
public partial class TelemetryHistoryWindow : Window
{
    public TelemetryHistoryWindow()
    {
        InitializeComponent();
    }

    private async void OnClear(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ConversationTabViewModel convo) return;
        int count = convo.TelemetryHistory.Count;
        if (count == 0) return;
        if (!await ConfirmDialog.ConfirmAsync(this, "Clear telemetry history",
                $"Delete {count} recorded telemetry snapshot{(count == 1 ? "" : "s")} for {convo.PeerName}? This removes the stored history and cannot be undone.",
                confirmText: "Clear"))
            return;
        convo.ClearTelemetryHistoryCommand.Execute(null);
    }

    /// <summary>Opens the window for a peer, or focuses the one already open —
    /// mirrors MeshRF.App, which keeps one history window per conversation.</summary>
    public static void Show(Window owner, ConversationTabViewModel conversation)
    {
        conversation.EnsureHistoryLoaded();

        var w = new TelemetryHistoryWindow { DataContext = conversation };
        w.Title = $"Telemetry History — {conversation.TabHeader}";

        w.DeviceGraph.SetSeries(
            new TelemetrySeries("Battery", TelemetryGraph.Battery, p => p.BatteryPct),
            new TelemetrySeries("Voltage", TelemetryGraph.Voltage, p => p.VoltageV),
            new TelemetrySeries("Channel", TelemetryGraph.ChannelUtil, p => p.ChannelUtilPct),
            new TelemetrySeries("Air TX", TelemetryGraph.AirUtil, p => p.AirUtilTxPct),
            new TelemetrySeries("Uptime", TelemetryGraph.Uptime, p => p.UptimeSeconds));

        w.EnvironmentGraph.SetSeries(
            new TelemetrySeries("Temp", TelemetryGraph.Temperature, p => p.TemperatureC),
            new TelemetrySeries("Humidity", TelemetryGraph.Humidity, p => p.RelativeHumidityPct),
            new TelemetrySeries("Pressure", TelemetryGraph.Pressure, p => p.BarometricPressureHpa),
            new TelemetrySeries("Gas", TelemetryGraph.Gas, p => p.GasResistanceMohm),
            new TelemetrySeries("IAQ", TelemetryGraph.Iaq, p => p.IaqValue));

        // The environmental "e" variants default off, as in MeshRF.App: most
        // sensors report only the standard set, so showing both doubles the
        // lines for nothing.
        w.AirQualityGraph.SetSeries(
            new TelemetrySeries("PM1.0", TelemetryGraph.Pm1Std, p => p.Pm10Standard),
            new TelemetrySeries("PM2.5", TelemetryGraph.Pm25Std, p => p.Pm25Standard),
            new TelemetrySeries("PM10", TelemetryGraph.Pm100Std, p => p.Pm100Standard),
            new TelemetrySeries("PM1.0e", TelemetryGraph.Pm1Env, p => p.Pm10Environmental) { Enabled = false },
            new TelemetrySeries("PM2.5e", TelemetryGraph.Pm25Env, p => p.Pm25Environmental) { Enabled = false },
            new TelemetrySeries("PM10e", TelemetryGraph.Pm100Env, p => p.Pm100Environmental) { Enabled = false });

        w.PowerGraph.SetSeries(
            new TelemetrySeries("CH1 V", TelemetryGraph.Ch1V, p => p.Ch1VoltageV),
            new TelemetrySeries("CH1 A", TelemetryGraph.Ch1I, p => p.Ch1CurrentMa),
            new TelemetrySeries("CH2 V", TelemetryGraph.Ch2V, p => p.Ch2VoltageV),
            new TelemetrySeries("CH2 A", TelemetryGraph.Ch2I, p => p.Ch2CurrentMa),
            new TelemetrySeries("CH3 V", TelemetryGraph.Ch3V, p => p.Ch3VoltageV),
            new TelemetrySeries("CH3 A", TelemetryGraph.Ch3I, p => p.Ch3CurrentMa));

        // Each graph plots its own pane's points, so a group with no samples
        // draws nothing rather than a line of gaps.
        w.DeviceGraph.SetPoints(conversation.DeviceTelemetryHistory);
        w.EnvironmentGraph.SetPoints(conversation.EnvironmentalTelemetryHistory);
        w.AirQualityGraph.SetPoints(conversation.AirQualityTelemetryHistory);
        w.PowerGraph.SetPoints(conversation.PowerTelemetryHistory);

        w.Show(owner);
    }
}
