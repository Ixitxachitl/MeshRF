// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MeshRF.App.ViewModels;

namespace MeshRF.App.Views;

public partial class TelemetryHistoryWindow : Window
{
    private ConversationViewModel? _conversation;

    public TelemetryHistoryWindow(ConversationViewModel conversation)
    {
        InitializeComponent();
        ApplySavedLayout();
        _conversation = conversation;
        DataContext = conversation;
        Title = $"Telemetry History - {conversation.TabHeader}";
        conversation.TelemetryHistory.CollectionChanged += TelemetryHistory_CollectionChanged;
        Closed += (_, _) =>
        {
            SaveLayout();
            conversation.TelemetryHistory.CollectionChanged -= TelemetryHistory_CollectionChanged;
        };
        DrawSparkGraph();
    }

    private void TelemetryHistory_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DrawSparkGraph();

    private void SparkCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSparkGraph();

    private void SparkGraphChanged(object sender, RoutedEventArgs e) => DrawSparkGraph();

    private void ApplySavedLayout()
    {
        var settings = AppSettings.Load();
        if (settings.TelemetryHistoryWindowWidth is double width && width >= MinWidth)
            Width = width;
        if (settings.TelemetryHistoryWindowHeight is double height && height >= MinHeight)
            Height = height;
        if (settings.TelemetryHistoryLeftPaneWidth is double leftWidth && leftWidth >= GraphPaneColumn.MinWidth)
            GraphPaneColumn.Width = new GridLength(leftWidth, GridUnitType.Pixel);
        if (settings.TelemetryHistoryTopPaneHeight is double topHeight && topHeight >= DevicePaneRow.MinHeight)
            DevicePaneRow.Height = new GridLength(topHeight, GridUnitType.Pixel);
        if (settings.TelemetryHistoryMiddlePaneHeight is double midHeight && midHeight >= EnvironmentPaneRow.MinHeight)
            EnvironmentPaneRow.Height = new GridLength(midHeight, GridUnitType.Pixel);
    }

    private void SaveLayout()
    {
        var settings = AppSettings.Load();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.TelemetryHistoryWindowWidth = Math.Max(MinWidth, bounds.Width);
        settings.TelemetryHistoryWindowHeight = Math.Max(MinHeight, bounds.Height);
        settings.TelemetryHistoryLeftPaneWidth = Math.Max(GraphPaneColumn.MinWidth, GraphPaneColumn.ActualWidth);
        settings.TelemetryHistoryTopPaneHeight = Math.Max(DevicePaneRow.MinHeight, DevicePaneRow.ActualHeight);
        settings.TelemetryHistoryMiddlePaneHeight = Math.Max(EnvironmentPaneRow.MinHeight, EnvironmentPaneRow.ActualHeight);
        settings.Save();
    }

    private void DrawSparkGraph()
    {
        if (_conversation is null || DeviceSparkCanvas is null || EnvironmentSparkCanvas is null || AirQualitySparkCanvas is null)
            return;

        DeviceSparkCanvas.Children.Clear();
        EnvironmentSparkCanvas.Children.Clear();
        AirQualitySparkCanvas.Children.Clear();

        var deviceSamples = _conversation.DeviceTelemetryHistory.ToList();
        DrawSparkGraph(DeviceSparkCanvas, deviceSamples,
        [
            new SeriesDefinition(BatteryToggle, p => p.BatteryPct, Color.FromRgb(46, 204, 113)),
            new SeriesDefinition(VoltageToggle, p => p.VoltageV, Color.FromRgb(52, 152, 219)),
            new SeriesDefinition(ChannelUtilToggle, p => p.ChannelUtilPct, Color.FromRgb(241, 196, 15)),
            new SeriesDefinition(AirUtilToggle, p => p.AirUtilTxPct, Color.FromRgb(230, 126, 34)),
            new SeriesDefinition(UptimeToggle, p => p.UptimeSeconds, Color.FromRgb(127, 140, 141)),
        ]);

        var environmentSamples = _conversation.EnvironmentalTelemetryHistory.ToList();
        DrawSparkGraph(EnvironmentSparkCanvas, environmentSamples,
        [
            new SeriesDefinition(TemperatureToggle, p => p.TemperatureC, Color.FromRgb(231, 76, 60)),
            new SeriesDefinition(HumidityToggle, p => p.RelativeHumidityPct, Color.FromRgb(26, 188, 156)),
            new SeriesDefinition(PressureToggle, p => p.BarometricPressureHpa, Color.FromRgb(155, 89, 182)),
            new SeriesDefinition(GasToggle, p => p.GasResistanceMohm, Color.FromRgb(149, 165, 166)),
            new SeriesDefinition(IaqToggle, p => p.IaqValue, Color.FromRgb(255, 107, 154)),
        ]);

        var airQualitySamples = _conversation.AirQualityTelemetryHistory.ToList();
        DrawSparkGraph(AirQualitySparkCanvas, airQualitySamples,
        [
            new SeriesDefinition(Pm1StdToggle,   p => p.Pm10Standard,      Color.FromRgb(93, 173, 226)),
            new SeriesDefinition(Pm25StdToggle,  p => p.Pm25Standard,      Color.FromRgb(235, 152, 78)),
            new SeriesDefinition(Pm100StdToggle, p => p.Pm100Standard,     Color.FromRgb(231, 76, 60)),
            new SeriesDefinition(Pm1EnvToggle,   p => p.Pm10Environmental,  Color.FromRgb(163, 216, 244)),
            new SeriesDefinition(Pm25EnvToggle,  p => p.Pm25Environmental,  Color.FromRgb(212, 172, 13)),
            new SeriesDefinition(Pm100EnvToggle, p => p.Pm100Environmental, Color.FromRgb(192, 57, 43)),
        ]);
    }

    private void DrawSparkGraph(Canvas canvas,
                                IReadOnlyList<TelemetryHistoryPoint> samples,
                                IReadOnlyList<SeriesDefinition> series)
    {
        if (samples.Count == 0)
            return;

        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        var gridBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)) { Opacity = 0.35 };
        for (int i = 1; i < 4; i++)
        {
            double y = height * i / 4.0;
            canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
        }

        foreach (var item in series)
            DrawSeries(canvas, samples, item, width, height);
    }

    private void DrawSeries(Canvas canvas,
                            IReadOnlyList<TelemetryHistoryPoint> samples,
                            SeriesDefinition series,
                            double width,
                            double height)
    {
        if (series.Toggle.IsChecked != true)
            return;

        var points = samples
            .Select((sample, index) => (Value: series.Value(sample), Index: index))
            .Where(p => p.Value.HasValue)
            .ToList();
        if (points.Count == 0)
            return;

        double min = points.Min(p => p.Value!.Value);
        double max = points.Max(p => p.Value!.Value);
        double span = max - min;
        const double pad = 10.0;
        double plotW = Math.Max(1, width - pad * 2);
        double plotH = Math.Max(1, height - pad * 2);
        double indexSpan = Math.Max(1, samples.Count - 1);

        var line = new Polyline
        {
            Stroke = new SolidColorBrush(series.Color),
            StrokeThickness = 2.0,
            Opacity = 0.95,
        };

        foreach (var point in points)
        {
            double normalized = span <= double.Epsilon
                ? 0.5
                : (point.Value!.Value - min) / span;
            line.Points.Add(new Point(
                pad + (point.Index / indexSpan) * plotW,
                pad + (1.0 - normalized) * plotH));
        }

        canvas.Children.Add(line);
    }

    private sealed record SeriesDefinition(
        CheckBox Toggle,
        Func<TelemetryHistoryPoint, double?> Value,
        Color Color);
}