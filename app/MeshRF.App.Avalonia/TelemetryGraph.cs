// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>One plotted series: a label, its colour, and how to read its value
/// out of a telemetry point (null = not reported at that time).</summary>
public sealed record TelemetrySeries(string Label, Color Colour, Func<TelemetryHistoryPoint, double?> Selector)
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A titled sparkline pane: one line per enabled series over the shared time
/// axis, with a checkbox strip to toggle series. Ported from MeshRF.App's
/// telemetry window, which draws the same thing onto a WPF Canvas.
///
/// Each series is normalised to its own min/max rather than sharing one axis —
/// the quantities have wildly different ranges (uptime in seconds against
/// battery percent), so a shared scale would flatten everything but the
/// largest into a straight line. The point is the shape of each trend, not
/// comparison of absolute values between them.
/// </summary>
public sealed class TelemetryGraph : UserControl
{
    private readonly Plot _plot = new();
    private readonly WrapPanel _legend = new() { Margin = new Thickness(0, 4, 0, 0) };
    private IReadOnlyList<TelemetryHistoryPoint> _points = Array.Empty<TelemetryHistoryPoint>();

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<TelemetryGraph, string>(nameof(Title), string.Empty);

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    private readonly List<TelemetrySeries> _series = new();

    public TelemetryGraph()
    {
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
        title.Bind(TextBlock.TextProperty, this.GetObservable(TitleProperty).ToBinding());

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            Child = _plot,
        };
        frame.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanelBackground").ToBinding());
        frame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushPanelBorder").ToBinding());

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(title, 0);
        Grid.SetRow(frame, 1);
        Grid.SetRow(_legend, 2);
        root.Children.Add(title);
        root.Children.Add(frame);
        root.Children.Add(_legend);

        Content = root;
    }

    /// <summary>Defines the series this pane plots and builds its legend.</summary>
    public void SetSeries(params TelemetrySeries[] series)
    {
        _series.Clear();
        _series.AddRange(series);
        _legend.Children.Clear();

        foreach (var s in _series)
        {
            var box = new CheckBox
            {
                IsChecked = s.Enabled,
                Margin = new Thickness(0, 0, 10, 2),
                Content = new TextBlock { Text = s.Label, Foreground = new SolidColorBrush(s.Colour) },
            };
            var captured = s;
            box.IsCheckedChanged += (_, _) =>
            {
                captured.Enabled = box.IsChecked == true;
                _plot.InvalidateVisual();
            };
            _legend.Children.Add(box);
        }
        _plot.Configure(_series, _points);
    }

    public void SetPoints(IReadOnlyList<TelemetryHistoryPoint> points)
    {
        _points = points;
        _plot.Configure(_series, _points);
    }

    /// <summary>The drawing surface. Separate from the control so the legend and
    /// title stay ordinary layout while only this part is custom-rendered.</summary>
    private sealed class Plot : Control
    {
        private IReadOnlyList<TelemetrySeries> _series = Array.Empty<TelemetrySeries>();
        private IReadOnlyList<TelemetryHistoryPoint> _points = Array.Empty<TelemetryHistoryPoint>();

        public void Configure(IReadOnlyList<TelemetrySeries> series, IReadOnlyList<TelemetryHistoryPoint> points)
        {
            _series = series;
            _points = points;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 2 || h <= 2 || _points.Count == 0) return;

            const double pad = 4;
            double plotW = w - pad * 2, plotH = h - pad * 2;
            if (plotW <= 0 || plotH <= 0) return;

            // Shared time axis so the panes line up with each other.
            double tMin = _points[0].TimestampUtc.Ticks;
            double tMax = _points[^1].TimestampUtc.Ticks;
            double tSpan = tMax - tMin;

            foreach (var series in _series)
            {
                if (!series.Enabled) continue;

                // Collect this series' reported samples. Gaps are skipped
                // rather than drawn as zero, so a metric that stops being
                // reported doesn't dive to the bottom of the chart.
                var samples = new List<(double T, double V)>();
                double min = double.MaxValue, max = double.MinValue;
                foreach (var p in _points)
                {
                    if (series.Selector(p) is not double v) continue;
                    samples.Add((p.TimestampUtc.Ticks, v));
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                if (samples.Count < 2) continue;

                // A flat series would divide by zero; centre it instead.
                double range = max - min;
                bool flat = range <= double.Epsilon;

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    for (int i = 0; i < samples.Count; i++)
                    {
                        double x = pad + (tSpan > 0 ? (samples[i].T - tMin) / tSpan : 0.5) * plotW;
                        double norm = flat ? 0.5 : (samples[i].V - min) / range;
                        double y = pad + (1.0 - norm) * plotH;
                        var pt = new Point(x, y);
                        if (i == 0) ctx.BeginFigure(pt, false);
                        else ctx.LineTo(pt);
                    }
                    ctx.EndFigure(false);
                }

                context.DrawGeometry(null, new Pen(new SolidColorBrush(series.Colour), 1.5), geometry);
            }
        }
    }

    // Shared palette, matching the colours MeshRF.App uses for these series.
    public static readonly Color Battery = Color.Parse("#2ECC71");
    public static readonly Color Voltage = Color.Parse("#3498DB");
    public static readonly Color ChannelUtil = Color.Parse("#F1C40F");
    public static readonly Color AirUtil = Color.Parse("#E67E22");
    public static readonly Color Uptime = Color.Parse("#7F8C8D");
    public static readonly Color Temperature = Color.Parse("#E74C3C");
    public static readonly Color Humidity = Color.Parse("#1ABC9C");
    public static readonly Color Pressure = Color.Parse("#9B59B6");
    public static readonly Color Gas = Color.Parse("#95A5A6");
    public static readonly Color Iaq = Color.Parse("#FF6B9A");
    public static readonly Color Pm1Std = Color.Parse("#5DADE2");
    public static readonly Color Pm25Std = Color.Parse("#EB984E");
    public static readonly Color Pm100Std = Color.Parse("#E74C3C");
    public static readonly Color Pm1Env = Color.Parse("#A3D8F4");
    public static readonly Color Pm25Env = Color.Parse("#D4AC0D");
    public static readonly Color Pm100Env = Color.Parse("#C0392B");
    public static readonly Color Ch1V = Color.Parse("#F39C12");
    public static readonly Color Ch1I = Color.Parse("#E67E22");
    public static readonly Color Ch2V = Color.Parse("#27AE60");
    public static readonly Color Ch2I = Color.Parse("#2ECC71");
    public static readonly Color Ch3V = Color.Parse("#8E44AD");
    public static readonly Color Ch3I = Color.Parse("#9B59B6");
}
