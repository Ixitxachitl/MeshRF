// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The textbook half of the link prediction: thermal noise, spreading loss and
/// the spreading factor's processing gain.
/// </summary>
public class LinkBudgetTests
{
    [Fact]
    public void TheNoiseFloorIsThermalNoiseAcrossTheBandwidthPlusTheFrontEnd()
    {
        // -174 + 10log10(250 kHz) + 6 dB.
        Assert.Equal(-114.02, LinkBudget.NoiseFloorDbm(250.0), 2);
    }

    [Fact]
    public void AWiderModemHearsMoreNoise()
    {
        // Doubling the bandwidth costs 3 dB, which is why the fast presets
        // trade range for airtime.
        double narrow = LinkBudget.NoiseFloorDbm(125.0);
        double wide = LinkBudget.NoiseFloorDbm(250.0);
        Assert.Equal(3.01, wide - narrow, 2);
    }

    [Fact]
    public void EachSpreadingFactorBuysAboutTwoAndAHalfDecibels()
    {
        for (int sf = 6; sf <= 12; sf++)
            Assert.Equal(-2.5, LinkBudget.RequiredSnrDb(sf) - LinkBudget.RequiredSnrDb(sf - 1), 6);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(13)]
    public void ASpreadingFactorOutsideTheLoraRangeIsRefused(int sf)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LinkBudget.RequiredSnrDb(sf));
    }

    [Fact]
    public void SensitivityIsTheNoiseFloorLessThePassGain()
    {
        // MediumFast, the preset this app is tested on: SF9 at 250 kHz.
        Assert.Equal(-126.52, LinkBudget.SensitivityDbm(9, 250.0), 2);
    }

    [Fact]
    public void FreeSpaceLossMatchesTheStandardFormula()
    {
        // 1 km at 915 MHz.
        Assert.Equal(91.68, LinkBudget.FreeSpacePathLossDb(1000.0, 915.0), 2);
    }

    [Fact]
    public void DoublingTheDistanceCostsSixDecibels()
    {
        double near = LinkBudget.FreeSpacePathLossDb(1000.0, 915.0);
        double far = LinkBudget.FreeSpacePathLossDb(2000.0, 915.0);
        Assert.Equal(6.02, far - near, 2);
    }

    [Fact]
    public void ExcessLossComesStraightOffTheReceivedPower()
    {
        double clear = LinkBudget.ReceivedPowerDbm(22, 2, 2, 100);
        double obstructed = LinkBudget.ReceivedPowerDbm(22, 2, 2, 100, excessLossDb: 15);
        Assert.Equal(-74, clear);
        Assert.Equal(clear - 15, obstructed);
    }

    [Fact]
    public void PredictedSnrIsTheSameQuantityARadioReports()
    {
        // A signal exactly at the noise floor is 0 dB SNR, which is what makes
        // a prediction comparable against a reported reading.
        double atFloor = LinkBudget.NoiseFloorDbm(250.0);
        Assert.Equal(0.0, LinkBudget.SnrDb(atFloor, 250.0), 6);
    }

    [Fact]
    public void MarginIsZeroExactlyAtSensitivity()
    {
        double sensitivity = LinkBudget.SensitivityDbm(11, 250.0);
        Assert.Equal(0.0, LinkBudget.MarginDb(sensitivity, 11, 250.0), 6);
        Assert.True(LinkBudget.MarginDb(sensitivity + 10, 11, 250.0) > 0);
        Assert.True(LinkBudget.MarginDb(sensitivity - 10, 11, 250.0) < 0);
    }

    [Fact]
    public void ASlowerPresetReachesFurtherThanAFasterOneAtTheSamePower()
    {
        // The whole reason LongFast outranges ShortFast: SF11/250k against
        // SF7/250k is 10 dB of extra margin over the same path.
        const double rxPower = -125.0;
        double longFast = LinkBudget.MarginDb(rxPower, 11, 250.0);
        double shortFast = LinkBudget.MarginDb(rxPower, 7, 250.0);
        Assert.Equal(10.0, longFast - shortFast, 6);
    }
}
