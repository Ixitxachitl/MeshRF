// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Thin horizontal frequency ruler intended to sit directly under (or overlaid
/// on) the spectrum/waterfall. The displayed span is centered at
/// <see cref="CenterFreqHz"/> and is <see cref="SpanHz"/> wide, matching the
/// FFT-shifted spectrum bins (DC at the center). Draws evenly spaced ticks
/// with absolute-MHz labels.
/// </summary>
public sealed class FrequencyAxis : Control
{
    public static readonly StyledProperty<double> CenterFreqHzProperty =
        AvaloniaProperty.Register<FrequencyAxis, double>(nameof(CenterFreqHz), 0.0);
    public static readonly StyledProperty<double> SpanHzProperty =
        AvaloniaProperty.Register<FrequencyAxis, double>(nameof(SpanHz), 0.0);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<FrequencyAxis, IBrush?>(nameof(Foreground), Brushes.Gainsboro);

    /// <summary>Center frequency of the displayed span, in Hz.</summary>
    public double CenterFreqHz { get => GetValue(CenterFreqHzProperty); set => SetValue(CenterFreqHzProperty, value); }
    /// <summary>Total displayed span (= device sample rate), in Hz.</summary>
    public double SpanHz { get => GetValue(SpanHzProperty); set => SetValue(SpanHzProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    // Number of interior tick divisions across the width.
    private const int Divisions = 8;

    static FrequencyAxis()
    {
        CenterFreqHzProperty.Changed.AddClassHandler<FrequencyAxis>((v, _) => v.InvalidateVisual());
        SpanHzProperty.Changed.AddClassHandler<FrequencyAxis>((v, _) => v.InvalidateVisual());
        ForegroundProperty.Changed.AddClassHandler<FrequencyAxis>((v, _) => v.InvalidateVisual());
    }

    public FrequencyAxis()
    {
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        double center = CenterFreqHz, span = SpanHz;
        if (span <= 0 || center <= 0) return;

        var brush = Foreground ?? Brushes.Gainsboro;
        var pen = new Pen(brush, 1.0);
        var typeface = new Typeface("Segoe UI");

        const double tickTop = 0;
        double tickLen = Math.Min(6, h * 0.4);

        for (int i = 0; i <= Divisions; i++)
        {
            double frac = (double)i / Divisions;
            double x = frac * w;
            // Snap edge ticks just inside so they are not clipped.
            if (i == 0) x = 0.5;
            else if (i == Divisions) x = w - 0.5;

            dc.DrawLine(pen, new Point(x, tickTop), new Point(x, tickTop + tickLen));

            double freqHz = center + (frac - 0.5) * span;
            string label = (freqHz / 1e6).ToString("0.000", CultureInfo.InvariantCulture);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10.0, brush);

            double tx = x - ft.Width / 2.0;
            // Keep edge labels within bounds.
            if (tx < 0) tx = 0;
            if (tx + ft.Width > w) tx = w - ft.Width;
            dc.DrawText(ft, new Point(tx, tickTop + tickLen + 1));
        }
    }
}
