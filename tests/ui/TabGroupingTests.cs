// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The tab strip is grouped by the mesh each tab is on: the primary's
/// channels and conversations first, then a group per preset being listened
/// for. A channel on a preset nobody is listening for is kept but not
/// offered, because there is nothing it could send or hear.
/// </summary>
public class TabGroupingTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private static RadioViewModel Station(uint rateHz = 10_000_000u)
    {
        var vm = new RadioViewModel();
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == rateHz);
        return vm;
    }

    private static ChannelTabViewModel ChannelsOn(RadioViewModel vm, string preset) =>
        vm.Tabs.OfType<ChannelTabViewModel>().First(t => t.Config.Preset == preset);

    [Fact]
    public void WithOneMeshEverythingSitsUnderPrimary() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = false;

        Assert.All(vm.Tabs, t => Assert.Equal(string.Empty, t.TabGroup));
        Assert.All(vm.Tabs, t => Assert.True(t.IsTabListed));
        // The label leads the run, on the first tab and nowhere else.
        Assert.Equal("Primary", vm.Tabs[0].TabGroupLabel);
        Assert.True(vm.Tabs[0].StartsTabGroup);
        Assert.All(vm.Tabs.Skip(1), t => Assert.Equal(string.Empty, t.TabGroupLabel));
    }));

    [Fact]
    public void APresetBeingListenedForGetsAGroupOfItsOwn() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        // Listening for LongFast seeds it a channel list, which becomes tabs.
        vm.RefreshMonitors();

        var longFast = ChannelsOn(vm, nameof(LoraPreset.LongFast));
        Assert.True(longFast.IsTabListed);
        Assert.Equal(nameof(LoraPreset.LongFast), longFast.TabGroup);

        // The primary's mesh comes first, and each group is labelled once.
        var listed = vm.Tabs.Where(t => t.IsTabListed).ToList();
        Assert.Equal(string.Empty, listed[0].TabGroup);
        Assert.Equal("Primary", listed[0].TabGroupLabel);
        var opener = Assert.Single(listed, t => t.TabGroupLabel == nameof(LoraPreset.LongFast));
        Assert.True(opener.StartsTabGroup);
        Assert.True(listed.IndexOf(opener) > 0, "the primary's mesh leads the strip");
    }));

    [Fact]
    public void AChannelOnAMeshNobodyListensForIsKeptButNotOffered() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        // Give the LongFast list a channel of the user's own.
        var added = vm.Tabs.OfType<ChannelTabViewModel>().Count(t => t.Config.Preset == nameof(LoraPreset.LongFast));
        Assert.True(added > 0);

        // Stop listening for it: the tabs go, the channels do not.
        var longFastRow = vm.MonitorPresets.Single(r => r.Name == nameof(LoraPreset.LongFast));
        longFastRow.Included = false;

        Assert.All(vm.Tabs.OfType<ChannelTabViewModel>().Where(t => t.Config.Preset == nameof(LoraPreset.LongFast)),
                   t => Assert.False(t.IsTabListed));
        // Its channels are still on file, and no group is left labelled for a
        // mesh with nothing shown on it.
        Assert.NotEmpty(vm.Tabs.OfType<ChannelTabViewModel>().Where(t => t.Config.Preset == nameof(LoraPreset.LongFast)));
        Assert.DoesNotContain(vm.Tabs.Where(t => t.IsTabListed), t => t.TabGroup == nameof(LoraPreset.LongFast));
        Assert.DoesNotContain(vm.Tabs, t => t.TabGroupLabel == nameof(LoraPreset.LongFast));

        // Listen again and they come back, with what was in them.
        longFastRow.Included = true;
        Assert.All(vm.Tabs.OfType<ChannelTabViewModel>().Where(t => t.Config.Preset == nameof(LoraPreset.LongFast)),
                   t => Assert.True(t.IsTabListed));
    }));

    [Fact]
    public void AConversationSitsUnderTheMeshItsPeerWasHeardOn() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        // One peer heard on the primary, one on LongFast.
        var store = new NodeStore();
        store.RecordSighting(0x1111u, heardOnPreset: nameof(LoraPreset.MediumFast), heardOnFreqMHz: 913.125);
        store.RecordSighting(0x2222u, heardOnPreset: nameof(LoraPreset.LongFast), heardOnFreqMHz: 906.875);
        store.Dispose();

        vm.MessageNodeCommand.Execute(new NodeRecord { NodeNum = 0x1111u });
        vm.MessageNodeCommand.Execute(new NodeRecord { NodeNum = 0x2222u });
        vm.RefreshMonitors();

        var onPrimary = vm.Tabs.OfType<ConversationTabViewModel>().Single(t => t.NodeNum == 0x1111u);
        var onLongFast = vm.Tabs.OfType<ConversationTabViewModel>().Single(t => t.NodeNum == 0x2222u);

        // The primary's peer was heard on the toolbar's own preset, which is
        // the primary mesh rather than a group of its own.
        Assert.Equal(string.Empty, onPrimary.TabGroup);
        Assert.Equal(nameof(LoraPreset.LongFast), onLongFast.TabGroup);

        // And it sits with that mesh's channels, after them.
        int channel = vm.Tabs.IndexOf(ChannelsOn(vm, nameof(LoraPreset.LongFast)));
        Assert.True(vm.Tabs.IndexOf(onLongFast) > channel,
                    "a conversation follows the channels of the mesh it is held over");
        Assert.True(vm.Tabs.IndexOf(onPrimary) < channel,
                    "and the primary's conversations come before the next mesh starts");
    }));

    [Fact]
    public void SelectionLeavesATabThatHasJustBeenTakenAway() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        var longFast = ChannelsOn(vm, nameof(LoraPreset.LongFast));
        vm.SelectedTab = longFast;

        vm.MonitorPresets.Single(r => r.Name == nameof(LoraPreset.LongFast)).Included = false;

        Assert.False(longFast.IsTabListed);
        Assert.NotSame(longFast, vm.SelectedTab);
        Assert.True(vm.SelectedTab!.IsTabListed);
    }));

    /// <summary>A drag may not carry a tab onto another mesh: which mesh a
    /// channel is on is what its key and its frequency mean.</summary>
    [Fact]
    public void TabsDoNotReorderAcrossMeshes() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        // A secondary on each mesh, since the role-primary channel of a list
        // is pinned and refuses a drag on its own account.
        vm.SelectedTab = vm.Tabs.OfType<ChannelTabViewModel>().First(t => t.Config.Preset.Length == 0);
        vm.AddChannelCommand.Execute(null);
        var onPrimary = (ChannelTabViewModel)vm.SelectedTab!;

        vm.SelectedTab = ChannelsOn(vm, nameof(LoraPreset.LongFast));
        vm.AddChannelCommand.Execute(null);
        var onLongFast = (ChannelTabViewModel)vm.SelectedTab!;

        Assert.Equal(string.Empty, onPrimary.Config.Preset);
        Assert.Equal(nameof(LoraPreset.LongFast), onLongFast.Config.Preset);
        Assert.False(vm.CanReorderTabPair(onPrimary, onLongFast), "a channel may not cross to another mesh");
    }));
}
