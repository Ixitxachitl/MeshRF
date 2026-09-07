// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// A beacon arrives as a chat bubble like any other message, with the mesh it
/// advertises attached to it: what the channel is, and a button to take it.
/// The offer is markup inside the bubble template, so the visual tree is the
/// only proof it is drawn at all — and that it stays out of the way of every
/// other message, which is most of them.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class BeaconBubbleTests(HeadlessAvalonia avalonia)
{
    private static readonly byte[] SharedKey =
    {
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00,
    };

    private static MainWindow Open()
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        Settle();
        return window;
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The offer's button as actually drawn, told apart by its
    /// caption because it is the only button inside a bubble.</summary>
    private static IReadOnlyList<Button> AddButtons(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
              .Where(b => b.IsEffectivelyVisible && b.Content as string == "Add channel")
              .ToList();

    /// <summary>Every line of text on screen, for asking what the offer
    /// actually says.</summary>
    private static IReadOnlyList<string> DrawnText(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
              .Where(t => t.IsEffectivelyVisible)
              .Select(t => t.Text ?? string.Empty)
              .ToList();

    private static BeaconOffer Offer(string listName, string channelName,
                                     IEnumerable<ChannelConfig> held, string note = "")
    {
        var beacon = new MeshBeacon
        {
            Message = "Ranger net, Tuesdays 19:00",
            HasChannel = true,
            ChannelName = channelName,
            ChannelPsk = SharedKey,
            Preset = LoraPreset.MediumFast,
            Region = Region.US,
        };
        return BeaconOffer.For(beacon, listName, listName.Length == 0 ? "Primary" : listName, held, note);
    }

    private static ChannelMessage Bubble(BeaconOffer? offer) => new()
    {
        FromId = "!aabbccdd",
        SenderNodeNum = 0xAABBCCDDu,
        Text = "Ranger net, Tuesdays 19:00",
        Offer = offer,
    };

    [Fact]
    public void AnOfferedChannelIsDrawnWithItsButton() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = Open();
        var vm = (RadioViewModel)window.DataContext!;

        var tab = vm.Tabs.OfType<ChannelTabViewModel>().First();
        vm.SelectedTab = tab;
        Settle();

        var offer = Offer(nameof(LoraPreset.MediumFast), "Ranger", []);
        Assert.True(offer.CanAdd);
        tab.Messages.Add(Bubble(offer));
        Settle();

        Assert.Contains(DrawnText(window), t => t.Contains("“Ranger”") && t.Contains("preset MediumFast"));

        var button = Assert.Single(AddButtons(window));
        Assert.Same(offer, button.CommandParameter);

        window.Close();
    }));

    /// <summary>Every other message is the common case, and none of them has a
    /// mesh to offer — so none of them grows a card for one.</summary>
    [Fact]
    public void AnOrdinaryMessageCarriesNoOffer() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = Open();
        var vm = (RadioViewModel)window.DataContext!;

        var tab = vm.Tabs.OfType<ChannelTabViewModel>().First();
        vm.SelectedTab = tab;
        tab.Messages.Add(Bubble(offer: null));
        Settle();

        Assert.Empty(AddButtons(window));

        window.Close();
    }));

    /// <summary>A channel already held on that mesh is not offered again: the
    /// card says so instead of showing a button that would duplicate it.</summary>
    [Fact]
    public void AChannelAlreadyHeldShowsTheReasonInsteadOfAButton() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = Open();
        var vm = (RadioViewModel)window.DataContext!;

        var tab = vm.Tabs.OfType<ChannelTabViewModel>().First();
        vm.SelectedTab = tab;

        var held = new ChannelConfig { Name = "Ranger", Psk = SharedKey, Role = ChannelRole.Secondary };
        var offer = Offer(string.Empty, "Ranger", new[] { held });
        Assert.False(offer.CanAdd);
        tab.Messages.Add(Bubble(offer));
        Settle();

        Assert.Empty(AddButtons(window));
        Assert.Contains("Already on Primary.", DrawnText(window));

        window.Close();
    }));

    /// <summary>Pressing the button adds the channel to the mesh it was
    /// advertised for, takes the strip there, and leaves the offer saying what
    /// it did rather than offering the same thing twice.</summary>
    [Fact]
    public void TakingTheOfferAddsTheChannelAndShowsIt() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = Open();
        var vm = (RadioViewModel)window.DataContext!;

        var tab = vm.Tabs.OfType<ChannelTabViewModel>().First();
        vm.SelectedTab = tab;

        var offer = Offer(nameof(LoraPreset.MediumFast), "Ranger", []);
        tab.Messages.Add(Bubble(offer));
        Settle();

        var button = Assert.Single(AddButtons(window));
        button.Command!.Execute(button.CommandParameter);
        Settle();

        var added = Assert.Single(vm.Tabs.OfType<ChannelTabViewModel>(),
            t => t.Config.Preset == nameof(LoraPreset.MediumFast) && t.Config.Name == "Ranger");
        Assert.Equal(SharedKey, added.Config.Psk);
        // The key came off the air, so it starts out withholding position.
        Assert.Equal(0, added.Config.PositionPrecision);
        // Its mesh is on show, or the tab just added would be nowhere.
        Assert.Same(added, vm.SelectedTab);
        Assert.True(added.IsTabListed);

        Assert.False(offer.CanAdd);
        Assert.Equal("Added to MediumFast.", offer.StatusText);
        Assert.Empty(AddButtons(window));

        window.Close();
    }));

    /// <summary>
    /// A mesh gets exactly one Primary, as firmware requires. The mesh the
    /// toolbar sits on is described by two lists — the primary's and the
    /// preset's — so an offer filed against the emptier of them must not
    /// promote itself alongside the primary already there.
    /// </summary>
    [Fact]
    public void AnOfferNeverBecomesASecondPrimaryOnAMeshThatHasOne() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = Open();
        var vm = (RadioViewModel)window.DataContext!;
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        Settle();

        // The toolbar sits on MediumFast's own channel, so that preset's list
        // and the primary's are two descriptions of one mesh.
        var offer = Offer(nameof(LoraPreset.MediumFast), "Ranger", []);
        Assert.True(offer.CanAdd);

        vm.AcceptBeaconOfferCommand.Execute(offer);
        Settle();

        var added = Assert.Single(vm.Tabs.OfType<ChannelTabViewModel>(), t => t.Config.Name == "Ranger");
        Assert.Equal(ChannelRole.Secondary, added.Config.Role);

        // And the mesh still has exactly the one Primary it started with.
        var mesh = vm.Tabs.OfType<ChannelTabViewModel>()
            .Where(t => t.Config.Preset.Length == 0 || t.Config.Preset == nameof(LoraPreset.MediumFast))
            .ToList();
        Assert.Single(mesh, t => t.Config.Role == ChannelRole.Primary);

        window.Close();
    }));

    /// <summary>
    /// A channel already on the primary's list is not offered again just
    /// because the beacon named the preset the primary happens to be running.
    /// Adding it anyway is what produced two "Alta" tabs.
    /// </summary>
    [Fact]
    public void AChannelOnThePrimarysListIsFoundWhenThePresetNamesThatMesh() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = Open();
        var vm = (RadioViewModel)window.DataContext!;
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        Settle();

        // Take the offer once — this is the operator adding Alta by hand.
        var first = Offer(nameof(LoraPreset.MediumFast), "Alta", []);
        vm.AcceptBeaconOfferCommand.Execute(first);
        Settle();

        var held = vm.Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).ToList();
        Assert.Single(held, c => c.Name == "Alta");

        // The next beacon advertising the same mesh must recognise it, wherever
        // in that mesh's two lists it landed.
        var again = Offer(nameof(LoraPreset.MediumFast), "Alta",
                          held.Where(c => c.Preset.Length == 0 || c.Preset == nameof(LoraPreset.MediumFast)));

        Assert.False(again.CanAdd);
        Assert.Contains("Already on", again.StatusText);

        window.Close();
    }));

    /// <summary>The strip names the channel, not the mesh: one mesh is on show
    /// at a time and the row above it says which.</summary>
    [Fact]
    public void ATabIsNamedForItsChannelAlone() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = Open();
        var vm = (RadioViewModel)window.DataContext!;
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        Settle();

        var onAPreset = vm.Tabs.OfType<ChannelTabViewModel>()
            .First(t => t.Config.Preset == nameof(LoraPreset.LongFast));

        Assert.DoesNotContain("·", onAPreset.TabHeader);
        Assert.StartsWith("LongFast", onAPreset.TabHeader);

        window.Close();
    }));
}
