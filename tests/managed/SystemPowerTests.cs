// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class SystemPowerTests
{
    [Fact]
    public void OnMains_ReportsTheExternallyPoweredSentinel()
    {
        // Meshtastic reads anything above 100 as externally powered, so a
        // desktop says so rather than claiming a full battery.
        Assert.Equal(101, SystemPower.BatteryLevelForWire(acOnline: true, batteryPct: null));
        Assert.Equal(101, SystemPower.BatteryLevelForWire(acOnline: true, batteryPct: 64));
    }

    [Fact]
    public void OnBattery_ReportsTheCharge()
    {
        Assert.Equal(64, SystemPower.BatteryLevelForWire(acOnline: false, batteryPct: 64));
        // A flat battery is a real reading, not a missing one.
        Assert.Equal(0, SystemPower.BatteryLevelForWire(acOnline: false, batteryPct: 0));
    }

    [Fact]
    public void AnUnreadableBatteryFallsBackToWhatWeLastReported()
    {
        // Otherwise a momentary read failure looks like the battery went flat,
        // which is the one wrong answer worth avoiding.
        Assert.Equal(72, SystemPower.BatteryLevelForWire(acOnline: false, batteryPct: null, previousPct: 72));
        // A fresh reading always wins over the remembered one.
        Assert.Equal(30, SystemPower.BatteryLevelForWire(acOnline: false, batteryPct: 30, previousPct: 72));
    }

    [Fact]
    public void WithNothingKnownAtAll_ReportsZero()
    {
        Assert.Equal(0, SystemPower.BatteryLevelForWire(acOnline: false, batteryPct: null, previousPct: null));
    }

    [Fact]
    public void ReadingTheHostNeverThrowsAndAnswersCoherently()
    {
        // Runs against whatever machine the tests are on — a CI runner with no
        // battery included. The contract is that it always answers.
        SystemPower.Read(out bool ac, out byte? pct, out float? volts);

        if (pct is byte p) Assert.InRange(p, (byte)0, (byte)100);
        if (volts is float v) Assert.InRange(v, 1.0f, 20.0f);
        // A machine with no battery must read as mains, not as a flat node.
        if (pct is null) Assert.True(ac || volts is null);
    }
}
