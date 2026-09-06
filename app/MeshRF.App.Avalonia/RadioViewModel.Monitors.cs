// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// One preset in the Monitors window: whether it is listened for, where its
/// default-slot channel is, and what is stopping it when it is not.
/// </summary>
public sealed partial class MonitorPresetRow : ObservableObject
{
    public required LoraPreset Preset { get; init; }
    public required string Name { get; init; }
    public required string SlotText { get; init; }
    public required string FreqText { get; init; }
    public required string StatusText { get; init; }

    /// <summary>False for a preset the region cannot hold, or the primary's
    /// own channel: neither is the user's to choose.</summary>
    public required bool CanChoose { get; init; }

    /// <summary>Raised when the tick changes, so the view model can rewrite
    /// the exclusion list and rebuild the rows.</summary>
    public Action<MonitorPresetRow>? Toggled { get; init; }

    [ObservableProperty] private bool _included;

    partial void OnIncludedChanged(bool value) => Toggled?.Invoke(this);
}

public partial class RadioViewModel
{
    /// <summary>Every preset the region supports, in the Monitors window's
    /// order: the primary first, then by frequency.</summary>
    public ObservableCollection<MonitorPresetRow> MonitorPresets { get; } = new();

    /// <summary>The channels drawn over the waterfall and the frequency axis:
    /// what is being listened to now, or what would be if the receiver were
    /// started with the settings as they stand.</summary>
    public ObservableCollection<ChannelBand> ChannelBands { get; } = new();

    [ObservableProperty] private string _monitorStatusText = string.Empty;

    /// <summary>Whether the capture centre is chosen by the plan. Unticking
    /// it hands the offset to <see cref="MonitorCenterOffsetKHz"/>.</summary>
    public bool MonitorCenterAuto
    {
        get => MonitorCenterOffsetKHz is null;
        set
        {
            if (value == MonitorCenterAuto) return;
            // Leaving Auto starts from where Auto had put it, so the box
            // opens on the answer the user was already looking at.
            MonitorCenterOffsetKHz = value ? null : Math.Round(BuildMonitorPlan().CenterOffsetKHz);
            OnPropertyChanged(nameof(MonitorCenterAuto));
            OnPropertyChanged(nameof(MonitorCenterOffsetText));
        }
    }

    /// <summary>The offset as typed, in kHz. Empty under Auto.</summary>
    public string MonitorCenterOffsetText
    {
        get => MonitorCenterOffsetKHz is { } kHz ? kHz.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
        set
        {
            if (MonitorCenterAuto) return;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kHz)) return;
            MonitorCenterOffsetKHz = kHz;
            OnPropertyChanged(nameof(MonitorCenterOffsetText));
        }
    }

    /// <summary>True while a rebuild is writing the rows, so a tick set from
    /// the plan is not read back as the user unticking something.</summary>
    private bool _rebuildingMonitorRows;

    /// <summary>The presets the plan would listen for, which is what decides
    /// whose channels are offered. Kept apart from the running listeners: a
    /// stopped receiver still has the meshes its operator has chosen.</summary>
    private HashSet<string> _shownPresets = new(StringComparer.Ordinal);

    /// <summary>Off on a hardware modem, which receives one channel at a
    /// time, and while the receiver is running, since the set is fixed at
    /// start.</summary>
    public bool CanEditMonitors => !IsRunning && SelectedDevice != RadioDeviceKind.Sx1262;

    /// <summary>Whether there is a note to show; the note itself explains
    /// why the controls are inert.</summary>
    public bool HasMonitorsNote => MonitorsUnavailableNote.Length > 0;

    public string MonitorsUnavailableNote => SelectedDevice == RadioDeviceKind.Sx1262
        ? "An SX1262 is a hardware modem: it receives one channel at a time, so there is nothing to listen for beside it."
        : IsRunning
            ? "Stop the receiver to change what it listens for."
            : string.Empty;

    /// <summary>
    /// Rebuilds the Monitors rows, the status line and the waterfall bands
    /// from the plan. Called whenever anything the plan reads changes.
    /// </summary>
    public void RefreshMonitors()
    {
        if (!_settingsLoaded) return;

        var plan = BuildMonitorPlan();
        RefreshChannelBands(plan);

        _shownPresets = plan.Listeners.Where(l => !l.IsPrimary && l.Preset is not null)
                            .Select(l => l.Preset!.Value.ToString())
                            .ToHashSet(StringComparer.Ordinal);
        // A mesh gets its channel list when it is chosen, not when the
        // receiver is started: the point of choosing it is to set its
        // channels up, which needs them to exist.
        foreach (var preset in _shownPresets) _rxHost.EnsureChannelList(preset);
        // A mesh that is no longer listened for cannot stay on show.
        if (_rxHost.ShownGroup.Length > 0 && !_shownPresets.Contains(_rxHost.ShownGroup))
            _rxHost.ShowGroup(string.Empty);
        _rxHost.RefreshTabGroups();
        RefreshTabGroupOptions();
        // A tab that has just been taken away cannot stay selected.
        if (SelectedTab is { IsTabListed: false })
            SelectedTab = Tabs.FirstOrDefault(t => t.IsTabListed);

        _rebuildingMonitorRows = true;
        try
        {
            MonitorPresets.Clear();
            foreach (var row in BuildMonitorRows(plan)) MonitorPresets.Add(row);
        }
        finally
        {
            _rebuildingMonitorRows = false;
        }

        double lowest = plan.DeviceCenterMHz - plan.UsableHalfSpanMHz;
        double highest = plan.DeviceCenterMHz + plan.UsableHalfSpanMHz;
        string rate = SelectedRxSampleRate is { } opt ? opt.Label : "no rate selected";
        MonitorStatusText = MultiPresetEnabled
            ? $"{plan.Listeners.Count} listening — centre {plan.DeviceCenterMHz:0.000} MHz, " +
              $"{lowest:0.000} to {highest:0.000} usable at {rate}"
            : $"The primary alone on {plan.DeviceCenterMHz:0.000} MHz at {rate}";
        OnPropertyChanged(nameof(CanEditMonitors));
        OnPropertyChanged(nameof(MonitorsUnavailableNote));
        OnPropertyChanged(nameof(HasMonitorsNote));
        OnPropertyChanged(nameof(MonitorCenterAuto));
        OnPropertyChanged(nameof(MonitorCenterOffsetText));
    }

    private IEnumerable<MonitorPresetRow> BuildMonitorRows(MonitorPlan.Result plan)
    {
        var rows = new List<(double Sort, MonitorPresetRow Row)>();

        foreach (var l in plan.Listeners)
        {
            string name = l.IsCustom ? MeshRF.Mesh.HeardOn.Custom : l.Preset!.Value.ToString();
            rows.Add((l.FreqMHz, new MonitorPresetRow
            {
                Preset = l.Preset ?? SelectedPreset,
                Name = name,
                SlotText = SlotTextFor(l.Preset, l.FreqMHz),
                FreqText = $"{l.FreqMHz:0.000} MHz",
                StatusText = l.IsPrimary ? "primary" : "listening",
                CanChoose = !l.IsPrimary,
                Included = true,
                Toggled = OnMonitorRowToggled,
            }));
        }

        foreach (var x in plan.LeftOut)
        {
            string status = x.Reason switch
            {
                MonitorPlan.LeftOutReason.IsPrimary => "the primary's own channel",
                MonitorPlan.LeftOutReason.Unsupported => $"{SelectedRegion} has no room for it",
                MonitorPlan.LeftOutReason.Excluded => "not listened for",
                _ => x.FitsAtRateHz is { } fits
                    ? $"out of range — fits at {fits / 1e6:0.###} MS/s"
                    : "out of range at every rate this device offers",
            };
            bool unsupported = x.Reason == MonitorPlan.LeftOutReason.Unsupported;
            rows.Add((unsupported ? double.MaxValue : x.FreqMHz, new MonitorPresetRow
            {
                Preset = x.Preset,
                Name = x.Preset.ToString(),
                SlotText = unsupported ? string.Empty : SlotTextFor(x.Preset, x.FreqMHz),
                FreqText = unsupported ? string.Empty : $"{x.FreqMHz:0.000} MHz",
                StatusText = status,
                CanChoose = x.Reason is MonitorPlan.LeftOutReason.Excluded or MonitorPlan.LeftOutReason.OutOfRange,
                Included = x.Reason != MonitorPlan.LeftOutReason.Excluded,
                Toggled = OnMonitorRowToggled,
            }));
        }

        return rows.OrderBy(r => r.Sort).Select(r => r.Row);
    }

    /// <summary>"slot 20", or nothing when the frequency is not on the grid
    /// (a custom primary).</summary>
    private string SlotTextFor(LoraPreset? preset, double freqMHz)
    {
        if (preset is not { } p) return string.Empty;
        int count = ChannelPlan.SlotCount(SelectedRegion, p);
        for (int slot = 1; slot <= count; slot++)
            if (Math.Abs(ChannelPlan.FrequencyMHz(SelectedRegion, p, slot) - freqMHz) < 1e-6)
                return $"slot {slot}";
        return string.Empty;
    }

    private void OnMonitorRowToggled(MonitorPresetRow row)
    {
        if (_rebuildingMonitorRows) return;

        var name = row.Preset.ToString();
        if (row.Included) MonitorExcludedPresets.Remove(name);
        else if (!MonitorExcludedPresets.Contains(name)) MonitorExcludedPresets.Add(name);

        MonitorExclusionsChanged();
        RefreshMonitors();
    }

    /// <summary>The bands drawn over the spectrum. While the receiver runs
    /// these are its listeners; stopped, they are what it would start on.</summary>
    private void RefreshChannelBands(MonitorPlan.Result plan)
    {
        ChannelBands.Clear();
        if (IsRunning && _rxSources.Length > 0)
        {
            foreach (var s in _rxSources)
            {
                double bwHz = BandwidthHz(s);
                ChannelBands.Add(new ChannelBand(BandLabel(s.Preset, s.IsCustom, s.FreqMHz),
                                                 s.FreqMHz * 1e6, bwHz, s.IsPrimary));
            }
            return;
        }

        foreach (var l in plan.Listeners)
            ChannelBands.Add(new ChannelBand(BandLabel(l.Preset, l.IsCustom, l.FreqMHz),
                                             l.FreqMHz * 1e6, l.BwHz, l.IsPrimary));
    }

    /// <summary>What a band is called on the waterfall: the preset, and the
    /// slot when it is not that preset's default, which is what tells two
    /// meshes on one preset apart.</summary>
    private string BandLabel(LoraPreset? preset, bool isCustom, double freqMHz)
    {
        if (isCustom || preset is not { } p) return MeshRF.Mesh.HeardOn.Custom;
        double defaultFreq = MonitorPlan.DefaultSlotFrequencyMHz(SelectedRegion, p);
        if (Math.Abs(defaultFreq - freqMHz) < 1e-6) return p.ToString();
        var slot = SlotTextFor(p, freqMHz);
        return slot.Length == 0 ? p.ToString() : $"{p} {slot["slot ".Length..]}";
    }
}
