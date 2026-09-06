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
    public void SettingsAreNamedForWhatTheyAmountTo()
    {
        Assert.Equal("LongFast", HeardOn.Name(LoraPreset.LongFast));
        Assert.Equal(HeardOn.Custom, HeardOn.Name(null));
    }

    /// <summary>
    /// Settings typed in by hand are not a different mesh for being typed.
    /// SF11 at 250 kHz is LongFast whichever way it was arrived at, and
    /// naming it "Custom" hid which mesh the station was on.
    /// </summary>
    [Fact]
    public void HandSetParametersAreNamedForThePresetTheyMatch()
    {
        Assert.True(LoraParamsHelper.TryPresetFor(11, 250.0, wideLora: false, out var longFast));
        Assert.Equal(LoraPreset.LongFast, longFast);
        Assert.True(LoraParamsHelper.TryPresetFor(9, 250.0, wideLora: false, out var mediumFast));
        Assert.Equal(LoraPreset.MediumFast, mediumFast);
        // The narrow presets are unscaled, so they match in a wide region too.
        Assert.True(LoraParamsHelper.TryPresetFor(7, 62.5, wideLora: true, out var narrowFast));
        Assert.Equal(LoraPreset.NarrowFast, narrowFast);
        // And the scaled ones match their scaled bandwidth there.
        Assert.True(LoraParamsHelper.TryPresetFor(11, 812.5, wideLora: true, out var wideLongFast));
        Assert.Equal(LoraPreset.LongFast, wideLongFast);

        // Settings no preset uses stay unnamed rather than being rounded to
        // the nearest one.
        Assert.False(LoraParamsHelper.TryPresetFor(12, 250.0, wideLora: false, out _));
        Assert.False(LoraParamsHelper.TryPresetFor(11, 200.0, wideLora: false, out _));

        Assert.Equal("LongFast", RxSource.Primary(longFast, isCustom: true, 906.875).PresetName);
        Assert.Equal(HeardOn.Custom, RxSource.Primary(null, isCustom: true, 906.875).PresetName);
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

        var custom = RxSource.Primary(null, isCustom: true, 913.125);
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
