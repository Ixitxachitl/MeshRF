// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using Xunit;

namespace MeshRF.Tests;

public class DisplayUnitsTests
{
    [Fact]
    public void FormatShortDistance_Metric_UsesMetresThenKilometres()
    {
        Assert.Equal("25 m", DisplayUnits.FormatShortDistance(25, UnitSystem.Metric));
        Assert.Equal("100 m", DisplayUnits.FormatShortDistance(100, UnitSystem.Metric));
        Assert.Equal("999 m", DisplayUnits.FormatShortDistance(999, UnitSystem.Metric));
        Assert.Equal("1 km", DisplayUnits.FormatShortDistance(1000, UnitSystem.Metric));
        Assert.Equal("2.5 km", DisplayUnits.FormatShortDistance(2500, UnitSystem.Metric));
    }

    [Fact]
    public void FormatShortDistance_Imperial_UsesFeetThenMiles()
    {
        // The bug this covers: an imperial user saw a geofence radius in metres.
        // 100 m is 328 ft, and must not come back as "100 m".
        var imperial = DisplayUnits.FormatShortDistance(100, UnitSystem.Imperial);
        Assert.Equal("328 ft", imperial);
        Assert.DoesNotContain(" m", imperial);

        // 5280 ft is one mile, so the switch happens at 1609.344 m.
        Assert.Equal("5279 ft", DisplayUnits.FormatShortDistance(1609, UnitSystem.Imperial));
        Assert.Equal("1 mi", DisplayUnits.FormatShortDistance(1609.344, UnitSystem.Imperial));
        Assert.Equal("2 mi", DisplayUnits.FormatShortDistance(3218.688, UnitSystem.Imperial));
    }

    [Fact]
    public void FormatShortDistance_KeepsExactValuesRatherThanBucketingThem()
    {
        // The position-precision options round to the nearest 10 m because they
        // describe a fuzzing radius. A configured geofence must not: reporting
        // 25 m as "30 m" would misstate the user's own setting.
        Assert.Equal("25 m", DisplayUnits.FormatShortDistance(25, UnitSystem.Metric));
        Assert.Equal("35 m", DisplayUnits.FormatShortDistance(35, UnitSystem.Metric));
        Assert.Equal("1 m", DisplayUnits.FormatShortDistance(1, UnitSystem.Metric));
    }

    [Theory]
    [InlineData(UnitSystem.Metric)]
    [InlineData(UnitSystem.Imperial)]
    public void FormatShortDistance_AlwaysCarriesAUnit(UnitSystem units)
    {
        foreach (var meters in new double[] { 1, 25, 100, 999, 1000, 1609.344, 5000, 23000 })
        {
            var text = DisplayUnits.FormatShortDistance(meters, units);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains(' ', text);   // "<number> <unit>", never a bare number
        }
    }

    [Fact]
    public void FormatShortDistance_UnitsDifferBetweenSystems()
    {
        // A radius must never render identically in both systems, which is
        // exactly how the metres-in-imperial bug looked.
        foreach (var meters in new double[] { 25, 100, 750, 2500 })
        {
            Assert.NotEqual(DisplayUnits.FormatShortDistance(meters, UnitSystem.Metric),
                            DisplayUnits.FormatShortDistance(meters, UnitSystem.Imperial));
        }
    }

    [Fact]
    public void ShortDistanceInput_RoundTripsThroughTheDisplayUnits()
    {
        // The radius field is typed in display units but sent as metres, so a
        // value has to survive the trip out to the box and back.
        foreach (var meters in new uint[] { 1, 25, 50, 100, 250, 1000, 5000 })
        {
            foreach (var units in new[] { UnitSystem.Metric, UnitSystem.Imperial })
            {
                var text = DisplayUnits.FormatShortDistanceInput(meters, units);
                var back = DisplayUnits.ParseShortDistanceInput(text, units);
                Assert.Equal(meters, back);
            }
        }
    }

    [Fact]
    public void ShortDistanceInput_ImperialShowsFeetAndReadsFeet()
    {
        Assert.Equal("328", DisplayUnits.FormatShortDistanceInput(100, UnitSystem.Imperial));
        Assert.Equal("100", DisplayUnits.FormatShortDistanceInput(100, UnitSystem.Metric));

        // 328 ft read back as metres, not taken as 328 m.
        Assert.Equal(100u, DisplayUnits.ParseShortDistanceInput("328", UnitSystem.Imperial));
        Assert.Equal(328u, DisplayUnits.ParseShortDistanceInput("328", UnitSystem.Metric));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("-5")]
    public void ParseShortDistanceInput_RejectsUnusableText(string? text)
    {
        // Null rather than 0, so a caller can tell malformed from a real zero.
        Assert.Null(DisplayUnits.ParseShortDistanceInput(text, UnitSystem.Metric));
        Assert.Null(DisplayUnits.ParseShortDistanceInput(text, UnitSystem.Imperial));
    }

    [Fact]
    public void ConvertShortDistanceText_KeepsTheRealWorldSizeAcrossAToggle()
    {
        // Switching units must not reread "100" in the new unit.
        Assert.Equal("328", DisplayUnits.ConvertShortDistanceText("100", UnitSystem.Metric, UnitSystem.Imperial));
        Assert.Equal("100", DisplayUnits.ConvertShortDistanceText("328", UnitSystem.Imperial, UnitSystem.Metric));
    }

    [Fact]
    public void ConvertShortDistanceText_LeavesUnusableOrUnchangedTextAlone()
    {
        Assert.Equal("100", DisplayUnits.ConvertShortDistanceText("100", UnitSystem.Metric, UnitSystem.Metric));
        Assert.Equal("abc", DisplayUnits.ConvertShortDistanceText("abc", UnitSystem.Metric, UnitSystem.Imperial));
        Assert.Equal("", DisplayUnits.ConvertShortDistanceText("", UnitSystem.Metric, UnitSystem.Imperial));
        Assert.Equal(string.Empty, DisplayUnits.ConvertShortDistanceText(null, UnitSystem.Metric, UnitSystem.Imperial));
    }

    [Fact]
    public void ShortDistanceUnitShort_NamesTheFieldsUnit()
    {
        Assert.Equal("m", DisplayUnits.ShortDistanceUnitShort(UnitSystem.Metric));
        Assert.Equal("ft", DisplayUnits.ShortDistanceUnitShort(UnitSystem.Imperial));
    }

    [Fact]
    public void FormatAltitude_FollowsTheSelectedSystem()
    {
        // The same tooltip showed a waypoint altitude in metres regardless of
        // the setting; this is the helper it should have been using.
        Assert.Equal("100 m", DisplayUnits.FormatAltitude(100, UnitSystem.Metric));
        Assert.Equal("328 ft", DisplayUnits.FormatAltitude(100, UnitSystem.Imperial));
    }
}
