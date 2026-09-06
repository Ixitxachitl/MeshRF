// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The "heard on" strings are shared by the node column, its filter, the log
/// tags and the JSON feed, so they have to agree.
/// </summary>
public class HeardOnTests
{
    [Fact]
    public void APresetIsNamedAndACustomPrimaryIsCustom()
    {
        Assert.Equal("LongFast", HeardOn.Name(LoraPreset.LongFast, isCustom: false));
        Assert.Equal(HeardOn.Custom, HeardOn.Name(null, isCustom: true));
        Assert.Equal(HeardOn.Custom, HeardOn.Name(LoraPreset.LongFast, isCustom: true));
    }

    [Fact]
    public void TheTagCarriesTheFrequencyToThreePlacesInvariantly()
    {
        Assert.Equal("LongFast 906.875", HeardOn.Tag("LongFast", 906.875));
        Assert.Equal("MediumFast 913.125", HeardOn.Tag("MediumFast", 913.125));
    }

    [Fact]
    public void ThePrimarySourceIsListenerZero()
    {
        var s = RxSource.Primary(LoraPreset.MediumFast, isCustom: false, 913.125);
        Assert.True(s.IsPrimary);
        Assert.Equal("MediumFast", s.PresetName);
        Assert.Equal("MediumFast 913.125", s.Tag);
        Assert.False(s.FromDownlink);

        var custom = RxSource.Primary(LoraPreset.MediumFast, isCustom: true, 913.125);
        Assert.Null(custom.Preset);
        Assert.Equal(HeardOn.Custom, custom.PresetName);
    }

    [Fact]
    public void ASecondaryNamesItsPresetAndKeepsItsIndex()
    {
        var s = new RxSource(3, LoraPreset.LongFast, false, 906.875);
        Assert.False(s.IsPrimary);
        Assert.Equal(3, s.Listener);
        Assert.Equal("LongFast 906.875", s.Tag);
    }

    [Fact]
    public void ATargetKnowsItsBandwidth()
    {
        var preset = TxTarget.ForPreset(LoraPreset.LongFast, 906_875_000, 1);
        Assert.False(preset.IsCustom);
        Assert.Equal(250_000u, preset.EffectiveBwHz);
        Assert.Equal(906.875, preset.FreqMHz, 6);

        var custom = TxTarget.ForParams(10, 125_000, 8, 913_125_000, 0);
        Assert.True(custom.IsCustom);
        Assert.Equal(125_000u, custom.EffectiveBwHz);
    }
}
