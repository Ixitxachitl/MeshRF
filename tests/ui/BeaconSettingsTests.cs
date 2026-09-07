// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF;
using MeshRF.AvaloniaApp;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The beacon dialog: what this station advertises to strangers, and where.
/// The destination list is the part that has no default — with nothing on it
/// nothing is transmitted, so the dialog has to say so rather than looking
/// armed and doing nothing.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class BeaconSettingsTests(HeadlessAvalonia avalonia)
{
    private static void Settle()
    {
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Owner, RadioViewModel Vm) Station()
    {
        var owner = new MainWindow { Width = 1280, Height = 900 };
        owner.Show();
        Settle();
        var vm = (RadioViewModel)owner.DataContext!;
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        Settle();
        return (owner, vm);
    }

    [Fact]
    public void TheDialogDrawsItsControls() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();
        var dialog = BeaconSettingsWindow.Open(owner, vm);
        Settle();

        var named = dialog.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Name is not null).Select(c => c.Name!).ToList();
        Assert.Contains("EnabledCheck", named);
        Assert.Contains("MessageBox", named);
        Assert.Contains("OfferCheck", named);
        Assert.Contains("OfferCombo", named);

        // The emoji picker acts on the message box, so it sits beside it.
        Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "😀");

        // Sending off the schedule lives on the Quick send bar now, beside the
        // other things this station broadcasts on demand.
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(),
                              b => b.Content as string == "Send now");

        // Every stored channel is on offer in the pickers.
        Assert.NotEmpty(vm.BeaconChannelOptions);
        Assert.Contains(vm.BeaconChannelOptions, o => o.Preset == vm.PrimaryListName);

        dialog.Close();
        owner.Close();
    }));

    /// <summary>
    /// The Quick send bar carries the off-schedule beacon. It goes through a
    /// click handler rather than binding the command directly, because that
    /// handler is where the confirmation lives: every other button on the bar
    /// asks which channel to send on, and a beacon — which carries its own
    /// destination list — would otherwise go on the air from a single press
    /// with nothing asked.
    /// </summary>
    [Fact]
    public void TheQuickSendBarCanBeaconOnDemand() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, _) = Station();

        // Scoped to the Quick send bar: the settings button one row up is also
        // called Beacon, and opens the window rather than transmitting.
        // The panel that holds the label directly, not every ancestor that
        // happens to contain it.
        var bar = owner.GetVisualDescendants().OfType<Panel>()
            .First(p => p.Children.OfType<TextBlock>().Any(t => t.Text == "Quick send"));
        var button = Assert.Single(bar.GetVisualDescendants().OfType<Button>(),
            b => b.Content as string == "Beacon");

        // No command bound: it goes through the handler that confirms first.
        Assert.Null(button.Command);

        owner.Close();
    }));

    /// <summary>
    /// Nothing configured is nothing to confirm: the command runs and says
    /// why, rather than putting a prompt in front of a send that would not
    /// have happened.
    /// </summary>
    [Fact]
    public void AnUnconfiguredBeaconSaysSoRatherThanPrompting() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();
        vm.RefreshBeaconChannelOptions();

        Assert.Empty(vm.BeaconTargets);
        Assert.False(vm.BeaconHasAnythingToSay);

        vm.SendBeaconNowCommand.Execute(null);
        Settle();
        Assert.Contains("No channels to beacon on", vm.StatusText);

        owner.Close();
    }));

    /// <summary>
    /// Sending on demand is an extra beacon, not the scheduled one arriving
    /// early: it leaves the next one where it was. Otherwise pressing the
    /// button would push the schedule out by an hour every time, and pressing
    /// it often enough would hold the schedule off forever.
    /// </summary>
    [Fact]
    public void SendingOnDemandDoesNotMoveTheSchedule() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();
        vm.RefreshBeaconChannelOptions();
        vm.BeaconMessage = "come and find us";
        vm.BeaconTargetToAdd = vm.BeaconChannelOptions.First(o => o.Preset == vm.PrimaryListName);
        vm.AddBeaconTargetCommand.Execute(null);

        // A station that beaconed an hour ago, so the next one is due shortly.
        var settings = new MeshRF.Mesh.BeaconSettings
        {
            Enabled = true,
            Message = vm.BeaconMessage,
            IntervalSeconds = 7200,
            Targets = vm.BeaconTargets.Select(r => r.Target).ToList(),
            LastSentUtc = DateTime.UtcNow.AddMinutes(-60),
        };
        var before = vm.BeaconLastSentUtc;

        // Nothing reaches the air with no receiver running, which is exactly
        // the case that matters: even a send that went out must not move it.
        var sent = vm.BroadcastBeaconAsync(settings).GetAwaiter().GetResult();
        Settle();

        Assert.Equal(0, sent);
        Assert.Equal(before, vm.BeaconLastSentUtc);

        owner.Close();
    }));

    /// <summary>
    /// A beacon this station sends shows up on the channel it went out on, and
    /// comes back with that channel's history.
    /// </summary>
    /// <remarks>
    /// The router drops our own transmission as isFromUs, exactly as it does
    /// an outgoing text message, so what went on the air is echoed here or
    /// appears nowhere. It went out with no trace but a log line until this
    /// existed.
    /// </remarks>
    [Fact]
    public void ABeaconWeSendShowsOnItsChannelAndSurvivesARestart() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        string channelName;
        {
            var (owner, vm) = Station();
            vm.RefreshBeaconChannelOptions();

            var channel = vm.Tabs.OfType<ChannelTabViewModel>()
                .First(t => t.Config.Preset == vm.PrimaryListName).Config;
            channelName = channel.Name;

            var payload = MeshRF.Mesh.MeshEncoder.BuildBeaconPayload("Ranger net, Tuesdays 19:00");
            vm.ShowOutgoingBeacon(channel, payload, packetId: 0x1234u);
            Settle();

            var tab = vm.Tabs.OfType<ChannelTabViewModel>().First(t => t.Config.Name == channelName);
            var bubble = Assert.Single(tab.Messages, m => m.Text == "Ranger net, Tuesdays 19:00");
            Assert.True(bubble.IsOutgoing);

            owner.Close();
        }
        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var reopened = new RadioViewModel();
        var replayed = reopened.Tabs.OfType<ChannelTabViewModel>().First(t => t.Config.Name == channelName);
        var restored = Assert.Single(replayed.Messages, m => m.Text == "Ranger net, Tuesdays 19:00");
        Assert.True(restored.IsOutgoing);
    }));

    /// <summary>Turning it on with no destination transmits nothing, and the
    /// summary says that rather than leaving it to be discovered.</summary>
    [Fact]
    public void WithNoChannelOnTheListNothingIsBroadcast() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();
        vm.RefreshBeaconChannelOptions();

        vm.BeaconEnabled = true;
        vm.BeaconMessage = "come and find us";
        Settle();

        Assert.Empty(vm.BeaconTargets);
        Assert.Contains("No channels to broadcast on", vm.BeaconSummary);

        owner.Close();
    }));

    [Fact]
    public void AddingAChannelPutsItOnTheListAndAddingItTwiceDoesNot() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();
        vm.RefreshBeaconChannelOptions();
        vm.BeaconEnabled = true;
        vm.BeaconMessage = "come and find us";

        vm.BeaconTargetToAdd = vm.BeaconChannelOptions.First(o => o.Preset == vm.PrimaryListName);
        vm.AddBeaconTargetCommand.Execute(null);
        Settle();

        var row = Assert.Single(vm.BeaconTargets);
        Assert.Equal(vm.PrimaryListName, row.Target.Preset);
        Assert.Equal(string.Empty, row.Note);
        // A test station has no node id or transmitter, so the summary stops
        // at that rather than reaching the schedule — but it no longer says
        // there is nowhere to send.
        Assert.DoesNotContain("No channels to broadcast on", vm.BeaconSummary);

        // One copy per channel: a repeat is the same transmission.
        vm.AddBeaconTargetCommand.Execute(null);
        Settle();
        Assert.Single(vm.BeaconTargets);
        Assert.Contains("already on the list", vm.StatusText);

        vm.RemoveBeaconTargetCommand.Execute(row);
        Settle();
        Assert.Empty(vm.BeaconTargets);

        owner.Close();
    }));

    /// <summary>The interval is floored at firmware's hour whatever is typed,
    /// including something that is not a number at all.</summary>
    [Theory]
    [InlineData("60", 3600)]
    [InlineData("5", 3600)]
    [InlineData("0", 3600)]
    [InlineData("", 3600)]
    [InlineData("not a number", 3600)]
    [InlineData("180", 10800)]
    public void TheIntervalIsFlooredAtAnHour(string typed, int expectedSeconds) =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();
        vm.BeaconIntervalMinutesText = typed;
        Assert.Equal(expectedSeconds, vm.BeaconIntervalSeconds);
        owner.Close();
    }));

    /// <summary>The message budget is bytes of UTF-8, not characters, because
    /// that is what firmware's cap counts.</summary>
    [Fact]
    public void TheMessageBudgetIsCountedInBytes() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();

        vm.BeaconMessage = "plain";
        Assert.Equal(5, vm.BeaconMessageBytes);
        Assert.False(vm.BeaconMessageTooLong);

        // Four bytes each, so 26 of them is 104: over a cap that counting
        // characters would have called 26.
        vm.BeaconMessage = string.Concat(Enumerable.Repeat("🍄", 26));
        Assert.Equal(104, vm.BeaconMessageBytes);
        Assert.True(vm.BeaconMessageTooLong);
        Assert.Contains("104 / 100", vm.BeaconMessageCount);

        owner.Close();
    }));

    /// <summary>Everything survives a restart, including the destination list —
    /// and the clock, so relaunching does not put a beacon on the air every
    /// time the app opens.</summary>
    [Fact]
    public void TheWholeConfigurationComesBackOnTheNextLaunch() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using (var first = Station().Vm)
        {
            first.RefreshBeaconChannelOptions();
            first.BeaconEnabled = true;
            first.BeaconMessage = "Ranger net, Tuesdays 19:00";
            first.BeaconIntervalMinutesText = "120";
            first.BeaconOfferChannelEnabled = true;
            first.BeaconOfferChannel = first.BeaconChannelOptions.First();
            first.BeaconTargetToAdd = first.BeaconChannelOptions.First(o => o.Preset == first.PrimaryListName);
            first.AddBeaconTargetCommand.Execute(null);
        }
        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));

        using var reopened = new RadioViewModel();
        reopened.RefreshBeaconChannelOptions();

        Assert.True(reopened.BeaconEnabled);
        Assert.Equal("Ranger net, Tuesdays 19:00", reopened.BeaconMessage);
        Assert.Equal(7200, reopened.BeaconIntervalSeconds);
        Assert.True(reopened.BeaconOfferChannelEnabled);
        Assert.NotNull(reopened.BeaconOfferChannel);
        Assert.Single(reopened.BeaconTargets);
    }));

    /// <summary>A destination whose channel has since been deleted says so on
    /// its row, rather than failing silently once an hour.</summary>
    [Fact]
    public void ADestinationWhoseChannelIsGoneSaysSo() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();
        vm.RefreshBeaconChannelOptions();

        var added = vm.BeaconChannelOptions.First(o => o.Preset == vm.PrimaryListName);
        vm.BeaconTargetToAdd = added;
        vm.AddBeaconTargetCommand.Execute(null);
        Assert.Equal(string.Empty, Assert.Single(vm.BeaconTargets).Note);

        // Point the row at a channel index nothing holds, as deleting the
        // channel would leave it.
        vm.BeaconTargets[0].Target.ChannelIndex = 99;
        vm.RefreshBeaconChannelOptions();
        Settle();

        Assert.Contains("gone", vm.BeaconTargets[0].Note);
        Assert.True(vm.BeaconTargets[0].HasNote);

        owner.Close();
    }));

    /// <summary>
    /// The offer names the mesh its channel is on. Setting the preset and the
    /// region separately would let them drift out of step with the channel and
    /// advertise a mesh that does not exist.
    /// </summary>
    [Fact]
    public void TheOfferedChannelDecidesThePresetAndRegionAdvertised() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, vm) = Station();
        vm.RefreshBeaconChannelOptions();

        var onLongFast = vm.BeaconChannelOptions.FirstOrDefault(o => o.Preset == nameof(LoraPreset.LongFast));
        Assert.NotNull(onLongFast);

        vm.BeaconOfferChannelEnabled = true;
        vm.BeaconOfferChannel = onLongFast;
        Settle();

        // Nothing to assert on the wire from here — that is the encoder's
        // test — but the mesh named has to be the channel's own.
        Assert.Equal(nameof(LoraPreset.LongFast), vm.BeaconOfferChannel!.Preset);

        owner.Close();
    }));
}
