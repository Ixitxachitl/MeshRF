// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// What a reception measured, and the unit it measured it in.
///
/// The two were separated once: a level off an SDR is dBFS, relative to a
/// converter's full scale and moved by whatever analogue gain sits in front of
/// it, and it was stored, displayed and published to a broker as dBm. Keeping
/// the unit on the value is what stops that happening again.
/// </summary>
public sealed class SignalReadingTests
{
    [Fact]
    public void ASoftwareDemodulatorReportsALevelRelativeToFullScale()
    {
        var r = SignalReading.FromPreambleLine(
            "  preamble: SF11 BW250k cfo=+0.1k peak=21.4dB snr=-11.7dB rssi=-42.5dBFS");

        Assert.Equal(-11.7f, r.SnrDb!.Value, 3);
        Assert.Equal(-42.5f, r.Rssi!.Value, 3);
        Assert.False(r.RssiIsDbm);
        Assert.Equal("dBFS", r.RssiUnit);
        Assert.Equal("-43 dBFS", r.RssiText);
    }

    /// <summary>A packet radio measures real received power, and that is the
    /// one path where this figure has an absolute reference.</summary>
    [Fact]
    public void APacketRadioReportsRealPower()
    {
        var r = SignalReading.FromPreambleLine(
            "  preamble: SF11 BW250k hardware snr=-8.0dB rssi=-113.0dBm");

        Assert.Equal(-8.0f, r.SnrDb!.Value, 3);
        Assert.Equal(-113.0f, r.Rssi!.Value, 3);
        Assert.True(r.RssiIsDbm);
        Assert.Equal("dBm", r.RssiUnit);
        Assert.Equal("-113 dBm", r.RssiText);
    }

    /// <summary>The "dB" inside "dBFS" is not an SNR. Read loosely, the level
    /// would be picked up as the ratio and both would be wrong.</summary>
    [Fact]
    public void TheUnitSuffixIsNotMistakenForTheRatio()
    {
        var r = SignalReading.FromPreambleLine("  preamble: SF9 BW250k rssi=-40.0dBFS");

        Assert.Null(r.SnrDb);
        Assert.Equal(-40.0f, r.Rssi!.Value, 3);
        Assert.False(r.RssiIsDbm);
    }

    /// <summary>A line from a core built before these were reported measures
    /// nothing, and says so rather than reading as a perfect zero.</summary>
    [Fact]
    public void AnOlderLineMeasuresNothing()
    {
        var r = SignalReading.FromPreambleLine("  preamble: SF11 BW250k cfo=+0.1k peak=21.4dB");

        Assert.Null(r.SnrDb);
        Assert.Null(r.Rssi);
        Assert.Null(r.RssiText);
    }

    /// <summary>A reception nothing measured — one handed in over MQTT, which
    /// reached us over a wire and was never on any air.</summary>
    [Fact]
    public void NoneCarriesNothing()
    {
        Assert.Null(SignalReading.None.SnrDb);
        Assert.Null(SignalReading.None.Rssi);
        Assert.Null(SignalReading.None.RssiText);
    }
}
