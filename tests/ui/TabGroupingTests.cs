// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Messages;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The tab strip holds one mesh at a time, picked from a dropdown: its
/// channels, then the conversations held over it. A wide capture can be
/// listening for a dozen presets, and all of their channels in one strip
/// would be unusable.
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

    private static void Show(RadioViewModel vm, string group) =>
        vm.SelectedTabGroupOption = vm.TabGroupOptions.Single(o => o.Group == group);

    [Fact]
    public void WithOneMeshThereIsNothingToPick() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = false;
        vm.RefreshMonitors();

        Assert.False(vm.HasSeveralMeshes);
        Assert.Equal(string.Empty, Assert.Single(vm.TabGroupOptions).Group);
        Assert.All(vm.Tabs, t => Assert.Equal(string.Empty, t.TabGroup));
        Assert.All(vm.Tabs, t => Assert.True(t.IsTabListed));
    }));

    [Fact]
    public void EachMeshBeingListenedForIsOfferedInThePicker() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        Assert.True(vm.HasSeveralMeshes);
        // The primary's mesh leads, and is what is shown to begin with.
        Assert.Equal(string.Empty, vm.TabGroupOptions[0].Group);
        Assert.Equal("Primary", vm.TabGroupOptions[0].Label);
        Assert.Equal(string.Empty, vm.SelectedTabGroupOption!.Group);
        Assert.Contains(vm.TabGroupOptions, o => o.Group == nameof(LoraPreset.LongFast));

        // Only the primary's tabs are on show.
        Assert.All(vm.Tabs.Where(t => t.IsTabListed), t => Assert.Equal(string.Empty, t.TabGroup));
        Assert.False(ChannelsOn(vm, nameof(LoraPreset.LongFast)).IsTabListed);
    }));

    [Fact]
    public void PickingAMeshShowsItsTabsAndOnlyThose() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        Show(vm, nameof(LoraPreset.LongFast));

        Assert.True(ChannelsOn(vm, nameof(LoraPreset.LongFast)).IsTabListed);
        Assert.All(vm.Tabs.Where(t => t.IsTabListed),
                   t => Assert.Equal(nameof(LoraPreset.LongFast), t.TabGroup));
        // Selection follows, rather than staying on a tab that is now hidden.
        Assert.True(vm.SelectedTab!.IsTabListed);
    }));

    [Fact]
    public void AChannelOnAMeshNotShownIsKeptAndComesBack() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        int longFastChannels = vm.Tabs.OfType<ChannelTabViewModel>()
            .Count(t => t.Config.Preset == nameof(LoraPreset.LongFast));
        Assert.True(longFastChannels > 0);

        // Stop listening for it: the mesh leaves the picker, its channels stay.
        vm.MonitorPresets.Single(r => r.Name == nameof(LoraPreset.LongFast)).Included = false;

        Assert.DoesNotContain(vm.TabGroupOptions, o => o.Group == nameof(LoraPreset.LongFast));
        Assert.Equal(longFastChannels, vm.Tabs.OfType<ChannelTabViewModel>()
            .Count(t => t.Config.Preset == nameof(LoraPreset.LongFast)));
        Assert.All(vm.Tabs.OfType<ChannelTabViewModel>().Where(t => t.Config.Preset == nameof(LoraPreset.LongFast)),
                   t => Assert.False(t.IsTabListed));

        vm.MonitorPresets.Single(r => r.Name == nameof(LoraPreset.LongFast)).Included = true;
        Assert.Contains(vm.TabGroupOptions, o => o.Group == nameof(LoraPreset.LongFast));
    }));

    [Fact]
    public void ShowingAMeshThatStopsBeingListenedForFallsBackToThePrimary() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        Show(vm, nameof(LoraPreset.LongFast));

        vm.MonitorPresets.Single(r => r.Name == nameof(LoraPreset.LongFast)).Included = false;

        Assert.Equal(string.Empty, vm.SelectedTabGroupOption!.Group);
        Assert.True(vm.SelectedTab!.IsTabListed);
    }));

    [Fact]
    public void AConversationBelongsToTheMeshItsPeerWasHeardOn() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

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
        // the primary mesh rather than a mesh of its own.
        Assert.Equal(string.Empty, onPrimary.TabGroup);
        Assert.Equal(nameof(LoraPreset.LongFast), onLongFast.TabGroup);
        Assert.True(onPrimary.IsTabListed);
        Assert.False(onLongFast.IsTabListed);

        // And on its own mesh it follows that mesh's channels.
        Show(vm, nameof(LoraPreset.LongFast));
        Assert.True(onLongFast.IsTabListed);
        Assert.True(vm.Tabs.IndexOf(onLongFast) > vm.Tabs.IndexOf(ChannelsOn(vm, nameof(LoraPreset.LongFast))));
    }));

    /// <summary>
    /// Opening a conversation adds a tab, and the tab strip is bound to that
    /// collection — so reordering from inside the change notification threw,
    /// which is what double-clicking a sender's name used to do.
    /// </summary>
    [Fact]
    public void OpeningAConversationWhileTheStripIsBoundDoesNotThrow() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        // A second listener on the collection is what makes it refuse a
        // modification during its own notification; the real strip is one.
        vm.Tabs.CollectionChanged += (_, _) => { };

        vm.MessageNodeCommand.Execute(new NodeRecord { NodeNum = 0x3333u });
        vm.MessageNodeCommand.Execute(new NodeRecord { NodeNum = 0x4444u });

        Assert.Contains(vm.Tabs.OfType<ConversationTabViewModel>(), t => t.NodeNum == 0x3333u);
        Assert.Contains(vm.Tabs.OfType<ConversationTabViewModel>(), t => t.NodeNum == 0x4444u);
    }));

    /// <summary>
    /// A peer whose node record we do not hold — never had a NodeInfo, or
    /// forgot it — still has to be answered on the mesh their message was
    /// read on. Otherwise the reply goes out on the primary, where they are
    /// not.
    /// </summary>
    [Fact]
    public void AnsweringAMessageFromAnUnknownNodeStaysOnThatMesh() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        // Reading a message on the LongFast mesh, from somebody we hold no
        // record for.
        Show(vm, nameof(LoraPreset.LongFast));
        vm.SelectedTab = ChannelsOn(vm, nameof(LoraPreset.LongFast));
        vm.MessageSenderCommand.Execute(new ChannelMessage { SenderNodeNum = 0x3840dd32u, Text = "test" });

        var convo = Assert.IsType<ConversationTabViewModel>(vm.SelectedTab);
        Assert.Equal(0x3840dd32u, convo.NodeNum);
        Assert.Equal(nameof(LoraPreset.LongFast), convo.TabGroup);
        Assert.True(convo.IsTabListed);
        Assert.Equal(nameof(LoraPreset.LongFast), vm.SelectedTabGroupOption!.Group);
    }));

    /// <summary>Where a peer is actually heard beats where they were read:
    /// a node that moves mesh takes its conversation with it.</summary>
    [Fact]
    public void APeerWeDoHoldARecordForIsSpokenToWhereTheyWereHeard() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        var store = new NodeStore();
        store.RecordSighting(0x5555u, heardOnPreset: nameof(LoraPreset.MediumFast), heardOnFreqMHz: 913.125);
        store.Dispose();

        // Opened while reading LongFast, but they are heard on the primary.
        Show(vm, nameof(LoraPreset.LongFast));
        vm.SelectedTab = ChannelsOn(vm, nameof(LoraPreset.LongFast));
        vm.MessageSenderCommand.Execute(new ChannelMessage { SenderNodeNum = 0x5555u, Text = "test" });

        var convo = vm.Tabs.OfType<ConversationTabViewModel>().Single(t => t.NodeNum == 0x5555u);
        Assert.Equal(string.Empty, convo.TabGroup);
    }));

    /// <summary>
    /// Somebody running MediumFast may perfectly well have named their own
    /// primary channel "LongFast", and the LongFast mesh has a channel of
    /// that name too. Replaying history by channel name alone filed one
    /// mesh's messages into the other's tab.
    /// </summary>
    [Fact]
    public void HistoryGoesBackToTheMeshItCameFromWhenTwoShareAChannelName() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        // A station on MediumFast whose own channel is called "LongFast",
        // alongside the LongFast mesh's channel of the same name.
        using (var channels = new ChannelStore())
        {
            channels.Upsert(new ChannelConfig
            {
                Preset = "", Index = 0, Name = "LongFast", Role = ChannelRole.Primary,
            });
            channels.Upsert(new ChannelConfig
            {
                Preset = nameof(LoraPreset.LongFast), Index = 0, Name = "LongFast", Role = ChannelRole.Primary,
            });
        }
        using (var messages = new MessageStore())
        {
            messages.Add(Broadcast(1, "", "mine"));
            messages.Add(Broadcast(2, nameof(LoraPreset.LongFast), "theirs"));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var vm = Station();

        var mine = vm.Tabs.OfType<ChannelTabViewModel>().Single(t => t.Config.Preset.Length == 0);
        var theirs = vm.Tabs.OfType<ChannelTabViewModel>()
            .Single(t => t.Config.Preset == nameof(LoraPreset.LongFast));

        Assert.Contains(mine.Messages, m => m.Text == "mine");
        Assert.DoesNotContain(mine.Messages, m => m.Text == "theirs");
        Assert.Contains(theirs.Messages, m => m.Text == "theirs");
        Assert.DoesNotContain(theirs.Messages, m => m.Text == "mine");
    }));

    private static MessageRecord Broadcast(uint packetId, string preset, string text) => new()
    {
        PacketId = packetId,
        FromNode = 0x3840dd32u,
        ToNode = 0xFFFFFFFFu,
        Channel = "LongFast",
        Preset = preset,
        PortNum = (int)MeshRF.Mesh.PortNum.TextMessage,
        Text = text,
        Decrypted = true,
        RxEpoch = 1000 + packetId,
    };

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

        Show(vm, nameof(LoraPreset.LongFast));
        vm.SelectedTab = ChannelsOn(vm, nameof(LoraPreset.LongFast));
        vm.AddChannelCommand.Execute(null);
        var onLongFast = (ChannelTabViewModel)vm.SelectedTab!;

        Assert.Equal(string.Empty, onPrimary.Config.Preset);
        Assert.Equal(nameof(LoraPreset.LongFast), onLongFast.Config.Preset);
        Assert.False(vm.CanReorderTabPair(onPrimary, onLongFast), "a channel may not cross to another mesh");
    }));
}
