// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Waypoints;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// A request directed at one node — a traceroute, a position or telemetry
/// request, our NodeInfo sent back at them — is sealed with a channel key and
/// then put on the air with the settings that node was last heard on. The two
/// have to name the same mesh. Sealed with this station's own primary and
/// transmitted on a secondary listener's settings, the frame carries a channel
/// hash nobody on that mesh matches: it is received, found undecryptable, and
/// dropped. A traceroute wants no ack, so not even a NAK comes back — the
/// request simply goes unanswered, which is exactly what it looks like when a
/// node is out of range.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class RequestMeshTests(HeadlessAvalonia avalonia)
{
    private const uint Us = 0x11111111u;
    private const uint OnLongFast = 0x22222222u;
    private const uint OnOurOwnMesh = 0x33333333u;

    /// <summary>A station whose own channels are MediumFast's, listening on
    /// LongFast beside them.</summary>
    private static AvaloniaMeshRxHost Station(NodeStore nodes)
    {
        var host = new AvaloniaMeshRxHost(nodes, new ChannelStore(), new WaypointStore(),
                                          new MessageStore(), Us, [], null,
                                          primaryList: nameof(LoraPreset.MediumFast));
        host.IsPresetListening = name => name == nameof(LoraPreset.LongFast);
        host.EnsureChannelList(nameof(LoraPreset.LongFast));
        return host;
    }

    /// <summary>
    /// The whole point: a node heard on the LongFast listener is spoken to on
    /// LongFast's own default channel, not on this station's MediumFast
    /// primary.
    /// </summary>
    [Fact]
    public void ANodeHeardOnASecondaryIsSpokenToOnThatMeshsDefaultChannel() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.RecordSighting(OnLongFast, heardOnPreset: nameof(LoraPreset.LongFast));
        var host = Station(nodes);

        Assert.Equal(nameof(LoraPreset.LongFast), host.ListNameForNode(OnLongFast));

        var channel = host.ChannelForNode(OnLongFast, null);
        Assert.NotNull(channel);
        Assert.Equal(nameof(LoraPreset.LongFast), channel!.Preset);
        Assert.Equal(nameof(LoraPreset.LongFast), channel.Name);
    }));

    /// <summary>And a node heard on the primary keeps this station's own
    /// channels, which is where every request went before there was anywhere
    /// else for one to go.</summary>
    [Fact]
    public void ANodeHeardOnThePrimaryStaysOnThisStationsChannels() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.RecordSighting(OnOurOwnMesh, heardOnPreset: nameof(LoraPreset.MediumFast));
        var host = Station(nodes);

        Assert.Equal(nameof(LoraPreset.MediumFast), host.ListNameForNode(OnOurOwnMesh));
        Assert.Equal(nameof(LoraPreset.MediumFast), host.ChannelForNode(OnOurOwnMesh, null)?.Preset);
    }));

    /// <summary>
    /// A node last heard on a preset nothing is tuned to any more is answered
    /// on the primary. Its own mesh is not being transmitted on, so a request
    /// sealed for it could never be heard, let alone answered.
    /// </summary>
    [Fact]
    public void AMeshNothingIsListeningOnFallsBackToThePrimary() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.RecordSighting(OnLongFast, heardOnPreset: nameof(LoraPreset.LongFast));
        var host = Station(nodes);
        host.IsPresetListening = _ => false;

        Assert.Equal(nameof(LoraPreset.MediumFast), host.ListNameForNode(OnLongFast));
    }));

    /// <summary>A node nobody has ever heard over the air — one that only came
    /// in over MQTT, or was typed in — has no mesh of its own, and takes the
    /// primary's.</summary>
    [Fact]
    public void ANodeNeverHeardOnAirTakesThePrimary() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.RecordSighting(OnOurOwnMesh, seenViaMqtt: true);
        var host = Station(nodes);

        Assert.Equal(nameof(LoraPreset.MediumFast), host.ListNameForNode(OnOurOwnMesh));
    }));
    /// <summary>
    /// A node on a mesh nothing is listening for cannot be reached at all. The
    /// frequency its mesh lives on is not being transmitted on, so a request
    /// falls back to the primary's settings and goes out where the addressee
    /// is not.
    /// </summary>
    [Fact]
    public void AMeshWithNoListenerPutsItsNodesOutOfReach() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.RecordSighting(OnLongFast, heardOnPreset: nameof(LoraPreset.LongFast));
        nodes.RecordSighting(OnOurOwnMesh, heardOnPreset: nameof(LoraPreset.MediumFast));
        var host = Station(nodes);

        Assert.True(host.CanReachNode(OnLongFast));
        Assert.True(host.CanReachNode(OnOurOwnMesh));

        // The LongFast listener goes away. Its nodes are still in the list and
        // still named; nothing can be sent to them.
        host.IsPresetListening = _ => false;
        Assert.False(host.CanReachNode(OnLongFast));

        // The primary's own mesh is not a listener in that sense and never
        // goes away while the receiver is up.
        Assert.True(host.CanReachNode(OnOurOwnMesh));
    }));

    /// <summary>A node nobody has ever heard over the air is not ruled out —
    /// it names no mesh to be off, and the primary is the same guess every
    /// send to it has always made.</summary>
    [Fact]
    public void ANodeWithNoMeshOfItsOwnIsStillReachable() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.RecordSighting(OnOurOwnMesh, seenViaMqtt: true);
        var host = Station(nodes);
        host.IsPresetListening = _ => false;

        Assert.True(host.CanReachNode(OnOurOwnMesh));
    }));

    /// <summary>
    /// And the menu really greys out, rather than the command quietly
    /// swallowing the click: the node actions are bound with the node as their
    /// parameter, so CanExecute is asked about that node.
    /// </summary>
    [Fact]
    public void TheNodeActionsGreyOutForANodeOutOfReach() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using (var seed = new NodeStore())
        {
            seed.RecordSighting(OnLongFast, heardOnPreset: nameof(LoraPreset.LongFast));
            seed.RecordSighting(OnOurOwnMesh, heardOnPreset: nameof(LoraPreset.MediumFast));
        }

        using var vm = new RadioViewModel();
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;

        var far = new NodeRecord { NodeNum = OnLongFast };
        var near = new NodeRecord { NodeNum = OnOurOwnMesh };

        // Nothing is running, so no secondary listener is up: LongFast is off.
        Assert.False(vm.CanReachNode(far));
        Assert.True(vm.CanReachNode(near));

        // Built and opened the way the map's node menu is: a MenuItem only
        // asks its command whether it can run once it is in a tree, so a
        // detached one is no evidence either way.
        var blocked = new MenuItem
        {
            Header = "Traceroute",
            Command = vm.TracerouteCommand,
            CommandParameter = far,
        };
        var allowed = new MenuItem
        {
            Header = "Traceroute",
            Command = vm.TracerouteCommand,
            CommandParameter = near,
        };

        var menu = new ContextMenu();
        menu.Items.Add(blocked);
        menu.Items.Add(allowed);
        var target = new Border { Width = 100, Height = 40, ContextMenu = menu };
        var window = new Window { Width = 200, Height = 100, Content = target };
        window.Show();
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
        menu.Open(target);
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        Assert.False(blocked.IsEffectivelyEnabled);
        Assert.True(allowed.IsEffectivelyEnabled);

        menu.Close();
        window.Close();
    }));
}
