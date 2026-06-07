// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace MeshRF.Tests;

public class LoraPresetEnumTests
{
    // The native enum mrf::modem::Preset is indexed by the same integer
    // values; if anyone reorders one side they must update the other.
    [Theory]
    [InlineData(LoraPreset.ShortTurbo,   0)]
    [InlineData(LoraPreset.ShortFast,    1)]
    [InlineData(LoraPreset.ShortSlow,    2)]
    [InlineData(LoraPreset.MediumFast,   3)]
    [InlineData(LoraPreset.MediumSlow,   4)]
    [InlineData(LoraPreset.LongTurbo,    5)]
    [InlineData(LoraPreset.LongFast,     6)]
    [InlineData(LoraPreset.LongModerate, 7)]
    [InlineData(LoraPreset.LongSlow,     8)]
    [InlineData(LoraPreset.LiteFast,     9)]
    [InlineData(LoraPreset.LiteSlow,     10)]
    [InlineData(LoraPreset.NarrowFast,   11)]
    [InlineData(LoraPreset.NarrowSlow,   12)]
    [InlineData(LoraPreset.TinyFast,     13)]
    [InlineData(LoraPreset.TinySlow,     14)]
    public void EnumValuesMatchNativeOrder(LoraPreset preset, int expected)
    {
        Assert.Equal(expected, (int)preset);
    }
}
