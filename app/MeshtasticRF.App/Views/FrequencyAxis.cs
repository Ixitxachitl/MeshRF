// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MeshtasticRF.App.Views;

/// <summary>
/// Thin horizontal frequency ruler intended to sit directly under (or overlaid
/// on) the spectrum/waterfall. The displayed span is centered at
/// <see cref="CenterFreqHz"/> and is <see cref="SpanHz"/> wide, matching the
/// FFT-shifted spectrum bins (DC at the center). Draws evenly spaced ticks with
/// absolute-MHz labels. Renders nothing until both values are positive.
/// </summary>
public sealed class FrequencyAxis : FrameworkElement
{
    public static readonly DependencyProperty CenterFreqHzProperty =
        DependencyProperty.Register(nameof(CenterFreqHz), typeof(double), typeof(FrequencyAxis),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpanHzProperty =
        DependencyProperty.Register(nameof(SpanHz), typeof(double), typeof(FrequencyAxis),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(FrequencyAxis),
            new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Center frequency of the displayed span, in Hz.</summary>
    public double CenterFreqHz { get => (double)GetValue(CenterFreqHzProperty); set => SetValue(CenterFreqHzProperty, value); }
    /// <summary>Total displayed span (= device sample rate), in Hz.</summary>
    public double SpanHz { get => (double)GetValue(SpanHzProperty); set => SetValue(SpanHzProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    // Number of interior tick divisions across the width.
    private const int Divisions = 8;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        double center = CenterFreqHz, span = SpanHz;
        if (span <= 0 || center <= 0) return;

        var pen = new Pen(Foreground, 1.0);
        pen.Freeze();
        var brush = Foreground;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        double tickTop = 0;
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
                FlowDirection.LeftToRight, typeface, 10.0, brush, dpi);

            double tx = x - ft.Width / 2.0;
            // Keep edge labels within bounds.
            if (tx < 0) tx = 0;
            if (tx + ft.Width > w) tx = w - ft.Width;
            dc.DrawText(ft, new Point(tx, tickTop + tickLen + 1));
        }
    }
}
