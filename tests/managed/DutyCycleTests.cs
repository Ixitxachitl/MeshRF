// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class DutyCycleTests
{
    // The one region where the role changes the budget: firmware's
    // getEffectiveDutyCycle gives a router the 10% class and everyone else 2.5%.
    [Theory]
    [InlineData("Router", 10.0)]
    [InlineData("RouterLate", 10.0)]
    [InlineData("Client", 2.5)]
    [InlineData("ClientBase", 2.5)]
    [InlineData("Tracker", 2.5)]
    public void Eu866BudgetFollowsTheRole(string role, double expected) =>
        Assert.Equal(expected, DutyCycle.EffectivePercent(Region.EU_866, role));

    // Everywhere else the role is irrelevant — the region's own figure stands.
    [Theory]
    [InlineData(Region.EU_868, 10.0)]
    [InlineData(Region.EU_N_868, 10.0)]
    [InlineData(Region.EU_433, 10.0)]
    [InlineData(Region.UA_433, 10.0)]
    [InlineData(Region.TH, 10.0)]
    [InlineData(Region.US, 100.0)]
    [InlineData(Region.ANZ, 100.0)]
    [InlineData(Region.LORA_24, 100.0)]
    public void RegionBudgetIgnoresTheRole(Region region, double expected)
    {
        Assert.Equal(expected, DutyCycle.EffectivePercent(region, "Router"));
        Assert.Equal(expected, DutyCycle.EffectivePercent(region, "Client"));
    }

    [Fact]
    public void UnconstrainedRegionAllowsAnything() =>
        Assert.True(DutyCycle.IsTxAllowed(Region.US, "Client", airUtilTxPct: 99.0));

    [Fact]
    public void Eu866ClientStopsAtTwoAndAHalfPercent()
    {
        Assert.True(DutyCycle.IsTxAllowed(Region.EU_866, "Client", 2.4));
        Assert.False(DutyCycle.IsTxAllowed(Region.EU_866, "Client", 2.6));
    }

    // The same airtime a client has to stop at is well inside a router's class.
    [Fact]
    public void Eu866RouterKeepsGoingWhereAClientStops()
    {
        Assert.False(DutyCycle.IsTxAllowed(Region.EU_866, "Client", 5.0));
        Assert.True(DutyCycle.IsTxAllowed(Region.EU_866, "Router", 5.0));
    }

    // Background traffic gets half the allowance, leaving room for a message
    // the user actually sent.
    [Fact]
    public void PoliteTrafficGetsHalfTheBudget()
    {
        Assert.False(DutyCycle.IsTxAllowed(Region.EU_866, "Client", 1.5, polite: true));
        Assert.True(DutyCycle.IsTxAllowed(Region.EU_866, "Client", 1.5, polite: false));
    }

    [Fact]
    public void OverrideBypassesTheLimit() =>
        Assert.True(DutyCycle.IsTxAllowed(Region.EU_866, "Client", 90.0, overridden: true));

    [Fact]
    public void SilentMinutesIsZeroWhenUnderBudget() =>
        Assert.Equal(0, DutyCycle.SilentMinutes(1.0, 2.5));

    [Fact]
    public void SilentMinutesGrowsWithTheOverage()
    {
        int small = DutyCycle.SilentMinutes(3.0, 2.5);
        int large = DutyCycle.SilentMinutes(10.0, 2.5);
        Assert.InRange(small, 1, 60);
        Assert.InRange(large, 1, 60);
        Assert.True(large > small);
    }
}
