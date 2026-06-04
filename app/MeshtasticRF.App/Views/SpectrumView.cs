// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MeshtasticRF.App.Views;

/// <summary>
/// Lightweight Canvas-based dBFS spectrum line plot. Scales the input frame
/// horizontally to fit, and clamps dBFS values into a [floor, ceil] window.
/// </summary>
public class SpectrumView : System.Windows.Controls.Canvas
{
    private readonly Polyline _line = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x35, 0xC8, 0xFF)),
        StrokeThickness = 1.2,
    };
    private readonly Line _gridDc = new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
        StrokeThickness = 0.5,
        StrokeDashArray = new DoubleCollection { 2, 3 },
    };

    public static readonly DependencyProperty FloorDbProperty =
        DependencyProperty.Register(nameof(FloorDb), typeof(double), typeof(SpectrumView),
            new PropertyMetadata(-100.0));
    public static readonly DependencyProperty CeilDbProperty =
        DependencyProperty.Register(nameof(CeilDb), typeof(double), typeof(SpectrumView),
            new PropertyMetadata(0.0));

    public double FloorDb { get => (double)GetValue(FloorDbProperty); set => SetValue(FloorDbProperty, value); }
    public double CeilDb  { get => (double)GetValue(CeilDbProperty);  set => SetValue(CeilDbProperty, value); }

    public SpectrumView()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18));
        Children.Add(_gridDc);
        Children.Add(_line);
        SizeChanged += (_, _) => Render(_lastFrame);
    }

    private float[]? _lastFrame;

    public void Update(ReadOnlySpan<float> frame)
    {
        // Copy because Render is async via layout; the caller may reuse buffer.
        if (_lastFrame is null || _lastFrame.Length != frame.Length)
            _lastFrame = new float[frame.Length];
        frame.CopyTo(_lastFrame);
        Render(_lastFrame);
    }

    private void Render(float[]? frame)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        // Vertical DC marker (DC bin is at index N/2 after FFT-shift).
        _gridDc.X1 = _gridDc.X2 = w / 2.0;
        _gridDc.Y1 = 0;
        _gridDc.Y2 = h;

        if (frame is null || frame.Length == 0)
        {
            _line.Points.Clear();
            return;
        }

        var floor = FloorDb;
        var ceil  = CeilDb;
        if (ceil <= floor) ceil = floor + 1.0;
        var range = ceil - floor;

        var n = frame.Length;
        var pts = new PointCollection(n);
        for (int i = 0; i < n; i++)
        {
            var x = (double)i * w / (n - 1);
            var v = frame[i];
            if (float.IsNaN(v) || float.IsInfinity(v)) v = (float)floor;
            var norm = (v - floor) / range;
            if (norm < 0) norm = 0; else if (norm > 1) norm = 1;
            var y = h - norm * h;
            pts.Add(new Point(x, y));
        }
        _line.Points = pts;
    }

    /// <summary>Helper for binding-friendly numeric formatting in XAML.</summary>
    public static string FormatDb(double v) =>
        v.ToString("F1", CultureInfo.InvariantCulture);
}
