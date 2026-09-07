// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// A beacon names a preset; this station has to say what that preset amounts
/// to here — whose channel list it is, and whether anything is tuned to it.
/// The answer cannot come from the tab list or from the running listeners:
/// with multi-preset listening off there are no listeners but the primary,
/// and the primary is a mesh like any other.
/// </summary>
public class BeaconMeshTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    /// <param name="rateHz">Wide enough to reach other presets when
    /// multi-preset listening is on.</param>
    private static RadioViewModel Station(LoraPreset primary, uint rateHz = 10_000_000u)
    {
        var vm = new RadioViewModel();
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        // Setting the preset snaps the toolbar onto its default slot, which is
        // the mesh a node running that preset unconfigured is on.
        vm.SelectedPreset = primary;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == rateHz);
        return vm;
    }

    /// <summary>
    /// The toolbar on MediumFast's own channel is on the MediumFast mesh, and
    /// a beacon advertising MediumFast is advertising the mesh already being
    /// read. Its channel belongs with the primary's, and there is nothing to
    /// go and enable.
    /// </summary>
    /// <remarks>
    /// This is what the first cut got wrong: it asked only about the listeners
    /// beside the primary, so a station listening on MediumFast and nothing
    /// else was told it was not listening for MediumFast.
    /// </remarks>
    [Fact]
    public void ThePrimarysOwnPresetIsThePrimarysMesh() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(LoraPreset.MediumFast);
        vm.MultiPresetEnabled = false;
        vm.RefreshMonitors();

        // The toolbar really is on MediumFast's default US channel.
        Assert.Equal(913.125, vm.CenterFreqMHz, 3);

        var (listName, note) = vm.MeshForPreset(LoraPreset.MediumFast, Region.UNSET);

        // The station's own mesh, named for the preset it was chosen as.
        Assert.Equal(nameof(LoraPreset.MediumFast), listName);
        Assert.Equal(vm.PrimaryListName, listName);
        Assert.Equal(string.Empty, note);
    }));

    /// <summary>And it stays the primary's mesh once other presets are being
    /// listened for beside it.</summary>
    [Fact]
    public void ThePrimarysOwnPresetStaysItsMeshWithOtherListenersUp() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(LoraPreset.MediumFast);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        var (listName, note) = vm.MeshForPreset(LoraPreset.MediumFast, Region.UNSET);

        // The station's own mesh, named for the preset it was chosen as.
        Assert.Equal(nameof(LoraPreset.MediumFast), listName);
        Assert.Equal(vm.PrimaryListName, listName);
        Assert.Equal(string.Empty, note);
    }));

    /// <summary>A preset nothing is tuned to gets a list of its own and a note
    /// saying what to do about it — naming the window by the label on the
    /// button that opens it.</summary>
    [Fact]
    public void APresetNothingIsTunedToSaysSo() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(LoraPreset.MediumFast);
        vm.MultiPresetEnabled = false;
        vm.RefreshMonitors();

        var (listName, note) = vm.MeshForPreset(LoraPreset.LongFast, Region.UNSET);

        Assert.Equal(nameof(LoraPreset.LongFast), listName);
        Assert.Contains("Not listening for LongFast", note);
        Assert.Contains("Listeners", note);
    }));

    /// <summary>Turning that preset's listener on makes it a mesh with a list
    /// of its own, and the note goes away.</summary>
    [Fact]
    public void APresetBeingListenedForOwnsItsOwnList() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(LoraPreset.MediumFast);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        var (listName, note) = vm.MeshForPreset(LoraPreset.LongFast, Region.UNSET);

        Assert.Equal(nameof(LoraPreset.LongFast), listName);
        Assert.Equal(string.Empty, note);
    }));

    /// <summary>
    /// Overriding the settings does not move the station's channel list. The
    /// operator picked MediumFast; that is still the mesh those channels are
    /// on, and a beacon advertising MediumFast is still advertising it.
    /// </summary>
    [Fact]
    public void AnOverriddenPrimaryKeepsThePresetItWasChosenAs() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(LoraPreset.MediumFast);
        vm.MultiPresetEnabled = false;
        vm.CenterFreqMHz = 906.875; // tuned off MediumFast's own slot by hand
        vm.RefreshMonitors();

        var (listName, note) = vm.MeshForPreset(LoraPreset.MediumFast, Region.UNSET);

        // The station's own mesh, named for the preset it was chosen as.
        Assert.Equal(nameof(LoraPreset.MediumFast), listName);
        Assert.Equal(vm.PrimaryListName, listName);
        Assert.Equal(string.Empty, note);
    }));

    /// <summary>Another region is another band, and the slot grid the beacon's
    /// frequency comes off is not this one.</summary>
    [Fact]
    public void AnotherRegionIsSaidPlainly() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(LoraPreset.MediumFast);
        vm.MultiPresetEnabled = false;
        vm.RefreshMonitors();

        var (listName, note) = vm.MeshForPreset(LoraPreset.MediumFast, Region.EU_868);

        Assert.Equal(nameof(LoraPreset.MediumFast), listName);
        Assert.Contains("EU_868", note);
        Assert.Contains("US", note);
    }));
}
