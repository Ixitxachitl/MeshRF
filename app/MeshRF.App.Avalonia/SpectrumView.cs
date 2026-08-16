// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Lightweight dBFS spectrum line plot, drawn immediate-mode into a
/// StreamGeometry.
/// </summary>
public class SpectrumView : Control
{
    private static readonly SolidColorBrush s_background = new(Color.FromRgb(0x10, 0x14, 0x18));
    private static readonly Pen s_linePen = new(new SolidColorBrush(Color.FromRgb(0x35, 0xC8, 0xFF)), 1.2);
    private static readonly Pen s_dcPen = new(
        new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 0.5,
        new DashStyle(new double[] { 2.0, 3.0 }, 0));

    public static readonly StyledProperty<double> FloorDbProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(FloorDb), -100.0);
    public static readonly StyledProperty<double> CeilDbProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(CeilDb), 0.0);

    /// <summary>
    /// Temporal smoothing applied to the displayed trace, in (0, 1]. Each new
    /// frame is blended into the shown curve as an exponential moving average:
    /// shown += (incoming - shown) * SmoothingFactor. 1.0 = no smoothing (raw,
    /// jittery); lower values average more, calming per-frame noise.
    /// </summary>
    public static readonly StyledProperty<double> SmoothingFactorProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(SmoothingFactor), 0.35);

    public double FloorDb { get => GetValue(FloorDbProperty); set => SetValue(FloorDbProperty, value); }
    public double CeilDb { get => GetValue(CeilDbProperty); set => SetValue(CeilDbProperty, value); }
    public double SmoothingFactor { get => GetValue(SmoothingFactorProperty); set => SetValue(SmoothingFactorProperty, value); }

    private float[]? _lastFrame;

    static SpectrumView()
    {
        FloorDbProperty.Changed.AddClassHandler<SpectrumView>((v, _) => v.InvalidateVisual());
        CeilDbProperty.Changed.AddClassHandler<SpectrumView>((v, _) => v.InvalidateVisual());
    }

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

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        dc.DrawRectangle(s_background, null, new Rect(0, 0, w, h));
        if (w <= 1 || h <= 1) return;

        // Vertical DC marker at bin N/2.
        dc.DrawLine(s_dcPen, new Point(w / 2.0, 0), new Point(w / 2.0, h));

        var frame = _lastFrame;
        if (frame is null || frame.Length < 2) return;

        double floor = FloorDb;
        double ceil = CeilDb;
        if (ceil <= floor) ceil = floor + 1.0;
        double range = ceil - floor;
        int n = frame.Length;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double x = i * w / (n - 1);
                double v = frame[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) v = floor;
                double norm = (v - floor) / range;
                if (norm < 0.0) norm = 0.0; else if (norm > 1.0) norm = 1.0;
                double y = h - norm * h;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false);
                else ctx.LineTo(new Point(x, y), true);
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, s_linePen, geo);
    }
}
