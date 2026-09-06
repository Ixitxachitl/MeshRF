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
/// bars at its edges, a faint wash between them, and its name. Sits in the
/// waterfall's own grid cell rather than in the waterfall, which is an
/// <see cref="Image"/> that only blits its bitmap.
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
    public static readonly StyledProperty<WaterfallColormap> ColormapProperty =
        AvaloniaProperty.Register<ChannelOverlay, WaterfallColormap>(nameof(Colormap));

    /// <summary>Centre of the displayed span: what the radio is tuned to.</summary>
    public double CenterFreqHz { get => GetValue(CenterFreqHzProperty); set => SetValue(CenterFreqHzProperty, value); }
    /// <summary>Total displayed span, the device sample rate.</summary>
    public double SpanHz { get => GetValue(SpanHzProperty); set => SetValue(SpanHzProperty, value); }
    public IEnumerable<ChannelBand>? Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    /// <summary>What the waterfall underneath is painted with. The marks are
    /// drawn in colours that ramp does not use, so they stay visible on it.</summary>
    public WaterfallColormap Colormap { get => GetValue(ColormapProperty); set => SetValue(ColormapProperty, value); }

    private INotifyCollectionChanged? _watched;

    static ChannelOverlay()
    {
        CenterFreqHzProperty.Changed.AddClassHandler<ChannelOverlay>((v, _) => v.InvalidateVisual());
        SpanHzProperty.Changed.AddClassHandler<ChannelOverlay>((v, _) => v.InvalidateVisual());
        ColormapProperty.Changed.AddClassHandler<ChannelOverlay>((v, _) => v.InvalidateVisual());
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

    /// <summary>
    /// What to draw the marks in over one colour ramp: two accents the ramp
    /// itself never produces, so neither can be mistaken for signal, and the
    /// colour of that ramp's noise floor to outline them against.
    /// </summary>
    /// <remarks>
    /// A fixed palette does not work. Turbo runs dark blue to red, Inferno
    /// black to pale yellow, and Meshtastic starts at <em>white</em> — so the
    /// one waterfall most of the display is a pale wash, and the pale blue
    /// these marks used to be drawn in disappeared into it entirely.
    /// </remarks>
    private readonly record struct Palette(Color Primary, Color Other, Color Floor);

    private static Palette PaletteFor(WaterfallColormap colormap) => colormap switch
    {
        // Blue through green to red, and never magenta or white.
        WaterfallColormap.Turbo => new(Colors.White, Color.FromRgb(0xFF, 0x5C, 0xF0), Color.FromRgb(0x14, 0x12, 0x10)),
        // Black through crimson to pale yellow, and never cyan or green.
        WaterfallColormap.Inferno => new(Color.FromRgb(0x5C, 0xE1, 0xFF), Color.FromRgb(0x7C, 0xFF, 0x9E), Colors.Black),
        // White through green and yellow to blue: a light field, so the marks
        // are dark ones the ramp has no room for.
        _ => new(Color.FromRgb(0xB0, 0x00, 0xB0), Color.FromRgb(0xA8, 0x4A, 0x00), Colors.White),
    };

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1) return;
        if (Bands is not { } bands) return;

        double center = CenterFreqHz, span = SpanHz;
        if (span <= 0 || center <= 0) return;

        var palette = PaletteFor(Colormap);
        var typeface = new Typeface("Segoe UI");
        // Outlined against the ramp's own floor colour, the way the map draws
        // a place name over whatever it crosses.
        var halo = new Pen(new SolidColorBrush(WithAlpha(palette.Floor, 0xD0)), 3.0,
                           lineJoin: PenLineJoin.Round);

        // Lay the bands out first: whether a name fits inside its own channel
        // decides how every name is drawn, and that cannot be known one band
        // at a time.
        var placed = new List<(ChannelBand Band, double Left, double Right, FormattedText Text)>();
        bool everyNameFitsItsChannel = true;

        foreach (var band in bands)
        {
            double x = w * (0.5 + (band.CenterHz - center) / span);
            double halfWidth = w * (band.BandwidthHz / span) / 2.0;
            double left = x - halfWidth, right = x + halfWidth;
            if (right < 0 || left > w) continue;

            var accent = band.IsPrimary ? palette.Primary : palette.Other;
            var text = new FormattedText(band.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                         typeface, 10.0, new SolidColorBrush(accent));
            placed.Add((band, left, right, text));
            if (right - left < text.Width + 6) everyNameFitsItsChannel = false;
        }

        foreach (var (band, left, right, _) in placed)
        {
            var accent = band.IsPrimary ? palette.Primary : palette.Other;
            // A 250 kHz channel is a hundredth of a 16 MS/s capture, so the
            // wash keeps a floor width or a listener would be a hairline.
            var area = new Rect(left, 0, Math.Max(2.0, right - left), h);
            dc.FillRectangle(new SolidColorBrush(WithAlpha(accent, 0x20)), area);

            // The edges, each outlined so they read over a bright ramp too.
            var edge = new Pen(new SolidColorBrush(accent), band.IsPrimary ? 1.6 : 1.0);
            foreach (double bx in new[] { left, right })
            {
                dc.DrawLine(halo, new Point(bx, 0), new Point(bx, h));
                dc.DrawLine(edge, new Point(bx, 0), new Point(bx, h));
            }
        }

        // Names are dropped rather than stacked where they would collide, so a
        // crowded band stays readable; the bars still show every channel.
        var taken = new List<(double Left, double Right)>();

        foreach (var (band, left, right, text) in placed)
        {
            // Along the channel when the channel is wide enough for it, and
            // down it when it is not — which at a wide capture is most of
            // them, and is why these names went missing altogether before.
            if (everyNameFitsItsChannel)
            {
                double tx = left + 3;
                if (tx + text.Width > w) tx = w - text.Width - 1;
                if (tx < 0) tx = 0;
                if (!Claim(taken, tx, tx + text.Width)) continue;
                DrawWithHalo(dc, text, new Point(tx, 1), halo);
                continue;
            }

            if (text.Width > h - 6) continue; // nowhere to run it
            double vx = Math.Clamp(left + 2, 0, Math.Max(0, w - text.Height));
            if (!Claim(taken, vx, vx + text.Height)) continue;

            // Rotating a quarter turn maps the run of the text onto +y and its
            // height onto -x, so the origin is shifted right by that height to
            // put the name just inside the channel's left edge.
            using (dc.PushTransform(Matrix.CreateRotation(Math.PI / 2)
                                    * Matrix.CreateTranslation(vx + text.Height, 3)))
                DrawWithHalo(dc, text, new Point(0, 0), halo);
        }
    }

    /// <summary>Takes the horizontal room a name needs, or reports that
    /// something is already there.</summary>
    private static bool Claim(List<(double Left, double Right)> taken, double left, double right)
    {
        foreach (var (l, r) in taken)
            if (left < r && right > l) return false;
        taken.Add((left, right));
        return true;
    }

    private static void DrawWithHalo(DrawingContext dc, FormattedText text, Point origin, IPen halo)
    {
        if (text.BuildGeometry(origin) is { } geometry) dc.DrawGeometry(null, halo, geometry);
        dc.DrawText(text, origin);
    }
}
