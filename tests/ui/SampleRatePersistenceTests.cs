// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The receive sample rate is a radio setting like the preset or the slot,
/// and it decides how much spectrum the capture covers — which, with several
/// presets being listened for, decides which of them are heard at all. So it
/// has to come back on the next launch.
/// </summary>
public class SampleRatePersistenceTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private static void Flush() => AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));

    [Fact]
    public void TheChosenRateComesBackOnTheNextLaunch() => Ui(() => TempDataDirectory.With(() =>
    {
        using (var first = new RadioViewModel())
        {
            first.SelectedDevice = RadioDeviceKind.HackRf;
            first.SelectedRxSampleRate = first.SampleRateOptions.Single(o => o.Hz == 8_000_000u);
        }
        Flush();

        using var second = new RadioViewModel();
        Assert.Equal(RadioDeviceKind.HackRf, second.SelectedDevice);
        Assert.NotNull(second.SelectedRxSampleRate);
        Assert.Equal(8_000_000u, second.SelectedRxSampleRate!.Hz);
        Assert.Equal(8_000_000.0, second.SpectrumSpanHz);
    }));

    /// <summary>Each device kind remembers its own, so moving between them
    /// does not cost either one its setting.</summary>
    [Fact]
    public void EachDeviceKeepsItsOwnRate() => Ui(() => TempDataDirectory.With(() =>
    {
        using (var first = new RadioViewModel())
        {
            first.SelectedDevice = RadioDeviceKind.HackRf;
            first.SelectedRxSampleRate = first.SampleRateOptions.Single(o => o.Hz == 12_500_000u);
            first.SelectedDevice = RadioDeviceKind.RtlSdr;
            first.SelectedRxSampleRate = first.SampleRateOptions.Single(o => o.Hz == 1_920_000u);
        }
        Flush();

        using (var second = new RadioViewModel())
        {
            Assert.Equal(RadioDeviceKind.RtlSdr, second.SelectedDevice);
            Assert.Equal(1_920_000u, second.SelectedRxSampleRate!.Hz);
            // Back to the HackRF and its own rate is still there.
            second.SelectedDevice = RadioDeviceKind.HackRf;
            Assert.Equal(12_500_000u, second.SelectedRxSampleRate!.Hz);
        }
        Flush();
    }));

    /// <summary>
    /// A rate saved before the HackRF ceiling came down to what the native
    /// core will actually run is no longer offered. It settles on the highest
    /// that is, and stays there rather than asking again every launch.
    /// </summary>
    [Fact]
    public void ARateNoLongerOfferedSettlesOnTheNearestAndStays() => Ui(() => TempDataDirectory.With(() =>
    {
        var seeded = AppSettings.Load();
        seeded.RxDeviceKind = nameof(RadioDeviceKind.HackRf);
        seeded.HackRfRxSampleRateHz = 20_000_000u;
        seeded.RxSampleRateHz = 20_000_000u;
        seeded.Save();
        Flush();

        using (var first = new RadioViewModel())
        {
            Assert.Equal(16_000_000u, first.SelectedRxSampleRate!.Hz);
            // Any ordinary change writes the settings back.
            first.SelectedSlot = first.Slots.Last();
        }
        Flush();

        Assert.Equal(16_000_000u, AppSettings.Load().HackRfRxSampleRateHz);

        using var second = new RadioViewModel();
        Assert.Equal(16_000_000u, second.SelectedRxSampleRate!.Hz);
    }));
}
