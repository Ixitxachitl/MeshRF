// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using MeshRF;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Renaming a mesh brings everything that belongs to it along.
///
/// A mesh is known by its name and nothing else — its channel list, its tab,
/// its stored history and what every node records as where it was heard all
/// key off it. A rename that moved none of that would not rename a mesh; it
/// would abandon one and start another, leaving the channels the operator set
/// up under a name nothing answers to.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class MeshRenameTests(HeadlessAvalonia avalonia)
{
    private static void Settle()
    {
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }

    private static RadioViewModel Station()
    {
        var vm = new RadioViewModel
        {
            SelectedDevice = RadioDeviceKind.HackRf,
            SelectedRegion = Region.US,
            SelectedPreset = LoraPreset.MediumFast,
        };
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        return vm;
    }

    private static CustomListenerEdit Backyard(string name = "Backyard net") => new()
    {
        Name = name, Sf = 12, BwKhz = 250, Cr = 6, FreqMHz = 913.875, Enabled = true,
    };

    /// <summary>A channel set up on a mesh, so there is something to lose.
    /// Added the way the app adds one, so it exists as a tab as well as a row:
    /// writing the store alone would leave the screen not knowing about it.
    /// </summary>
    private static void AddChannel(RadioViewModel vm, string mesh, string channel)
    {
        var tab = vm.Host.AddChannel(mesh);
        tab.Config.Name = channel;
        vm.Host.UpsertChannelConfig(tab.Config);
    }

    [Fact]
    public void RenamingAListenerTakesItsChannelsWithIt() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var mine = Backyard();
        vm.SaveCustomListener(mine, null);
        Settle();

        AddChannel(vm, "Backyard net", "Shed");
        Settle();
        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(),
                        t => t.Config.Preset == "Backyard net" && t.Config.Name == "Shed");

        var renamed = mine.Clone();
        renamed.Name = "Garden net";
        vm.SaveCustomListener(renamed, mine);
        Settle();

        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(),
                        t => t.Config.Preset == "Garden net" && t.Config.Name == "Shed");
        Assert.DoesNotContain(vm.Tabs.OfType<ChannelTabViewModel>(),
                              t => t.Config.Preset == "Backyard net");
    }));

    /// <summary>And its nodes, which would otherwise be pointing at a mesh
    /// that no longer exists — out of reach, and spoken to on the primary.
    /// </summary>
    [Fact]
    public void RenamingAListenerTakesItsNodesWithIt() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var mine = Backyard();
        vm.SaveCustomListener(mine, null);
        Settle();

        vm.Host.RecordSighting(0x2222u, 1000, new SignalReading(-9f, -100f, true),
                               hopsAway: 1, viaMqtt: false,
                               new RxSource(1, "Backyard net", null, true, 913.875));
        Settle();
        Assert.Equal("Backyard net", vm.Nodes.Single(n => n.NodeNum == 0x2222u).HeardOnPreset);

        var renamed = mine.Clone();
        renamed.Name = "Garden net";
        vm.SaveCustomListener(renamed, mine);
        Settle();

        Assert.Equal("Garden net", vm.Nodes.Single(n => n.NodeNum == 0x2222u).HeardOnPreset);
    }));

    /// <summary>
    /// The primary especially: you set its channels up, then give the mesh a
    /// name, and they come with you. Without this, naming it would look like
    /// losing every channel you had.
    /// </summary>
    [Fact]
    public void NamingThePrimaryTakesItsChannelsWithIt() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();

        // Hand-set parameters, so the mesh has no name of its own to fall back
        // on and naming it means something.
        vm.OverrideSf = 12;
        vm.OverrideBwKhz = 250;
        vm.OverrideCr = 6;
        Settle();

        var was = vm.PrimaryListName;
        AddChannel(vm, was, "Ham");
        Settle();
        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(),
                        t => t.Config.Preset == was && t.Config.Name == "Ham");

        vm.CustomPrimaryName = "Home net";
        Settle();

        Assert.Equal("Home net", vm.PrimaryListName);
        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(),
                        t => t.Config.Preset == "Home net" && t.Config.Name == "Ham");
    }));

    /// <summary>Renaming it again keeps them, and so does changing your mind
    /// back — the channels follow the mesh however often it is called
    /// something else.</summary>
    [Fact]
    public void ThePrimarysChannelsFollowEveryRename() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.OverrideSf = 12;
        vm.OverrideBwKhz = 250;
        vm.OverrideCr = 6;
        Settle();

        AddChannel(vm, vm.PrimaryListName, "Ham");
        vm.CustomPrimaryName = "Home net";
        Settle();
        vm.CustomPrimaryName = "Shack";
        Settle();

        Assert.Equal("Shack", vm.PrimaryListName);
        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(),
                        t => t.Config.Preset == "Shack" && t.Config.Name == "Ham");
        Assert.DoesNotContain(vm.Tabs.OfType<ChannelTabViewModel>(),
                              t => t.Config.Preset == "Home net");
    }));

    /// <summary>
    /// Changing the preset is not a rename. The station has gone to a
    /// different mesh, and the channels it made on the old one belong to that
    /// one — moving them would be quietly taking them from it.
    /// </summary>
    [Fact]
    public void ChangingThePresetLeavesTheChannelsOnTheMeshTheyWereMadeOn() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        AddChannel(vm, vm.PrimaryListName, "Ham");
        Settle();
        Assert.Equal(nameof(LoraPreset.MediumFast), vm.PrimaryListName);

        vm.SelectedPreset = LoraPreset.LongTurbo;
        Settle();

        Assert.Equal(nameof(LoraPreset.LongTurbo), vm.PrimaryListName);
        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(),
                        t => t.Config.Preset == nameof(LoraPreset.MediumFast) && t.Config.Name == "Ham");
    }));

    /// <summary>
    /// A rename onto a mesh that already has channels is a merge, and there is
    /// no undoing one. It is declined instead, and nothing is lost: the old
    /// name still holds everything.
    /// </summary>
    [Fact]
    public void ARenameOntoAnOccupiedNameIsDeclined() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.SaveCustomListener(Backyard(), null);
        Settle();
        AddChannel(vm, "Backyard net", "Shed");

        // LongFast is a listener in its own right here, so it already has one.
        Assert.NotEmpty(vm.Host.ChannelsIn(nameof(LoraPreset.LongFast)));
        Assert.False(vm.Host.RenameMesh("Backyard net", nameof(LoraPreset.LongFast)));

        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(),
                        t => t.Config.Preset == "Backyard net" && t.Config.Name == "Shed");
    }));
}
