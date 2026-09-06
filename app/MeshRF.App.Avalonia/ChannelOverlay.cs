// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>One channel drawn across the spectrum: what it is and how wide.</summary>
/// <param name="Label">Preset name, and slot where it is not the default.</param>
/// <param name="IsPrimary">The channel the toolbar is set to, which is what
/// transmits and what the packet snapshot follows.</param>
public sealed record ChannelBand(string Label, double CenterHz, double BandwidthHz, bool IsPrimary);

/// <summary>
/// Marks each listener's channel over the waterfall: a pair of vertical
/// bars at its edges, a faint wash between them, and its name at the top.
/// Sits in the waterfall's own grid cell rather than in the waterfall, which
/// is an <see cref="Image"/> that only blits its bitmap.
/// </summary>
/// <remarks>
/// With one listener this is the channel being received, which is worth
/// seeing on its own: it says which of the signals on the waterfall the
/// demodulator is actually looking at.
/// </remarks>
public sealed class ChannelOverlay : Control
{
    public static readonly StyledProperty<double> CenterFreqHzProperty =
        AvaloniaProperty.Register<ChannelOverlay, double>(nameof(CenterFreqHz));
    public static readonly StyledProperty<double> SpanHzProperty =
        AvaloniaProperty.Register<ChannelOverlay, double>(nameof(SpanHz));
    public static readonly StyledProperty<IEnumerable<ChannelBand>?> BandsProperty =
        AvaloniaProperty.Register<ChannelOverlay, IEnumerable<ChannelBand>?>(nameof(Bands));

    /// <summary>Centre of the displayed span: what the radio is tuned to.</summary>
    public double CenterFreqHz { get => GetValue(CenterFreqHzProperty); set => SetValue(CenterFreqHzProperty, value); }
    /// <summary>Total displayed span, the device sample rate.</summary>
    public double SpanHz { get => GetValue(SpanHzProperty); set => SetValue(SpanHzProperty, value); }
    public IEnumerable<ChannelBand>? Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    private INotifyCollectionChanged? _watched;

    static ChannelOverlay()
    {
        CenterFreqHzProperty.Changed.AddClassHandler<ChannelOverlay>((v, _) => v.InvalidateVisual());
        SpanHzProperty.Changed.AddClassHandler<ChannelOverlay>((v, _) => v.InvalidateVisual());
        BandsProperty.Changed.AddClassHandler<ChannelOverlay>((v, e) => v.OnBandsChanged(e));
    }

    public ChannelOverlay()
    {
        // Drawn, not hit-tested: the waterfall underneath keeps its clicks.
        IsHitTestVisible = false;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The set is rebuilt in place when the receiver starts, so the
    /// collection itself is watched rather than only the property.</summary>
    private void OnBandsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_watched is not null) _watched.CollectionChanged -= OnBandsCollectionChanged;
        _watched = e.NewValue as INotifyCollectionChanged;
        if (_watched is not null) _watched.CollectionChanged += OnBandsCollectionChanged;
        InvalidateVisual();
    }

    private void OnBandsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1) return;
        if (Bands is not { } bands) return;

        double center = CenterFreqHz, span = SpanHz;
        if (span <= 0 || center <= 0) return;

        var primaryStroke = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x7F, 0xD1, 0xFF)), 1.0);
        var otherStroke = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xD9, 0x7F)), 1.0);
        var primaryFill = new SolidColorBrush(Color.FromArgb(0x1E, 0x7F, 0xD1, 0xFF));
        var otherFill = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xD9, 0x7F));
        var typeface = new Typeface("Segoe UI");

        // Labels are dropped rather than stacked where they would collide, so
        // a crowded band stays readable; the bars still show every channel.
        var taken = new List<(double Left, double Right)>();

        foreach (var band in bands)
        {
            double x = w * (0.5 + (band.CenterHz - center) / span);
            double halfWidth = w * (band.BandwidthHz / span) / 2.0;
            double left = x - halfWidth, right = x + halfWidth;
            if (right < 0 || left > w) continue;

            bool primary = band.IsPrimary;
            dc.FillRectangle(primary ? primaryFill : otherFill,
                             new Rect(left, 0, Math.Max(1.0, right - left), h));
            var stroke = primary ? primaryStroke : otherStroke;
            dc.DrawLine(stroke, new Point(left, 0), new Point(left, h));
            dc.DrawLine(stroke, new Point(right, 0), new Point(right, h));

            var text = new FormattedText(band.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                         typeface, 10.0, stroke.Brush);
            double tx = left + 3;
            if (tx + text.Width > w) tx = w - text.Width - 1;
            if (tx < 0) tx = 0;

            bool collides = false;
            foreach (var (l, r) in taken)
                if (tx < r && tx + text.Width > l) { collides = true; break; }
            if (collides) continue;

            taken.Add((tx, tx + text.Width));
            dc.DrawText(text, new Point(tx, 1));
        }
    }
}
