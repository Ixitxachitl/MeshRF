// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MeshRF.App.Views;

/// <summary>
/// Lightweight FrameworkElement-based dBFS spectrum line plot. Draws via
/// OnRender/StreamGeometry so no PointCollection is created each frame and
/// WPF's render-thread geometry load stays minimal.
/// </summary>
public class SpectrumView : FrameworkElement
{
    private static readonly SolidColorBrush s_background;
    private static readonly Pen s_linePen;
    private static readonly Pen s_dcPen;

    static SpectrumView()
    {
        var bg = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18));
        bg.Freeze();
        s_background = bg;

        var lineBrush = new SolidColorBrush(Color.FromRgb(0x35, 0xC8, 0xFF));
        lineBrush.Freeze();
        s_linePen = new Pen(lineBrush, 1.2);
        s_linePen.Freeze();

        var dcBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        dcBrush.Freeze();
        var dashes = new DoubleCollection(new[] { 2.0, 3.0 });
        dashes.Freeze();
        var dashStyle = new DashStyle(dashes, 0);
        dashStyle.Freeze();
        s_dcPen = new Pen(dcBrush, 0.5) { DashStyle = dashStyle };
        s_dcPen.Freeze();
    }

    public static readonly DependencyProperty FloorDbProperty =
        DependencyProperty.Register(nameof(FloorDb), typeof(double), typeof(SpectrumView),
            new PropertyMetadata(-100.0, (d, _) => ((SpectrumView)d).InvalidateVisual()));
    public static readonly DependencyProperty CeilDbProperty =
        DependencyProperty.Register(nameof(CeilDb), typeof(double), typeof(SpectrumView),
            new PropertyMetadata(0.0, (d, _) => ((SpectrumView)d).InvalidateVisual()));

    /// <summary>
    /// Temporal smoothing applied to the displayed trace, in (0, 1]. Each new
    /// frame is blended into the shown curve as an exponential moving average:
    /// shown += (incoming - shown) * SmoothingFactor. 1.0 = no smoothing (raw,
    /// jittery at high frame rates); lower values average more, calming the
    /// per-frame noise without reducing the underlying update rate.
    /// </summary>
    public static readonly DependencyProperty SmoothingFactorProperty =
        DependencyProperty.Register(nameof(SmoothingFactor), typeof(double), typeof(SpectrumView),
            new PropertyMetadata(0.35));

    public double FloorDb { get => (double)GetValue(FloorDbProperty); set => SetValue(FloorDbProperty, value); }
    public double CeilDb  { get => (double)GetValue(CeilDbProperty);  set => SetValue(CeilDbProperty, value); }
    public double SmoothingFactor { get => (double)GetValue(SmoothingFactorProperty); set => SetValue(SmoothingFactorProperty, value); }

    private float[]? _lastFrame;

    public SpectrumView()
    {
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public void Update(ReadOnlySpan<float> frame)
    {
        // Re-allocate (and reset the smoothing state) when the bin count changes.
        if (_lastFrame is null || _lastFrame.Length != frame.Length)
        {
            _lastFrame = new float[frame.Length];
            frame.CopyTo(_lastFrame);
            InvalidateVisual();
            return;
        }

        double alpha = SmoothingFactor;
        if (alpha >= 1.0 || alpha <= 0.0)
        {
            // No smoothing: show the incoming frame verbatim.
            frame.CopyTo(_lastFrame);
            InvalidateVisual();
            return;
        }

        float a = (float)alpha;
        var shown = _lastFrame;
        for (int i = 0; i < frame.Length; i++)
        {
            float v = frame[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
                continue; // keep the previous value through dropouts
            shown[i] += (v - shown[i]) * a;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        dc.DrawRectangle(s_background, null, new Rect(0, 0, w, h));
        if (w <= 1 || h <= 1) return;

        // Vertical DC marker at bin N/2.
        dc.DrawLine(s_dcPen, new Point(w / 2.0, 0), new Point(w / 2.0, h));

        var frame = _lastFrame;
        if (frame is null || frame.Length < 2) return;

        double floor = FloorDb;
        double ceil  = CeilDb;
        if (ceil <= floor) ceil = floor + 1.0;
        double range = ceil - floor;
        int n = frame.Length;

        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double x = i * w / (n - 1);
                double v = frame[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) v = floor;
                double norm = (v - floor) / range;
                if (norm < 0.0) norm = 0.0; else if (norm > 1.0) norm = 1.0;
                double y = h - norm * h;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else        ctx.LineTo(new Point(x, y), true, false);
            }
        }
        dc.DrawGeometry(null, s_linePen, geo);
    }

    /// <summary>Helper for binding-friendly numeric formatting in XAML.</summary>
    public static string FormatDb(double v) =>
        v.ToString("F1", CultureInfo.InvariantCulture);
}
