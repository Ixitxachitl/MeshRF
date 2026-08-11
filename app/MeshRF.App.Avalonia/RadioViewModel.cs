// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Radio control surface: device select / start-stop RX / signal stats,
/// plus a real (not mocked) message/node list — received frames are fed
/// through the same MeshRxRouter (MeshRF.Core) the WPF app uses, via
/// AvaloniaMeshRxHost.
/// </summary>
public partial class RadioViewModel : ObservableObject, IDisposable
{
    // Mirrors MainViewModel.PayloadLineRegex; matches lines like
    // "  payload[OK] len=31 crc=E511/E511 FFFFFFFF594FA54F...".
    private static readonly Regex PayloadLineRegex = new(
        @"payload(?:\[(?<status>OK|BAD)\])?\s+len=(?<len>\d+)(?:\s+crc=(?<rx>[0-9A-Fa-f]+)/(?<calc>[0-9A-Fa-f]+))?\s+(?<hex>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    private readonly AppSettings _settings;
    private readonly MeshtasticCore? _core;
    private readonly DispatcherTimer _pollTimer;
    private readonly NodeStore _nodeStore = new();
    private readonly MessageStore _messageStore = new();
    private readonly ChannelStore _channelStore = new();
    private readonly WaypointStore _waypointStore = new();
    private readonly AvaloniaMeshRxHost _rxHost;
    private readonly MeshRxRouter _rxRouter;

    public ObservableCollection<ITabItem> Tabs => _rxHost.Tabs;
    public ObservableCollection<NodeRecord> Nodes => _rxHost.Nodes;
    public ObservableCollection<WaypointRecord> Waypoints => _rxHost.Waypoints;
    public ObservableCollection<string> LogLines => _rxHost.LogLines;

    [ObservableProperty]
    private ITabItem? _selectedTab;

    [ObservableProperty]
    private string _newChannelName = string.Empty;

    [ObservableProperty]
    private RadioDeviceKind _selectedDevice = RadioDeviceKind.Auto;

    // 906.875 MHz = US LongFast slot 20, same default MeshRF.App's
    // MainViewModel starts from.
    [ObservableProperty]
    private double _centerFreqMHz = 906.875;

    [ObservableProperty]
    private LoraPreset _selectedPreset = LoraPreset.LongFast;

    public LoraPreset[] AvailablePresets { get; } = Enum.GetValues<LoraPreset>();

    [ObservableProperty]
    private Region _selectedRegion = Region.US;

    public Region[] AvailableRegions { get; } = Enum.GetValues<Region>();

    [ObservableProperty]
    private ObservableCollection<int> _slots = new();

    [ObservableProperty]
    private int _selectedSlot = 20;

    // SF/BW/CR: auto-filled from the preset (ApplyPresetToLoraParams), editable
    // to override — mirrors MeshRF.App's OverrideSf/OverrideBwKhz/OverrideCr.
    [ObservableProperty]
    private byte _overrideSf = 11;

    [ObservableProperty]
    private double _overrideBwKhz = 250;

    [ObservableProperty]
    private byte _overrideCr = 5;

    /// <summary>True when SF/BW/CR differ from the selected preset's defaults.</summary>
    public bool IsCustomLoraParams
    {
        get
        {
            var p = LoraParamsHelper.FromPreset(SelectedPreset);
            return OverrideSf != p.Sf || Math.Abs(OverrideBwKhz - p.BwKhz) > 0.01 || OverrideCr != p.Cr;
        }
    }

    [ObservableProperty]
    private byte _lnaGainDb = 24;

    [ObservableProperty]
    private byte _vgaGainDb = 20;

    [ObservableProperty]
    private bool _ampEnable;

    [ObservableProperty]
    private byte _rtlGainDb = 30;

    [ObservableProperty]
    private bool _rtlAgcEnable;

    private bool _suppressLoraParamSync;
    private bool _suppressSlotSync;
    private bool _suppressRetune;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _deviceStatus = string.Empty;

    [ObservableProperty]
    private float _rssiDbfs;

    [ObservableProperty]
    private string _messageText = string.Empty;

    public RadioDeviceKind[] AvailableDevices { get; } = Enum.GetValues<RadioDeviceKind>();

    public string ToggleButtonText => IsRunning ? "Stop RX" : "Start RX";

    public RadioViewModel()
    {
        _settings = AppSettings.Load();

        // Snapshot everything we need from _settings into locals up front.
        // OnSelectedPresetChanged/OnSelectedRegionChanged etc. below call
        // SaveSettings(), which mutates these same _settings fields to match
        // the view model's current (partially-applied) state — reading
        // _settings again later in this constructor would see those
        // in-progress values instead of what was actually on disk, silently
        // clobbering the saved slot/frequency with a preset's default.
        var savedRxDeviceKind = _settings.RxDeviceKind;
        var savedRegion = _settings.Region;
        var savedPreset = _settings.Preset;
        var savedOverrideSf = _settings.OverrideSf;
        var savedOverrideBwHz = _settings.OverrideBwHz;
        var savedOverrideCr = _settings.OverrideCr;
        var savedSlot = _settings.Slot;
        var savedCenterFreqMHz = _settings.CenterFreqMHz;
        var savedLnaGainDb = _settings.LnaGainDb;
        var savedVgaGainDb = _settings.VgaGainDb;
        var savedAmpEnable = _settings.AmpEnable;
        var savedRtlGainDb = _settings.RtlGainDb;
        var savedRtlAgcEnable = _settings.RtlAgcEnable;

        // Shared with MeshRF.App's UserNodeNum when set (same settings.json);
        // otherwise an ephemeral random identity for this session — see
        // AvaloniaMeshRxHost.MyNodeNum. Avoid 0 (unset) and the broadcast
        // address for the random fallback.
        var myNodeNum = _settings.UserNodeNum != 0
            ? _settings.UserNodeNum
            : (uint)Random.Shared.NextInt64(1, 0xFFFFFFFE);
        _rxHost = new AvaloniaMeshRxHost(_nodeStore, _channelStore, _waypointStore, _messageStore, myNodeNum);
        _rxRouter = new MeshRxRouter(_rxHost, _messageStore, new AvaloniaUiDispatcher());
        SelectedTab = Tabs.FirstOrDefault();
        if (Enum.TryParse<RadioDeviceKind>(savedRxDeviceKind, out var device))
            SelectedDevice = device;
        if (Enum.TryParse<Region>(savedRegion, out var region))
            SelectedRegion = region;
        if (Enum.TryParse<LoraPreset>(savedPreset, out var preset))
            SelectedPreset = preset;

        if (savedOverrideSf != 0 || savedOverrideBwHz != 0 || savedOverrideCr != 0)
        {
            OverrideSf = savedOverrideSf;
            OverrideBwKhz = savedOverrideBwHz / 1000.0;
            OverrideCr = savedOverrideCr;
        }
        else
        {
            ApplyPresetToLoraParams(SelectedPreset);
        }
        RebuildSlots(snapToDefault: savedSlot <= 0);
        if (savedSlot > 0) SelectedSlot = savedSlot;
        if (savedCenterFreqMHz > 0)
            CenterFreqMHz = savedCenterFreqMHz;

        LnaGainDb = savedLnaGainDb;
        VgaGainDb = savedVgaGainDb;
        AmpEnable = savedAmpEnable;
        RtlGainDb = savedRtlGainDb;
        RtlAgcEnable = savedRtlAgcEnable;

        // Final sync: re-save so _settings/disk reflect the fully-resolved
        // state above rather than whatever an intermediate cascade wrote.
        SaveSettings();

        try
        {
            _core = new MeshtasticCore();
            StatusText = $"Native bridge loaded ({Environment.OSVersion.Platform}).";
        }
        catch (Exception ex)
        {
            StatusText = $"Native bridge unavailable: {ex.Message}";
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
        Poll();
    }

    private void Poll()
    {
        if (_core is null) return;

        IsRunning = _core.IsRunning;
        DeviceStatus = $"RX: {_core.DeviceName}  TX: {_core.TxDeviceName} — {_core.DeviceStatus}";
        if (!IsRunning) return;

        RssiDbfs = _core.GetSignalStats().RssiDbfs;
        _rxHost.CurrentRssiDbfs = RssiDbfs;

        for (int i = 0; i < 64; i++)
        {
            var ev = _core.PullEvent();
            if (ev is null) break;
            ProcessDemodEvent(ev);
        }
    }

    private void ProcessDemodEvent(string ev)
    {
        if (ev.IndexOf("payload", StringComparison.Ordinal) < 0) return;
        var m = PayloadLineRegex.Match(ev);
        if (!m.Success) return;
        if (!(m.Groups["status"].Success && m.Groups["status"].Value == "OK")) return;

        var frame = HexToBytes(m.Groups["hex"].Value);
        if (frame.Length < MeshHeader.Size) return;
        if (!MeshHeader.TryParse(frame, out var header)) return;

        float? packetRssiDbm = float.IsNegativeInfinity(RssiDbfs) ? null : RssiDbfs;
        _rxRouter.ProcessReceivedFrame(frame, header, snrDb: null, packetRssiDbm: packetRssiDbm);
    }

    private static byte[] HexToBytes(string hex)
    {
        if ((hex.Length & 1) != 0) return Array.Empty<byte>();
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out b[i]))
                return Array.Empty<byte>();
        }
        return b;
    }

    [RelayCommand(CanExecute = nameof(CanToggleRx))]
    private void ToggleRx()
    {
        if (_core is null) return;

        if (_core.IsRunning)
        {
            _core.Stop();
        }
        else
        {
            _core.SetRxDevice(SelectedDevice);
            ApplyGains();
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            try
            {
                if (IsCustomLoraParams)
                {
                    var bwHz = (uint)Math.Round(OverrideBwKhz * 1000.0);
                    _core.StartRxParams(OverrideSf, bwHz, OverrideCr, hz);
                }
                else
                {
                    _core.StartRx(SelectedPreset, hz);
                }
            }
            catch (InvalidOperationException ex)
            {
                StatusText = $"Failed to start RX: {ex.Message}";
            }
        }
        Poll();
    }

    private bool CanToggleRx() => _core is not null;

    private void ApplyGains()
    {
        if (_core is null) return;
        if (SelectedDevice == RadioDeviceKind.RtlSdr)
            _core.SetGains(RtlGainDb, 0, RtlAgcEnable);
        else
            _core.SetGains(LnaGainDb, VgaGainDb, AmpEnable);
    }

    /// <summary>Syncs OverrideSf/BwKhz/Cr to the firmware defaults for
    /// <paramref name="preset"/> without triggering a save loop.</summary>
    private void ApplyPresetToLoraParams(LoraPreset preset)
    {
        var p = LoraParamsHelper.FromPreset(preset);
        _suppressLoraParamSync = true;
        try
        {
            OverrideSf = p.Sf;
            OverrideBwKhz = p.BwKhz;
            OverrideCr = p.Cr;
        }
        finally
        {
            _suppressLoraParamSync = false;
        }
        OnPropertyChanged(nameof(IsCustomLoraParams));
    }

    private void RebuildSlots(bool snapToDefault = false)
    {
        var count = ChannelPlan.SlotCount(SelectedRegion, SelectedPreset);
        var preferred = ChannelPlan.DefaultSlot(SelectedRegion, SelectedPreset);
        int desired = snapToDefault || SelectedSlot < 1 || SelectedSlot > count ? preferred : SelectedSlot;

        _suppressSlotSync = true;
        try
        {
            var fresh = new ObservableCollection<int>();
            for (var i = 1; i <= count; i++) fresh.Add(i);
            Slots = fresh;
            SelectedSlot = desired;
        }
        finally
        {
            _suppressSlotSync = false;
        }

        _suppressRetune = true;
        try { CenterFreqMHz = ChannelPlan.FrequencyMHz(SelectedRegion, SelectedPreset, desired); }
        finally { _suppressRetune = false; }
    }

    partial void OnSelectedDeviceChanged(RadioDeviceKind value) { ApplyGains(); SaveSettings(); }

    partial void OnSelectedPresetChanged(LoraPreset value)
    {
        // Autofill SF/BW/CR from the new preset — preset is the anchor, so
        // overwriting any prior manual override here is the right UX.
        ApplyPresetToLoraParams(value);
        RebuildSlots(snapToDefault: true);
        SaveSettings();
    }

    partial void OnSelectedRegionChanged(Region value) { RebuildSlots(snapToDefault: true); SaveSettings(); }

    partial void OnSelectedSlotChanged(int value)
    {
        if (_suppressSlotSync || value <= 0) return;
        CenterFreqMHz = ChannelPlan.FrequencyMHz(SelectedRegion, SelectedPreset, value);
        SaveSettings();
    }

    partial void OnOverrideSfChanged(byte value)      { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); SaveSettings(); } }
    partial void OnOverrideBwKhzChanged(double value) { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); SaveSettings(); } }
    partial void OnOverrideCrChanged(byte value)      { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); SaveSettings(); } }

    partial void OnCenterFreqMHzChanged(double value) { if (!_suppressRetune) SaveSettings(); }

    partial void OnLnaGainDbChanged(byte value) { ApplyGains(); SaveSettings(); }
    partial void OnVgaGainDbChanged(byte value) { ApplyGains(); SaveSettings(); }
    partial void OnAmpEnableChanged(bool value) { ApplyGains(); SaveSettings(); }
    partial void OnRtlGainDbChanged(byte value) { ApplyGains(); SaveSettings(); }
    partial void OnRtlAgcEnableChanged(bool value) { ApplyGains(); SaveSettings(); }

    private void SaveSettings()
    {
        _settings.RxDeviceKind = SelectedDevice.ToString();
        _settings.Preset = SelectedPreset.ToString();
        _settings.CenterFreqMHz = CenterFreqMHz;
        _settings.Region = SelectedRegion.ToString();
        _settings.Slot = SelectedSlot;
        _settings.OverrideSf = OverrideSf;
        _settings.OverrideBwHz = (uint)Math.Round(OverrideBwKhz * 1000.0);
        _settings.OverrideCr = OverrideCr;
        _settings.LnaGainDb = LnaGainDb;
        _settings.VgaGainDb = VgaGainDb;
        _settings.AmpEnable = AmpEnable;
        _settings.RtlGainDb = RtlGainDb;
        _settings.RtlAgcEnable = RtlAgcEnable;
        _settings.Save();
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        if (_core is null || string.IsNullOrWhiteSpace(MessageText)) return;

        // DMs aren't PKC-sealed here (no node identity/PKI yet) — sent as a
        // legacy channel-PSK-encrypted unicast on the primary channel,
        // exactly like a broadcast but addressed to one node.
        ObservableCollection<ChannelMessage>? messages;
        ChannelConfig channel;
        uint to = 0xFFFFFFFFu;
        switch (SelectedTab)
        {
            case ChannelTabViewModel chanTab:
                messages = chanTab.Messages;
                channel = chanTab.Config;
                break;
            case ConversationTabViewModel convoTab:
                var primary = Tabs.OfType<ChannelTabViewModel>().FirstOrDefault();
                if (primary is null) return;
                messages = convoTab.Messages;
                channel = primary.Config;
                to = convoTab.NodeNum;
                break;
            default:
                return;
        }

        var text = MessageText.Trim();
        var packetId = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        var hz = (ulong)(CenterFreqMHz * 1_000_000);

        var frame = MeshEncoder.EncodeTextMessage(channel, _rxHost.MyNodeNum, packetId, text, to: to);

        bool ok = await Task.Run(() => _core.Transmit(SelectedPreset, hz, frame)).ConfigureAwait(true);
        if (!ok)
        {
            StatusText = "Failed to transmit (no TX-capable device selected?).";
            return;
        }

        // Echo locally — we won't decode our own transmission back off the
        // air (MeshRxRouter treats hearing it as isFromUs and drops it).
        messages.Insert(0, new ChannelMessage
        {
            FromId = $"!{_rxHost.MyNodeNum:x8}",
            SenderNodeNum = _rxHost.MyNodeNum,
            Text = text,
            PacketId = packetId,
            IsOutgoing = true,
        });
        MessageText = string.Empty;
    }

    private bool CanSendMessage() =>
        _core?.CanTransmit == true && SelectedTab is not null && !string.IsNullOrWhiteSpace(MessageText);

    partial void OnMessageTextChanged(string value) => SendMessageCommand.NotifyCanExecuteChanged();
    partial void OnSelectedTabChanged(ITabItem? value) => SendMessageCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void MessageNode(NodeRecord? node)
    {
        if (node is null) return;
        SelectedTab = _rxHost.OpenConversation(node.NodeNum);
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleButtonText));
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddChannel))]
    private void AddChannel()
    {
        var name = NewChannelName.Trim();
        if (name.Length == 0) return;
        SelectedTab = _rxHost.AddChannel(name);
        NewChannelName = string.Empty;
    }

    private bool CanAddChannel() => !string.IsNullOrWhiteSpace(NewChannelName);

    partial void OnNewChannelNameChanged(string value) => AddChannelCommand.NotifyCanExecuteChanged();

    public void Dispose()
    {
        _pollTimer.Stop();
        _rxRouter.Dispose();
        _rxHost.Dispose();
        _nodeStore.Dispose();
        _messageStore.Dispose();
        _core?.Dispose();
    }
}
