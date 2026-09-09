// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// What the Scripts editor offers for a <c>mesh:</c>. The vocabulary is decided
/// in the engine, but the list of meshes can only come from a station that has
/// actually worked out what it is listening to.
/// </summary>
public class ScriptMeshCompletionTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    /// <param name="rateHz">Wide enough to reach other presets beside the
    /// primary.</param>
    private static RadioViewModel Station(LoraPreset primary, uint rateHz = 10_000_000u)
    {
        var vm = new RadioViewModel();
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = primary;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == rateHz);
        return vm;
    }

    /// <summary>With one mesh there is one thing to offer, and a script that
    /// names it is saying what it would have done anyway.</summary>
    [Fact]
    public void OnOneMeshOnlyThePrimaryIsOffered() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(LoraPreset.MediumFast);
        vm.MultiPresetEnabled = false;
        vm.RefreshMonitors();

        Assert.Equal([nameof(LoraPreset.MediumFast)],
                     vm.ScriptCompletions.Meshes.Select(m => m.Label));
    }));

    /// <summary>The primary leads, and every other mesh the operator has chosen
    /// follows — the same list the tab strip shows, because it is the same
    /// question.</summary>
    [Fact]
    public void EveryMeshThisStationIsOnIsOfferedPrimaryFirst() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(LoraPreset.MediumFast);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        var meshes = vm.ScriptCompletions.Meshes;

        Assert.Equal(nameof(LoraPreset.MediumFast), meshes[0].Label);
        Assert.Equal("this station's own mesh", meshes[0].Note);
        Assert.Contains(nameof(LoraPreset.LongFast), meshes.Select(m => m.Label));
        // The primary is not offered twice under the preset it is running.
        Assert.Single(meshes, m => m.Label == nameof(LoraPreset.MediumFast));
    }));
}
