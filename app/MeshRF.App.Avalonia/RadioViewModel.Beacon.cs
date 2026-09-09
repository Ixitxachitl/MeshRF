// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Channels;
using MeshRF.Mesh;

namespace MeshRF.AvaloniaApp;

/// <summary>One stored channel as the beacon dialog's pickers name it.</summary>
public sealed class BeaconChannelOption
{
    public required string Preset { get; init; }
    public required int Index { get; init; }
    public required string Label { get; init; }

    /// <summary>True when this option and that target are the same channel.</summary>
    public bool Is(BeaconTarget target) =>
        string.Equals(target.Preset, Preset, StringComparison.Ordinal) && target.ChannelIndex == Index;

    public override string ToString() => Label;
}

/// <summary>A destination on the list, with the channel resolved for display.</summary>
public sealed partial class BeaconTargetRow : ObservableObject
{
    public required BeaconTarget Target { get; init; }
    public required string Label { get; init; }

    /// <summary>Says what stops this destination working, or nothing when it
    /// works. A channel deleted after it was added leaves a row naming a mesh
    /// nothing can be sealed for.</summary>
    [ObservableProperty] private string _note = string.Empty;

    public bool HasNote => Note.Length > 0;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    public override string ToString() => Label;
}

public partial class RadioViewModel
{
    /// <summary>
    /// What this station advertises to strangers, and where. Held apart from
    /// the settings object so the dialog edits observable properties, and
    /// written back through <see cref="SaveBeaconSettings"/>.
    /// </summary>
    [ObservableProperty] private bool _beaconEnabled;
    [ObservableProperty] private string _beaconMessage = string.Empty;
    [ObservableProperty] private bool _beaconOfferChannelEnabled;
    [ObservableProperty] private BeaconChannelOption? _beaconOfferChannel;
    [ObservableProperty] private BeaconChannelOption? _beaconTargetToAdd;

    /// <summary>Every stored channel, for both pickers. Rebuilt whenever the
    /// dialog opens, since channels come and go while it is closed.</summary>
    public ObservableCollection<BeaconChannelOption> BeaconChannelOptions { get; } = new();

    /// <summary>The meshes a beacon goes out on, one copy each.</summary>
    public ObservableCollection<BeaconTargetRow> BeaconTargets { get; } = new();

    private DateTime? _lastBeaconSentUtc;
    private int _beaconTickInFlight;

    /// <summary>
    /// When the schedule last came round, or null while it never has. Only the
    /// schedule moves it: sending on demand is not the schedule running, so it
    /// leaves the next one where it was.
    /// </summary>
    public DateTime? BeaconLastSentUtc => _lastBeaconSentUtc;

    /// <summary>
    /// The offered channel as stored, which outlives the picker's option
    /// objects.
    /// </summary>
    /// <remarks>
    /// The settings are loaded before there are any options to select from,
    /// and the first save runs before them too — so building the settings from
    /// the selection alone wrote a null over the saved channel on every
    /// launch, and it came back unset. The reference is what is kept; the
    /// selection is a view of it.
    /// </remarks>
    private string _beaconOfferPreset = string.Empty;
    private int _beaconOfferIndex;

    /// <summary>The interval as typed, in minutes — an hour minimum is a long
    /// time to count in seconds.</summary>
    [ObservableProperty] private string _beaconIntervalMinutesText =
        (BeaconPolicy.MinIntervalSeconds / 60).ToString(CultureInfo.InvariantCulture);

    /// <summary>The interval actually used: what was typed, floored at
    /// firmware's minimum.</summary>
    public int BeaconIntervalSeconds =>
        int.TryParse(BeaconIntervalMinutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            ? BeaconPolicy.ClampInterval(minutes * 60)
            : BeaconPolicy.MinIntervalSeconds;

    public int BeaconMessageBytes => BeaconPolicy.MessageBytes(BeaconMessage);

    /// <summary>How much of the message budget is spent, so an over-long one
    /// is visible before it is silently cut.</summary>
    public string BeaconMessageCount => $"{BeaconMessageBytes} / {BeaconPolicy.MaxMessageBytes} bytes";

    public bool BeaconMessageTooLong => !BeaconPolicy.MessageFits(BeaconMessage);

    /// <summary>Whether the beacon would carry anything at all: words, an
    /// offered channel, or both.</summary>
    public bool BeaconHasAnythingToSay => BeaconPolicy.HasAnythingToSay(BuildBeaconSettings());

    /// <summary>
    /// What the beacon will actually do, in one line. Every way it can be on
    /// and still send nothing gets said here rather than discovered by
    /// watching an empty log.
    /// </summary>
    public string BeaconSummary
    {
        get
        {
            if (!BeaconEnabled) return "Off. Nothing is broadcast.";
            if (BeaconTargets.Count == 0)
                return "No channels to broadcast on — add one below and nothing goes out until you do.";
            if (!BeaconOfferChannelEnabled && string.IsNullOrWhiteSpace(BeaconMessage))
                return "Nothing to say: write a message, offer a channel, or both.";
            if (!CanTransmit) return "Set your node ID and a TX-capable device before this can transmit.";

            var every = BeaconIntervalSeconds / 60;
            var where = BeaconTargets.Count == 1 ? BeaconTargets[0].Label : $"{BeaconTargets.Count} channels";
            var next = _lastBeaconSentUtc is null
                ? "the next tick"
                : BeaconPolicy.NextDueUtc(BuildBeaconSettings(), DateTime.UtcNow).ToLocalTime()
                    .ToString("t", CultureInfo.CurrentCulture);
            return $"On: every {every} min on {where}. Next at {next}.";
        }
    }

    /// <summary>Fires the beacon now, whatever the schedule says.</summary>
    [RelayCommand]
    private async Task SendBeaconNow()
    {
        SaveBeaconSettings();
        var settings = BuildBeaconSettings();
        if (settings.Targets.Count == 0) { StatusText = "No channels to beacon on."; return; }
        if (!BeaconPolicy.HasAnythingToSay(settings)) { StatusText = "The beacon has nothing to say."; return; }
        if (!CanTransmit) { StatusText = "Set your node ID and a TX-capable device first."; return; }

        int sent = await BroadcastBeaconAsync(settings);
        StatusText = sent > 0 ? $"Sent {sent} beacon(s)." : "Beacon transmit failed.";
    }

    /// <summary>Rebuilds both pickers from the channels that exist now, and
    /// re-checks the destinations against them.</summary>
    public void RefreshBeaconChannelOptions()
    {
        var options = AllChannelConfigs()
            .OrderBy(c => c.Preset == _rxHost.PrimaryListName ? 0 : 1)
            .ThenBy(c => c.Preset, StringComparer.Ordinal)
            .ThenBy(c => c.Index)
            .Select(c => new BeaconChannelOption
            {
                Preset = c.Preset,
                Index = c.Index,
                Label = ChannelLabel(c),
            })
            .ToList();

        BeaconChannelOptions.Clear();
        foreach (var option in options) BeaconChannelOptions.Add(option);

        // Keep the offered channel selected across a rebuild, matched on the
        // stored reference rather than on the option objects, which are new
        // each time.
        BeaconOfferChannel = options.FirstOrDefault(o =>
            o.Preset == _beaconOfferPreset && o.Index == _beaconOfferIndex);
        BeaconTargetToAdd ??= BeaconChannelOptions.FirstOrDefault();

        foreach (var row in BeaconTargets) row.Note = TargetNote(row.Target);
        OnPropertyChanged(nameof(BeaconSummary));
    }

    /// <summary>"MediumFast · Alta", or the mesh alone for its unnamed
    /// channel — which is how a Meshtastic channel with no name is spelled.</summary>
    private static string ChannelLabel(ChannelConfig channel) =>
        channel.Name.Length > 0 ? $"{channel.Preset} · {channel.Name}" : $"{channel.Preset} · (unnamed)";

    private string ChannelLabel(BeaconTarget target) =>
        ResolveBeaconChannel(target) is { } channel
            ? ChannelLabel(channel)
            : $"{target.Preset} · channel {target.ChannelIndex}";

    private ChannelConfig? ResolveBeaconChannel(BeaconTarget target) =>
        AllChannelConfigs().FirstOrDefault(target.Matches);

    /// <summary>What stops a destination working, or nothing when it works.</summary>
    private string TargetNote(BeaconTarget target)
    {
        var channel = ResolveBeaconChannel(target);
        if (channel is null) return "That channel is gone — remove this destination.";
        if (channel.IsDisabled) return "That channel is disabled, so nothing can go out on it.";
        if (target.Preset != _rxHost.PrimaryListName && !Enum.TryParse<LoraPreset>(target.Preset, out _))
            return $"No preset called {target.Preset} — nothing can be tuned to it.";
        return string.Empty;
    }

    [RelayCommand]
    private void AddBeaconTarget()
    {
        if (BeaconTargetToAdd is not { } option) return;
        var target = new BeaconTarget { Preset = option.Preset, ChannelIndex = option.Index };
        // One copy per channel: adding the same one twice is one transmission
        // either way, so it is not added twice.
        if (BeaconTargets.Any(r => r.Target.SameAs(target))) { StatusText = $"{option.Label} is already on the list."; return; }

        BeaconTargets.Add(new BeaconTargetRow
        {
            Target = target,
            Label = option.Label,
            Note = TargetNote(target),
        });
        SaveBeaconSettings();
    }

    [RelayCommand]
    private void RemoveBeaconTarget(BeaconTargetRow? row)
    {
        if (row is null) return;
        BeaconTargets.Remove(row);
        SaveBeaconSettings();
    }

    partial void OnBeaconEnabledChanged(bool value) => SaveBeaconSettings();
    partial void OnBeaconMessageChanged(string value)
    {
        OnPropertyChanged(nameof(BeaconMessageBytes));
        OnPropertyChanged(nameof(BeaconMessageCount));
        OnPropertyChanged(nameof(BeaconMessageTooLong));
        SaveBeaconSettings();
    }
    partial void OnBeaconIntervalMinutesTextChanged(string value) => SaveBeaconSettings();
    partial void OnBeaconOfferChannelEnabledChanged(bool value) => SaveBeaconSettings();
    partial void OnBeaconOfferChannelChanged(BeaconChannelOption? value)
    {
        // A rebuild that finds no matching option leaves the selection null
        // without the operator having deselected anything, so the stored
        // reference is only moved when one is actually chosen.
        if (value is not null) { _beaconOfferPreset = value.Preset; _beaconOfferIndex = value.Index; }
        SaveBeaconSettings();
    }

    /// <summary>The settings object as the view model currently reads.</summary>
    private BeaconSettings BuildBeaconSettings() => new()
    {
        Enabled = BeaconEnabled,
        Message = BeaconMessage,
        IntervalSeconds = BeaconIntervalSeconds,
        HasOfferChannel = BeaconOfferChannelEnabled && _beaconOfferPreset.Length > 0,
        OfferChannelPreset = _beaconOfferPreset,
        OfferChannelIndex = _beaconOfferIndex,
        LastSentUtc = _lastBeaconSentUtc,
        Targets = BeaconTargets.Select(r => r.Target).ToList(),
    };

    private void LoadBeaconSettings(BeaconSettings saved)
    {
        BeaconEnabled = saved.Enabled;
        BeaconMessage = saved.Message ?? string.Empty;
        BeaconIntervalMinutesText = (BeaconPolicy.ClampInterval(saved.IntervalSeconds) / 60)
            .ToString(CultureInfo.InvariantCulture);
        BeaconOfferChannelEnabled = saved.HasOfferChannel;
        _beaconOfferPreset = saved.OfferChannelPreset ?? string.Empty;
        _beaconOfferIndex = saved.OfferChannelIndex;
        _lastBeaconSentUtc = saved.LastSentUtc;

        BeaconTargets.Clear();
        foreach (var target in saved.Targets)
        {
            var copy = new BeaconTarget { Preset = target.Preset, ChannelIndex = target.ChannelIndex };
            BeaconTargets.Add(new BeaconTargetRow { Target = copy, Label = ChannelLabel(copy) });
        }
    }

    /// <summary>
    /// Persists the beacon and re-reads the summary. The settings themselves
    /// are written by <c>ApplyOwnedSettings</c>, which takes the file's copy —
    /// assigning the in-memory one here would never reach disk.
    /// </summary>
    private void SaveBeaconSettings()
    {
        if (!_settingsLoaded) return;
        SaveSettings();
        OnPropertyChanged(nameof(BeaconSummary));
    }

    /// <summary>
    /// Called from the poll loop. One tick at a time: a beacon on several
    /// meshes is several transmits, each far slower than the poll interval.
    /// </summary>
    private void KickBeaconTick()
    {
        if (!CanTransmit) return;
        var settings = BuildBeaconSettings();
        if (!BeaconPolicy.WouldBroadcast(settings)) return;
        if (DateTime.UtcNow < BeaconPolicy.NextDueUtc(settings, DateTime.UtcNow)) return;
        // Background traffic, so it takes the polite half of the duty-cycle
        // budget and waits for the next tick with room rather than crowding
        // out something the operator asked for.
        if (!DutyCycleAllows(polite: true, out _)) return;
        if (Interlocked.Exchange(ref _beaconTickInFlight, 1) != 0) return;
        _ = RunBeaconTickAsync(settings);
    }

    private async Task RunBeaconTickAsync(BeaconSettings settings)
    {
        try
        {
            // The schedule has come round, so it starts again from here —
            // stamped before the sends rather than after, because a transmit
            // refused for duty cycle or a busy channel is not a reason to
            // retry every quarter second for an hour.
            _lastBeaconSentUtc = DateTime.UtcNow;
            SaveBeaconSettings();
            await BroadcastBeaconAsync(settings);
        }
        catch (Exception ex) { LogFromAnyThread($"  beacon failed: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _beaconTickInFlight, 0); }
    }

    /// <summary>
    /// Sends one beacon per destination and returns how many reached the air.
    /// </summary>
    /// <remarks>
    /// Leaves the schedule alone. Sending on demand is an extra beacon, not
    /// the scheduled one arriving early — pressing the button on the Quick
    /// send bar should not push the next scheduled broadcast out by an hour,
    /// and pressing it repeatedly should not hold the schedule off forever.
    /// The tick stamps its own clock before calling this.
    /// </remarks>
    public async Task<int> BroadcastBeaconAsync(BeaconSettings settings)
    {
        var offered = settings.HasOfferChannel
            ? ResolveBeaconChannel(new BeaconTarget
            {
                Preset = settings.OfferChannelPreset,
                ChannelIndex = settings.OfferChannelIndex,
            })
            : null;
        // The mesh the offered channel is on is what is being advertised, so
        // the preset and region come from it rather than being set separately
        // and left to drift out of step with the channel.
        var offerPreset = offered is null ? null : PresetForList(offered.Preset);

        int sent = 0;
        foreach (var target in BeaconPolicy.DistinctTargets(settings))
        {
            var channel = ResolveBeaconChannel(target);
            if (channel is null || channel.IsDisabled)
            {
                LogFromAnyThread($"  beacon skipped: {target.Preset} channel {target.ChannelIndex} is not usable");
                continue;
            }
            if (BeaconTxTarget(channel.Preset) is not { } tx)
            {
                LogFromAnyThread($"  beacon skipped: nothing can be tuned to {channel.Preset}");
                continue;
            }

            var region = offered is null ? Region.UNSET : SelectedRegion;
            var packetId = NextPacketId();
            var frame = MeshEncoder.EncodeBeacon(channel, _rxHost.MyNodeNum, packetId,
                settings.Message,
                offerChannel: offered,
                offerPreset: offerPreset,
                offerRegion: region,
                okToMqtt: OkToMqtt,
                xeddsaPrivateKey: MyXeddsa.PrivateKey, xeddsaPublicKey: MyXeddsa.PublicKey);

            if (await TransmitFrameAsync(frame, tx))
            {
                sent++;
                LogFromAnyThread($"  beacon sent on {ChannelLabel(channel)} ({tx.MeshTag})");
                // The router drops our own transmission as isFromUs, so what
                // went on the air is shown here or nowhere.
                var payload = MeshEncoder.BuildBeaconPayload(settings.Message, offered, offerPreset, region);
                ShowOutgoingBeaconFromAnyThread(channel, payload, packetId);
            }
            else
            {
                LogFromAnyThread($"  beacon not sent on {ChannelLabel(channel)}: transmit refused");
            }
        }
        return sent;
    }

    /// <summary>Files a beacon this station sent onto the channel it went out
    /// on. Public because the transmit path is only reachable with a running
    /// receiver, and what it does afterwards is worth pinning down without
    /// one.</summary>
    public void ShowOutgoingBeacon(ChannelConfig channel, byte[] payload, uint packetId) =>
        _rxHost.ShowOutgoingBeacon(channel, payload, packetId);

    /// <summary>Files the sent beacon onto its channel from whichever thread
    /// the transmit finished on. The tab's message list is bound to the UI,
    /// like the log lines beside it.</summary>
    private void ShowOutgoingBeaconFromAnyThread(ChannelConfig channel, byte[] payload, uint packetId)
    {
        if (Dispatcher.UIThread.CheckAccess()) _rxHost.ShowOutgoingBeacon(channel, payload, packetId);
        else Dispatcher.UIThread.Post(() => _rxHost.ShowOutgoingBeacon(channel, payload, packetId));
    }

    /// <summary>The preset a channel list is named for, or null for a list
    /// that names none.</summary>
    private LoraPreset? PresetForList(string listName) =>
        listName == _rxHost.PrimaryListName && IsCustomLoraParams
            ? null
            : Enum.TryParse<LoraPreset>(listName, out var preset) ? preset : null;

    /// <summary>
    /// What a beacon on a mesh goes out on. Unlike a reply, this does not fall
    /// back to the primary when nothing is listening for that preset: a beacon
    /// sealed with one mesh's key and transmitted on another's settings is a
    /// frame nobody there can read. The transmitter does not need a listener —
    /// it means only that replies would not be heard, which a zero-hop
    /// advertisement does not ask for.
    /// </summary>
    private TxTarget? BeaconTxTarget(string listName)
    {
        if (listName == _rxHost.PrimaryListName) return PrimaryTarget();
        if (!Enum.TryParse<LoraPreset>(listName, out var preset)) return null;
        if (!ChannelPlan.Supports(SelectedRegion, preset)) return null;

        var freqHz = (ulong)Math.Round(MonitorPlan.DefaultSlotFrequencyMHz(SelectedRegion, preset) * 1_000_000.0);
        // A listener on that mesh lets the transmitter wait for its channel to
        // be clear; without one the busy check falls back to the primary's.
        int listener = -1;
        foreach (var source in _rxSources)
            if (!source.IsPrimary && source.MeshName == listName) { listener = source.Listener; break; }

        return TxTarget.ForPreset(preset, freqHz, listener);
    }
}
