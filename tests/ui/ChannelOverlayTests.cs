// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The bars marking each demodulated channel across the waterfall. They are
/// drawn immediate-mode, so where they landed — and whether a name was drawn
/// at all — is only visible in the pixels.
/// </summary>
public class ChannelOverlayTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private const int Width = 800;
    private const int Height = 240;

    /// <summary>A 2.4 MHz capture centred on 910 MHz: 1 MHz is 333 px, so a
    /// 250 kHz channel is a comfortable 83 px.</summary>
    private static ChannelOverlay Wide(params ChannelBand[] bands) => Overlay(2_400_000, bands);

    /// <summary>A 16 MHz capture, where the same channel is 12 px. This is
    /// what listening on many presets at once actually looks like.</summary>
    private static ChannelOverlay Crowded(params ChannelBand[] bands) => Overlay(16_000_000, bands);

    private static ChannelOverlay Overlay(double spanHz, params ChannelBand[] bands) => new()
    {
        CenterFreqHz = 910_000_000,
        SpanHz = spanHz,
        Bands = new ObservableCollection<ChannelBand>(bands),
        Colormap = WaterfallColormap.Turbo,
    };

    private static int ColumnFor(double freqHz, double spanHz = 2_400_000) =>
        (int)Math.Round(Width * (0.5 + (freqHz - 910_000_000) / spanHz));

    /// <summary>Ink in a one-pixel column, below where the names are drawn.</summary>
    private static int InkInColumn(Rendered r, int x) =>
        r.CountInk(new PixelRect(x, 90, 1, Height - 90), floor: 0x20);

    /// <summary>Ink anywhere a name could have been drawn near a channel:
    /// along the top for a name that fits, down the channel for one that does
    /// not.</summary>
    private static int NameInkNear(Rendered r, int x) =>
        r.CountInk(new PixelRect(Math.Max(0, x - 2), 2, 16, 70), floor: 0x40);

    [Fact]
    public void EachChannelIsBarredAtItsOwnEdges() => Ui(() =>
    {
        var r = Rendered.Draw(Wide(
            new ChannelBand("MediumFast", 910_600_000, 250_000, IsPrimary: true),
            new ChannelBand("LongFast", 909_400_000, 250_000, IsPrimary: false)), Width, Height);

        foreach (double edge in new[] { 910_475_000d, 910_725_000d, 909_275_000d, 909_525_000d })
        {
            int x = ColumnFor(edge);
            // The stroke is a hairline, so allow it either side of the exact
            // column rather than demanding it land on one.
            int found = InkInColumn(r, x - 1) + InkInColumn(r, x) + InkInColumn(r, x + 1);
            Assert.True(found > 0, $"no bar at {edge / 1e6:0.000} MHz (column {x})");
        }

        // Between the two channels there is neither bar nor wash.
        Assert.Equal(0, InkInColumn(r, ColumnFor(910_000_000)));
    });

    /// <summary>
    /// The case that went wrong in the app: at a wide capture the channels are
    /// a few pixels each and only a fraction of a megahertz apart, and names
    /// laid along them collided and were dropped — every one of them. Run down
    /// the channel instead, a name needs about its own line height, so they
    /// fit.
    /// </summary>
    [Fact]
    public void NarrowChannelsStillGetTheirNames() => Ui(() =>
    {
        // 200 kHz apart in a 16 MHz span: 10 px between channels 12 px wide.
        var r = Rendered.Draw(Crowded(
            new ChannelBand("MediumFast", 910_000_000, 250_000, IsPrimary: true),
            new ChannelBand("NarrowFast", 910_200_000, 62_500, IsPrimary: false)), Width, Height);

        Assert.True(NameInkNear(r, ColumnFor(910_000_000, 16e6)) > 12,
                    "the first channel's name should be drawn down it");
        Assert.True(NameInkNear(r, ColumnFor(910_200_000, 16e6)) > 12,
                    "and so should its close neighbour's");
    });

    [Fact]
    public void AWideChannelKeepsItsNameAlongTheTop() => Ui(() =>
    {
        var r = Rendered.Draw(Wide(
            new ChannelBand("MediumFast", 910_000_000, 250_000, IsPrimary: true)), Width, Height);

        int left = ColumnFor(909_875_000);
        // Along the top of the channel, so the ink runs sideways from its left
        // edge rather than downward.
        Assert.True(r.CountInk(new PixelRect(left + 2, 1, 60, 12), floor: 0x40) > 12,
                    "a channel with room should carry its name along the top");
        Assert.Equal(0, r.CountInk(new PixelRect(left + 2, 40, 12, 60), floor: 0x40));
    });

    [Fact]
    public void ThePrimaryIsDrawnApartFromTheRest() => Ui(() =>
    {
        var r = Rendered.Draw(Wide(
            new ChannelBand("MediumFast", 910_600_000, 250_000, IsPrimary: true),
            new ChannelBand("LongFast", 909_400_000, 250_000, IsPrimary: false)), Width, Height);

        // On Turbo the primary is white and the rest magenta, so the primary's
        // band is the neutral one and the other leans off green.
        var primary = new PixelRect(ColumnFor(910_500_000), 120, 40, 60);
        var other = new PixelRect(ColumnFor(909_300_000), 120, 40, 60);
        Assert.True(r.Count(p => p.R > p.G + 6 && p.B > p.G + 6, other) >
                    r.Count(p => p.R > p.G + 6 && p.B > p.G + 6, primary),
                    "the secondary's wash should be the magenta one");
    });

    /// <summary>
    /// A fixed palette does not work, because the Meshtastic ramp starts at
    /// white: marks tuned for a dark waterfall vanished into it.
    /// </summary>
    [Fact]
    public void TheMarksChangeColourWithTheWaterfallRamp() => Ui(() =>
    {
        var band = new ChannelBand("MediumFast", 910_000_000, 250_000, IsPrimary: true);

        var overTurbo = Rendered.Draw(Wide(band), Width, Height);

        var light = Wide(band);
        light.Colormap = WaterfallColormap.Meshtastic;
        var overLight = Rendered.Draw(light, Width, Height);

        // Turbo runs blue to red and never reaches white, so the primary is
        // drawn white on it. The Meshtastic ramp starts white, so the same
        // mark would vanish and a dark magenta is used instead — magenta
        // being the one thing that ramp never produces.
        // Counted rather than demanded exactly, because subpixel text
        // antialiasing fringes a glyph edge with a stray coloured pixel or
        // two; a full-height bar is hundreds.
        Assert.True(overTurbo.CountNear("#FFFFFF", tolerance: 24) > 100, "Turbo should bar the primary in white");
        Assert.True(overLight.CountNear("#B000B0", tolerance: 24) > 100, "the light ramp should bar it in dark magenta");
        Assert.True(overTurbo.CountNear("#B000B0", tolerance: 24) < 10, "and Turbo should not use that magenta at all");
    });

    [Fact]
    public void AChannelOffTheEdgeOfTheCaptureIsNotDrawn() => Ui(() =>
    {
        // 906.875 is far outside a 2.4 MHz capture centred on 910.
        var r = Rendered.Draw(Wide(
            new ChannelBand("LongFast", 906_875_000, 250_000, IsPrimary: false)), Width, Height);
        Assert.Equal(0, r.CountInk(new PixelRect(0, 0, Width, Height), floor: 0x20));
    });

    [Fact]
    public void NothingIsDrawnBeforeTheSpanIsKnown() => Ui(() =>
    {
        var overlay = Wide(new ChannelBand("MediumFast", 910_000_000, 250_000, IsPrimary: true));
        overlay.SpanHz = 0;
        var r = Rendered.Draw(overlay, Width, Height);
        Assert.Equal(0, r.CountInk(new PixelRect(0, 0, Width, Height), floor: 0x20));
    });

    /// <summary>A channel a hundredth of the capture wide still has to be
    /// visible, not a hairline that disappears between pixels.</summary>
    [Fact]
    public void AVeryNarrowChannelIsStillDrawnWideEnoughToSee() => Ui(() =>
    {
        // 15.6 kHz in a 16 MHz span is under a pixel.
        var r = Rendered.Draw(Crowded(
            new ChannelBand("TinyFast", 910_000_000, 15_600, IsPrimary: false)), Width, Height);
        int x = ColumnFor(910_000_000, 16e6);
        int found = 0;
        for (int dx = -2; dx <= 2; dx++) found += InkInColumn(r, x + dx);
        Assert.True(found > 0, "a sub-pixel channel should still be marked");
    });
}
