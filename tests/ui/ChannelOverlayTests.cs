// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The bars marking each demodulated channel across the waterfall. They are
/// drawn immediate-mode, so where they landed is only visible in the pixels.
/// </summary>
public class ChannelOverlayTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private const int Width = 800;
    private const int Height = 120;

    /// <summary>A 2.4 MHz capture centred on 910 MHz: 1 MHz is 333 px.</summary>
    private static ChannelOverlay Overlay(params ChannelBand[] bands) => new()
    {
        CenterFreqHz = 910_000_000,
        SpanHz = 2_400_000,
        Bands = new ObservableCollection<ChannelBand>(bands),
    };

    private static int ColumnFor(double freqHz) =>
        (int)Math.Round(Width * (0.5 + (freqHz - 910_000_000) / 2_400_000.0));

    /// <summary>Ink in a one-pixel-wide column, below the labels.</summary>
    private static int InkInColumn(Rendered r, int x) =>
        r.CountInk(new PixelRect(x, 40, 1, Height - 40), floor: 0x20);

    [Fact]
    public void EachChannelIsBarredAtItsOwnEdges() => Ui(() =>
    {
        // Two 250 kHz channels, 600 kHz either side of the centre.
        var overlay = Overlay(
            new ChannelBand("MediumFast", 910_600_000, 250_000, IsPrimary: true),
            new ChannelBand("LongFast", 909_400_000, 250_000, IsPrimary: false));
        var r = Rendered.Draw(overlay, Width, Height);

        foreach (double edge in new[] { 910_475_000d, 910_725_000d, 909_275_000d, 909_525_000d })
        {
            int x = ColumnFor(edge);
            // The stroke is a hairline, so allow it either side of the exact
            // column rather than demanding it land on one.
            int found = InkInColumn(r, x - 1) + InkInColumn(r, x) + InkInColumn(r, x + 1);
            Assert.True(found > 0, $"no bar at {edge / 1e6:0.000} MHz (column {x})");
        }

        // Between the two channels there is neither bar nor wash.
        int gap = ColumnFor(910_000_000);
        Assert.Equal(0, InkInColumn(r, gap));
    });

    [Fact]
    public void ThePrimaryIsDrawnApartFromTheRest() => Ui(() =>
    {
        var r = Rendered.Draw(Overlay(
            new ChannelBand("MediumFast", 910_600_000, 250_000, IsPrimary: true),
            new ChannelBand("LongFast", 909_400_000, 250_000, IsPrimary: false)), Width, Height);

        // The primary's wash is blue, the other's amber; each shows up only
        // inside its own channel.
        var primaryBand = new PixelRect(ColumnFor(910_500_000), 60, 40, 40);
        var otherBand = new PixelRect(ColumnFor(909_300_000), 60, 40, 40);
        Assert.True(BluerThanRed(r, primaryBand), "the primary's band should read blue");
        Assert.False(BluerThanRed(r, otherBand), "a secondary's band should not");
    });

    private static bool BluerThanRed(Rendered r, PixelRect area)
    {
        int bluer = r.Count(p => p.B > p.R + 4, area);
        int redder = r.Count(p => p.R > p.B + 4, area);
        return bluer > redder;
    }

    [Fact]
    public void AChannelOffTheEdgeOfTheCaptureIsNotDrawn() => Ui(() =>
    {
        // 906.875 is far outside a 2.4 MHz capture centred on 910.
        var r = Rendered.Draw(Overlay(
            new ChannelBand("LongFast", 906_875_000, 250_000, IsPrimary: false)), Width, Height);
        Assert.Equal(0, r.CountInk(new PixelRect(0, 0, Width, Height), floor: 0x20));
    });

    [Fact]
    public void ALabelIsDroppedWhereItWouldLandOnItsNeighboursName() => Ui(() =>
    {
        var apart = Rendered.Draw(Overlay(
            new ChannelBand("MediumFast", 910_600_000, 250_000, IsPrimary: true),
            new ChannelBand("LongFast", 909_400_000, 250_000, IsPrimary: false)), Width, Height);

        // Touching channels: the second name would overprint the first, so it
        // is left off and only one label is drawn.
        var crowded = Rendered.Draw(Overlay(
            new ChannelBand("MediumFast", 910_000_000, 250_000, IsPrimary: true),
            new ChannelBand("LongFast", 910_250_000, 250_000, IsPrimary: false)), Width, Height);

        var labelRow = new PixelRect(0, 0, Width, 14);
        Assert.True(apart.CountInk(labelRow, floor: 0x40) > crowded.CountInk(labelRow, floor: 0x40),
                    "the crowded pair should carry fewer label pixels than the spaced pair");
    });

    [Fact]
    public void NothingIsDrawnBeforeTheSpanIsKnown() => Ui(() =>
    {
        var overlay = Overlay(new ChannelBand("MediumFast", 910_000_000, 250_000, IsPrimary: true));
        overlay.SpanHz = 0;
        var r = Rendered.Draw(overlay, Width, Height);
        Assert.Equal(0, r.CountInk(new PixelRect(0, 0, Width, Height), floor: 0x20));
    });
}
