// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;

namespace MeshRF.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MeshtasticCore _core = new();
    private readonly NodeStore _nodeStore = new();
    private readonly ChannelStore _channelStore = new();
    private readonly MessageStore _messageStore = new();
    private readonly AppSettings _settings;
    private bool _settingsLoaded;

    // Payload recording: open StreamWriter when active. Each decoded payload is
    // appended as one JSON object (JSONL). Null when not recording.
    private StreamWriter? _payloadWriter;
    private int _payloadCount;

    [ObservableProperty]
    private LoraPreset _selectedPreset = LoraPreset.LongFast;

    [ObservableProperty]
    private Region _selectedRegion = Region.US;

    [ObservableProperty]
    private int _selectedSlot = 20; // US LongFast default

    [ObservableProperty]
    private double _centerFreqMHz = 906.875; // US LongFast slot 20

    [ObservableProperty]
    private byte _lnaGainDb = 24;

    [ObservableProperty]
    private byte _vgaGainDb = 20;

    [ObservableProperty]
    private bool _ampEnable;

    [ObservableProperty]
    private bool _agcEnable;

    [ObservableProperty]
    private double _agcTargetDbfs = -15.0;

    /// <summary>RTL-SDR manual tuner gain in dB (0..49). Independent of the
    /// HackRF LNA/VGA controls so each device remembers its own setting.</summary>
    [ObservableProperty]
    private byte _rtlGainDb = 30;

    /// <summary>RTL-SDR 5 V bias-T on the antenna port. Off by default.</summary>
    [ObservableProperty]
    private bool _biasTee;

    [ObservableProperty]
    private string _theme = "System";

    public IReadOnlyList<string> Themes { get; } = new[] { "System", "Light", "Dark" };

    [ObservableProperty]
    private string _waterfallColormap = "Turbo";

    public IReadOnlyList<string> WaterfallColormaps { get; } = new[] { "Turbo", "Inferno" };

    [ObservableProperty]
    private bool _waterfallAutoLevels = true;

    [ObservableProperty]
    private double _waterfallFloorDb = -100.0;

    [ObservableProperty]
    private double _waterfallCeilDb = 0.0;

    /// <summary>Displayed spectrum/waterfall span in Hz (= device sample rate).
    /// Updated from the running pipeline; 0 when stopped. Drives the frequency
    /// axis labels.</summary>
    [ObservableProperty]
    private double _spectrumSpanHz;

    /// <summary>Center frequency of the displayed span in Hz (= tuned freq).</summary>
    public double SpectrumCenterHz => CenterFreqMHz * 1_000_000.0;

    [ObservableProperty]
    private string _status = "Idle";

    public string DeviceName => _core.DeviceName;
    public bool HasRealRadio => _core.HasRealRadio;
    public string DeviceStatus => _core.DeviceStatus;
    public string DeviceBadge => _core.HasRealRadio
        ? $"Device: {_core.DeviceName}"
        : $"Device: {_core.DeviceName} (no hardware \u2014 synthetic)";

    /// <summary>An entry in the device-backend selector.</summary>
    public sealed record DeviceOption(RadioDeviceKind Kind, string Label);

    /// <summary>Selectable radio backends (HackRF / RTL-SDR / Auto / Synthetic).
    /// Populated at construction with an availability annotation.</summary>
    public IReadOnlyList<DeviceOption> DeviceOptions { get; private set; } =
        Array.Empty<DeviceOption>();

    [ObservableProperty]
    private DeviceOption? _selectedDevice;

    private bool _suppressDeviceUpdate;

    /// <summary>True when the selected backend is an RTL-SDR (drives which
    /// hardware controls the toolbar shows).</summary>
    public bool IsRtlSdr => SelectedDevice?.Kind == RadioDeviceKind.RtlSdr;

    /// <summary>True for everything that isn't an RTL-SDR (HackRF / Auto /
    /// Synthetic) — those use the LNA/VGA/AMP gain model.</summary>
    public bool IsHackRf => !IsRtlSdr;

    /// <summary>The device selector is only editable while RX is stopped.</summary>
    public bool CanSelectDevice => !IsRunning;

    [ObservableProperty]
    private float _rssiDbfs = float.NegativeInfinity;

    [ObservableProperty]
    private float _peakDbfs = float.NegativeInfinity;

    [ObservableProperty]
    private ulong _totalSamples;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private bool _isRecordingPayloads;

    public IReadOnlyList<LoraPreset> Presets { get; } = Enum.GetValues<LoraPreset>();
    public IReadOnlyList<Region> Regions { get; } = Enum.GetValues<Region>();

    [ObservableProperty]
    private ObservableCollection<int> _slots = new();

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<NodeRecord> Nodes { get; } = new();

    /// <summary>Most-recent decoded mesh messages, newest first.</summary>
    public ObservableCollection<MessageRecord> Messages { get; } = new();

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    /// <summary>Tabs shown in the channel/conversation TabControl: channels
    /// followed by any open direct-message conversations.</summary>
    public ObservableCollection<object> Tabs { get; } = new();

    [ObservableProperty]
    private object? _selectedTab;

    /// <summary>The selected tab when it is a channel (null for DM tabs).</summary>
    public ChannelViewModel? SelectedChannel => SelectedTab as ChannelViewModel;

    partial void OnSelectedTabChanged(object? value)
    {
        OnPropertyChanged(nameof(SelectedChannel));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
    }

    // -- Local node identity -------------------------------------------------

    [ObservableProperty] private string _myNodeIdText = string.Empty;
    [ObservableProperty] private string _myLongName = string.Empty;
    [ObservableProperty] private string _myShortName = string.Empty;
    [ObservableProperty] private string _myRole = "Client";

    /// <summary>Our resolved 32-bit node number (0 = unset).</summary>
    private uint _myNodeNum;

    public IReadOnlyList<string> NodeRoleOptions { get; } = new[]
    {
        "Client", "ClientMute", "ClientHidden", "Router", "RouterClient",
        "Repeater", "Tracker", "Sensor", "TAK", "TakTracker", "LostAndFound",
    };

    // -- Hardware model / rebroadcast / TX keys / home location -------------

    [ObservableProperty] private string _myHwModel = "UNSET";
    [ObservableProperty] private string _rebroadcastMode = "ALL";
    [ObservableProperty] private string _myPublicKey = string.Empty;
    [ObservableProperty] private string _myPrivateKey = string.Empty;

    /// <summary>Default hop limit for transmitted packets (1..7). Mirrors the
    /// firmware LoRa config; broadcasts and DMs are sent with this many hops.</summary>
    [ObservableProperty] private int _hopLimit = 3;

    [ObservableProperty] private string _homeLatitudeText = string.Empty;
    [ObservableProperty] private string _homeLongitudeText = string.Empty;

    /// <summary>Resolved home location (null when unset/invalid).</summary>
    public double? HomeLatitude { get; private set; }
    public double? HomeLongitude { get; private set; }

    /// <summary>Raised when the home location or node positions change so the
    /// map view can refresh its markers.</summary>
    public event EventHandler? MapDataChanged;

    /// <summary>A point shown on the map: a node position or the home marker.</summary>
    public sealed record MapMarker(
        double Lat, double Lon, string Label, string Title, bool IsHome);

    /// <summary>
    /// Markers for the map view: the home location (if set) plus every node
    /// that has a known position.
    /// </summary>
    public IReadOnlyList<MapMarker> GetMapMarkers()
    {
        var list = new List<MapMarker>();
        if (HomeLatitude is double hlat && HomeLongitude is double hlon)
            list.Add(new MapMarker(hlat, hlon, "Home", "Home", IsHome: true));

        foreach (var n in Nodes)
        {
            if (n.Latitude is not double lat || n.Longitude is not double lon) continue;
            var name = string.IsNullOrWhiteSpace(n.LongName) ? n.DisplayId : n.LongName;
            var label = string.IsNullOrWhiteSpace(n.ShortName) ? name : n.ShortName;
            list.Add(new MapMarker(lat, lon, label, BuildNodeTooltip(n), IsHome: false));
        }
        return list;
    }

    /// <summary>Removes the given nodes from the in-memory list, the persistent
    /// store, and refreshes the map.</summary>
    public void RemoveNodes(IEnumerable<MeshRF.Nodes.NodeRecord> nodes)
    {
        var targets = nodes?.ToList();
        if (targets is null || targets.Count == 0) return;

        bool removedPositioned = false;
        foreach (var n in targets)
        {
            // Never delete our own node: it represents us and is re-created on
            // startup / identity change, so removing it is pointless and would
            // make our name vanish from chats until the next restart.
            if (_myNodeNum != 0 && n.NodeNum == _myNodeNum) continue;
            _nodeStore.Forget(n.NodeNum);
            Nodes.Remove(n);
            if (n.Latitude is not null && n.Longitude is not null) removedPositioned = true;
        }

        if (removedPositioned)
            MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forget the stored public key for the given node(s) and ask them
    /// to re-send their NodeInfo, so a changed (mismatched) key can be re-learned
    /// and trusted. Wired to the node list's "Request new keys" menu item.</summary>
    public void RequestKeys(IEnumerable<MeshRF.Nodes.NodeRecord> nodes)
    {
        var targets = nodes?.Where(n => n is not null).ToList();
        if (targets is null || targets.Count == 0) return;

        foreach (var n in targets)
        {
            if (_myNodeNum != 0 && n.NodeNum == _myNodeNum) continue;
            _nodeStore.ClearPublicKey(n.NodeNum);
            RequestNodeInfo(n.NodeNum);
            Log($"  requested new keys from {NodeDisplayName(n.NodeNum)}");
        }

        ReloadNodes();
    }

    /// <summary>Builds the multi-line tooltip shown when hovering a node on the
    /// map: identity, telemetry, and how long ago it was last heard.</summary>
    private static string BuildNodeTooltip(MeshRF.Nodes.NodeRecord n)
    {
        var name = string.IsNullOrWhiteSpace(n.LongName) ? n.DisplayId : n.LongName;
        var sb = new System.Text.StringBuilder();
        sb.Append(name);
        if (!string.IsNullOrWhiteSpace(n.ShortName))
            sb.Append("  [").Append(n.ShortName).Append(']');
        sb.Append('\n').Append(n.DisplayId);

        if (!string.IsNullOrWhiteSpace(n.Role))
            sb.Append("\nRole: ").Append(n.Role);
        if (!string.IsNullOrWhiteSpace(n.HwModel))
            sb.Append("\nHW: ").Append(n.HwModel);

        // Position.
        if (n.Latitude is double la && n.Longitude is double lo)
        {
            sb.Append('\n').Append(la.ToString("F5", CultureInfo.InvariantCulture))
              .Append(", ").Append(lo.ToString("F5", CultureInfo.InvariantCulture));
            if (n.AltitudeM is int alt) sb.Append("  ").Append(alt).Append(" m");
        }

        // Signal.
        var sig = new List<string>();
        if (n.RssiDbm is float rssi) sig.Add($"{rssi:F0} dBm");
        if (n.SnrDb is float snr) sig.Add($"SNR {snr:F1}");
        if (n.HopsAway is byte hops) sig.Add(hops == 0 ? "direct" : $"{hops} hop{(hops == 1 ? "" : "s")}");
        if (sig.Count > 0) sb.Append('\n').Append(string.Join("  ·  ", sig));

        // Power telemetry.
        var pwr = new List<string>();
        if (n.BatteryPct is byte bat) pwr.Add($"{bat}%");
        if (n.VoltageV is float v) pwr.Add($"{v:F2} V");
        if (pwr.Count > 0) sb.Append("\nBattery: ").Append(string.Join("  ", pwr));

        // Channel utilization.
        var util = new List<string>();
        if (n.ChannelUtilPct is float ch) util.Add($"ChUtil {ch:F1}%");
        if (n.AirUtilTxPct is float air) util.Add($"AirTx {air:F1}%");
        if (util.Count > 0) sb.Append('\n').Append(string.Join("  ", util));

        // Environment telemetry.
        var env = new List<string>();
        if (n.TemperatureC is float t) env.Add($"{t:F1} °C");
        if (n.RelativeHumidityPct is float h) env.Add($"{h:F0}% RH");
        if (n.BarometricPressureHpa is float p) env.Add($"{p:F0} hPa");
        if (env.Count > 0) sb.Append('\n').Append(string.Join("  ", env));

        // Last heard (relative).
        sb.Append("\nHeard ").Append(FormatAge(n.LastHeardEpoch));
        return sb.ToString();
    }

    /// <summary>Formats a unix epoch as a human "x ago" string.</summary>
    private static string FormatAge(long epoch)
    {
        if (epoch <= 0) return "never";
        var delta = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h {delta.Minutes}m ago";
        return $"{(int)delta.TotalDays}d {delta.Hours}h ago";
    }

    /// <summary>Common Meshtastic hardware models (subset of the firmware
    /// <c>HardwareModel</c> enum). "UNSET" leaves it unspecified.</summary>
    public IReadOnlyList<string> HwModelOptions { get; } = new[]
    {
        "UNSET", "TBEAM", "TBEAM_S3_CORE", "TLORA_V2", "TLORA_V1",
        "TLORA_V2_1_1P6", "TLORA_V2_1_1P8", "TLORA_T3_S3",
        "HELTEC_V2_1", "HELTEC_V3", "HELTEC_WSL_V3", "HELTEC_WIRELESS_TRACKER",
        "HELTEC_WIRELESS_PAPER", "RAK4631", "RAK11200", "RAK2560",
        "NANO_G1", "NANO_G1_EXPLORER", "NANO_G2_ULTRA",
        "STATION_G1", "STATION_G2", "T_ECHO", "T_DECK", "T_WATCH_S3",
        "PICOMPUTER_S3", "SENSECAP_INDICATOR", "TRACKER_T1000_E",
        "SEEED_XIAO_S3", "WIO_WM1110", "RPI_PICO", "PORTDUINO", "PRIVATE_HW",
    };

    /// <summary>Firmware <c>Config.DeviceConfig.RebroadcastMode</c> options.</summary>
    public IReadOnlyList<string> RebroadcastModeOptions { get; } = new[]
    {
        "ALL", "ALL_SKIP_DECODING", "LOCAL_ONLY", "KNOWN_ONLY",
        "NONE", "CORE_PORTNUMS_ONLY",
    };

    /// <summary>Persistent node database — exposed for tests / advanced UI.</summary>
    public NodeStore NodeStore => _nodeStore;

    /// <summary>Persistent message database — exposed for tests / advanced UI.</summary>
    public MessageStore MessageStore => _messageStore;

    /// <summary>Native DSP handle exposed for the View to poll spectrum frames.</summary>
    public MeshtasticCore Core => _core;

    private bool _suppressSlotSync;
    // Set while RebuildSlots updates CenterFreqMHz, so the resulting frequency
    // change doesn't trigger its own retune — the preset/region handler that
    // called RebuildSlots performs a single retune itself.
    private bool _suppressRetune;

    public MainViewModel()
    {
        _settings = AppSettings.Load();

        // Apply persisted values BEFORE wiring change handlers fire usefully.
        // We rely on the [ObservableProperty] setters to fire OnXChanged,
        // which we guard against re-saving until _settingsLoaded becomes true.
        if (Enum.TryParse<Region>(_settings.Region, out var r)) SelectedRegion = r;
        if (Enum.TryParse<LoraPreset>(_settings.Preset, out var p)) SelectedPreset = p;
        LnaGainDb = _settings.LnaGainDb;
        VgaGainDb = _settings.VgaGainDb;
        AmpEnable = _settings.AmpEnable;
        AgcEnable = _settings.AgcEnable;
        AgcTargetDbfs = _settings.AgcTargetDbfs;
        RtlGainDb = _settings.RtlGainDb;
        BiasTee = _settings.BiasTee;
        Theme = _settings.Theme;
        WaterfallColormap = _settings.WaterfallColormap;
        WaterfallAutoLevels = _settings.WaterfallAutoLevels;
        WaterfallFloorDb = _settings.WaterfallFloorDb;
        WaterfallCeilDb = _settings.WaterfallCeilDb;

        // Local node identity (for recognising direct messages).
        _myNodeNum = _settings.UserNodeNum;
        MyNodeIdText = _myNodeNum != 0 ? $"!{_myNodeNum:x8}" : string.Empty;
        MyLongName = _settings.UserLongName;
        MyShortName = _settings.UserShortName;
        MyRole = string.IsNullOrEmpty(_settings.UserRole) ? "Client" : _settings.UserRole;

        MyHwModel = string.IsNullOrEmpty(_settings.UserHwModel) ? "UNSET" : _settings.UserHwModel;
        RebroadcastMode = string.IsNullOrEmpty(_settings.RebroadcastMode) ? "ALL" : _settings.RebroadcastMode;
        HopLimit = Math.Clamp(_settings.HopLimit, 1, 7);
        MyPublicKey = _settings.UserPublicKey;
        MyPrivateKey = _settings.UserPrivateKey;

        // Ensure we always have a valid X25519 keypair so PKC direct messages
        // work out of the box. Generate one on first run (or if the stored key
        // is missing/corrupt); the change handlers persist it via SaveSettings.
        if (TryParseKeyBase64(MyPrivateKey).Length != 32)
        {
            var priv = Curve25519.GeneratePrivateKey();
            MyPrivateKey = Convert.ToBase64String(priv);            // derives + saves public key
            MyPublicKey = Convert.ToBase64String(Curve25519.GetPublicKey(priv));
        }

        HomeLatitude = _settings.HomeLatitude;
        HomeLongitude = _settings.HomeLongitude;
        // Populate the text boxes without retriggering UpdateHomeLocation: doing
        // so one box at a time would transiently null the not-yet-set coordinate
        // (and persist that null) before the second box is assigned.
        _suppressHomeTextUpdate = true;
        HomeLatitudeText = HomeLatitude?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
        HomeLongitudeText = HomeLongitude?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
        _suppressHomeTextUpdate = false;

        RebuildSlots(snapToDefault: false);
        // Restore the user's last slot/freq if it's still valid for this preset.
        if (_settings.Slot >= 1 && _settings.Slot <= Slots.Count)
        {
            SelectedSlot = _settings.Slot;
            CenterFreqMHz = _settings.CenterFreqMHz;
        }

        // Push gains into the native core so they take effect when RX starts.
        _core.SetGains(LnaGainDb, VgaGainDb, AmpEnable);

        // Select the persisted radio backend before probing names/status below.
        var deviceKind = Enum.TryParse<RadioDeviceKind>(_settings.DeviceKind, out var dk)
            ? dk : RadioDeviceKind.Auto;
        _core.SetDevice(deviceKind);
        DeviceOptions = BuildDeviceOptions();
        _suppressDeviceUpdate = true;
        SelectedDevice = DeviceOptions.FirstOrDefault(o => o.Kind == deviceKind)
                             ?? DeviceOptions[0];
        _suppressDeviceUpdate = false;

        // Now that the backend is known, push the gains appropriate for it and
        // apply the RTL-SDR bias-T option.
        PushGains();
        _core.SetDeviceOption("bias_tee", BiasTee ? 1 : 0);
        OnPropertyChanged(nameof(IsRtlSdr));
        OnPropertyChanged(nameof(IsHackRf));

        // Bring up channel and node tabs before logging anything, so boot
        // messages land on the Primary tab.
        ReloadChannels();
        UpsertSelf();
        ReloadNodes();
        ReloadMessages();
        LoadChatHistory();

        Status = $"Idle ({_core.DeviceName})";
        Log(DeviceBadge);
        if (!string.IsNullOrEmpty(_core.DeviceStatus))
            Log(_core.DeviceStatus);

        _settingsLoaded = true;
    }

    /// <summary>Refresh the in-memory <see cref="Nodes"/> collection from disk.</summary>
    public void ReloadNodes()
    {
        Nodes.Clear();
        // Our own node lives in the database so chats can show our name, but we
        // don't list ourselves among the discovered peers.
        foreach (var n in _nodeStore.All())
            if (_myNodeNum == 0 || n.NodeNum != _myNodeNum)
                Nodes.Add(n);
        // Keep any open DM tabs' telemetry panels in sync with the latest data.
        foreach (var convo in Tabs.OfType<ConversationViewModel>())
            convo.Node = Nodes.FirstOrDefault(n => n.NodeNum == convo.NodeNum);
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Record our own node in the node store so our name (rather than a bare
    /// node id) appears in chats, even though we ignore our own NodeInfo on the
    /// air. Called on identity changes; safe to call repeatedly.
    /// </summary>
    private void UpsertSelf()
    {
        if (_myNodeNum == 0) return;
        _nodeStore.Upsert(new NodeRecord
        {
            NodeNum = _myNodeNum,
            UserId = $"!{_myNodeNum:x8}",
            LongName = MyLongName ?? string.Empty,
            ShortName = MyShortName ?? string.Empty,
            HwModel = MyHwModel ?? string.Empty,
            Role = MyRole ?? string.Empty,
            PublicKey = Convert.ToHexString(TryParseKeyBase64(MyPublicKey)),
        });
    }

    /// <summary>Persist our own node record and refresh the in-memory node list
    /// so name changes are reflected immediately in chats.</summary>
    private void RefreshSelfNode()
    {
        if (!_settingsLoaded) return;
        UpsertSelf();
        ReloadNodes();
    }

    /// <summary>Refresh the in-memory <see cref="Messages"/> collection from disk.</summary>
    public void ReloadMessages()
    {
        Messages.Clear();
        foreach (var m in _messageStore.Recent()) Messages.Add(m);
    }

    /// <summary>
    /// Rebuild the channel chat rooms and direct-message conversation tabs from
    /// persisted history so messages survive restarts. They are only removed
    /// when the user explicitly clears them.
    /// </summary>
    public void LoadChatHistory()
    {
        // Start from a clean slate so a manual reload doesn't duplicate rows.
        foreach (var ch in Channels) ch.Messages.Clear();
        foreach (var convo in Tabs.OfType<ConversationViewModel>().ToList())
            Tabs.Remove(convo);

        // Rebuild channel (broadcast) chat rooms from history.
        foreach (var msg in _messageStore.TextHistory())
        {
            if (string.IsNullOrEmpty(msg.Text)) continue;

            bool isDm = msg.ToNode != 0xFFFFFFFFu &&
                        (msg.FromNode == _myNodeNum || msg.ToNode == _myNodeNum);
            if (isDm) continue; // DMs are restored per-conversation below.

            var chanVm = ResolveChannelTab(msg.Channel);
            if (chanVm is not null)
            {
                chanVm.Messages.Add(BuildHistoryMessage(msg));
                if (chanVm.Messages.Count > 1000) chanVm.Messages.RemoveAt(0);
            }
        }

        // Reopen a DM tab for every peer we have a conversation with (sent or
        // received), so closed/relaunched conversations reappear with history.
        if (_myNodeNum != 0)
        {
            foreach (var peer in _messageStore.ConversationPeers(_myNodeNum))
                OpenConversation(peer, NodeDisplayName(peer), focus: false);
        }

        // Restoring DM tabs moves selection; leave the primary channel focused.
        SelectedTab = Channels.FirstOrDefault();
    }

    /// <summary>Load the full persisted history for a peer into a conversation
    /// tab (idempotent: clears first so reopening doesn't duplicate rows).</summary>
    private void LoadConversationHistory(ConversationViewModel convo)
    {
        convo.Messages.Clear();
        if (_myNodeNum == 0) return;
        foreach (var msg in _messageStore.Conversation(convo.NodeNum, _myNodeNum))
        {
            if (string.IsNullOrEmpty(msg.Text)) continue;
            convo.Add(BuildHistoryMessage(msg));
        }
    }

    /// <summary>Turn a stored record into a <see cref="ChannelMessage"/>,
    /// restoring our own sends' persisted ACK/NAK delivery status.</summary>
    private ChannelMessage BuildHistoryMessage(MessageRecord msg)
    {
        bool outgoing = _myNodeNum != 0 && msg.FromNode == _myNodeNum;
        return new ChannelMessage
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(msg.RxEpoch).LocalDateTime,
            FromId = NodeDisplayName(msg.FromNode),
            Text = msg.Text,
            RssiDbm = msg.RssiDbfs,
            SnrDb = msg.SnrDb,
            PacketId = msg.PacketId,
            IsOutgoing = outgoing,
            Delivery = outgoing ? (MessageDelivery)msg.Delivery : MessageDelivery.None,
        };
    }

    /// <summary>
    /// Map a stored message's channel name to a channel tab. Falls back to the
    /// Primary tab when the stored name is a modem-preset name (the default
    /// channel is named after the preset, so its history was saved under
    /// whatever preset was active at the time — a later preset change must not
    /// orphan those messages).
    /// </summary>
    private ChannelViewModel? ResolveChannelTab(string? channelName)
    {
        var exact = Channels.FirstOrDefault(c =>
            string.Equals(c.Config.Name, channelName, StringComparison.Ordinal));
        if (exact is not null) return exact;

        if (!string.IsNullOrEmpty(channelName) &&
            Enum.GetNames<LoraPreset>().Contains(channelName))
        {
            return Channels.FirstOrDefault(c =>
                c.Config.Role == ChannelRole.Primary && c.Config.UsesDefaultKey);
        }
        return null;
    }

    /// <summary>Clear a channel's chat room (in-memory and persisted).</summary>
    [RelayCommand]
    private void ClearChannelMessages(ChannelViewModel? ch)
    {
        if (ch is null) return;
        ch.Messages.Clear();
        try { _messageStore.ClearChannel(ch.Config.Name); }
        catch (Exception ex) { Log($"clear channel failed: {ex.Message}"); }
    }

    /// <summary>Clear a direct-message conversation (in-memory and persisted).</summary>
    [RelayCommand]
    private void ClearConversationMessages(ConversationViewModel? convo)
    {
        if (convo is null) return;
        convo.Messages.Clear();
        try { _messageStore.ClearConversation(convo.NodeNum, _myNodeNum); }
        catch (Exception ex) { Log($"clear conversation failed: {ex.Message}"); }
    }

    /// <summary>
    /// Reload channels from disk. Bootstraps a default Primary channel
    /// (named after the current LoRa preset) on first run.
    /// </summary>
    public void ReloadChannels()
    {
        var existing = _channelStore.All().ToList();
        // Migration: any persisted PSK equal to the expanded default key is
        // collapsed back to the firmware's 1-byte sentinel (0x01 / "AQ==").
        foreach (var c in existing)
        {
            if (c.Psk.Length == ChannelConfig.DefaultPsk.Length &&
                c.Psk.AsSpan().SequenceEqual(ChannelConfig.DefaultPsk))
            {
                c.Psk = new byte[] { 0x01 };
                _channelStore.Upsert(c);
            }
        }
        if (existing.Count == 0)
        {
            var primary = new ChannelConfig
            {
                Index = 0,
                Name = SelectedPreset.ToString(),
                Psk = new byte[] { 0x01 }, // sentinel == firmware "default key"
                Role = ChannelRole.Primary,
            };
            _channelStore.Upsert(primary);
            existing.Add(primary);
        }

        Channels.Clear();
        foreach (var c in existing) Channels.Add(new ChannelViewModel(c, OnChannelSaved));
        SyncPrimaryChannelName();
        RebuildTabs();
        SelectedTab = Channels.FirstOrDefault();
    }

    /// <summary>
    /// Keep the default Primary channel's tab name in sync with the active
    /// modem preset. Only auto-named default channels are renamed (name empty
    /// or equal to a preset name, and still using the firmware default key); a
    /// user's custom primary name is left untouched.
    /// </summary>
    private void SyncPrimaryChannelName()
    {
        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null) return;
        var cfg = primary.Config;
        if (!cfg.UsesDefaultKey) return;
        var presetName = SelectedPreset.ToString();
        var autoNamed = string.IsNullOrEmpty(cfg.Name) ||
                        Enum.GetNames<LoraPreset>().Contains(cfg.Name);
        if (!autoNamed || cfg.Name == presetName) return;
        primary.RenameTo(presetName);
        _channelStore.Upsert(cfg);
    }

    /// <summary>Rebuild <see cref="Tabs"/> = channels + preserved DM conversations.</summary>
    private void RebuildTabs()
    {
        var convos = Tabs.OfType<ConversationViewModel>().ToList();
        Tabs.Clear();
        foreach (var c in Channels) Tabs.Add(c);
        foreach (var v in convos) Tabs.Add(v);
    }

    private void OnChannelSaved(ChannelConfig cfg)
    {
        // Firmware invariant: exactly one channel may have role Primary.
        if (cfg.Role == ChannelRole.Primary)
        {
            foreach (var ch in Channels)
            {
                if (ch.Config.Index != cfg.Index && ch.Config.Role == ChannelRole.Primary)
                {
                    ch.Config.Role = ChannelRole.Secondary;
                    _channelStore.Upsert(ch.Config);
                }
            }
        }
        _channelStore.Upsert(cfg);
        // Refresh the tab header / collection so the * marker moves.
        var idx = Channels.IndexOf(Channels.First(c => c.Config.Index == cfg.Index));
        var keepSelected = SelectedChannel?.Config.Index;
        ReloadChannels();
        // ReloadChannels rebuilds the channel view models with empty message
        // lists, so repopulate them from history (otherwise saving settings
        // appears to wipe the chat).
        LoadChatHistory();
        if (keepSelected is int wanted)
            SelectedTab = Channels.FirstOrDefault(c => c.Config.Index == wanted)
                              ?? (object?)Channels.FirstOrDefault();
    }

    /// <summary>Persist a channel and refresh the UI.</summary>
    public void SaveChannel(ChannelConfig cfg)
    {
        _channelStore.Upsert(cfg);
        ReloadChannels();
    }

    [RelayCommand]
    private void AddChannel()
    {
        var taken = Channels.Select(c => c.Config.Index).ToHashSet();
        int idx = -1;
        for (int i = 1; i < 8; i++) if (!taken.Contains(i)) { idx = i; break; }
        if (idx < 0) return; // 8-channel cap matches firmware
        var cfg = new ChannelConfig
        {
            Index = idx,
            Name = $"Channel {idx}",
            Psk = ChannelConfig.NewRandomPsk(),
            Role = ChannelRole.Secondary,
        };
        _channelStore.Upsert(cfg);
        ReloadChannels();
        SelectedTab = Channels.FirstOrDefault(c => c.Config.Index == idx);
    }

    [RelayCommand]
    private void RemoveSelectedChannel()
    {
        var ch = SelectedChannel;
        if (ch is null) return;
        if (ch.Config.Role == ChannelRole.Primary) return; // never delete primary
        _channelStore.Delete(ch.Config.Index);
        ReloadChannels();
    }

    /// <summary>
    /// Append a timestamped line to the global running log. The log is shared
    /// across all tabs (channels and direct messages), not channel-specific.
    /// </summary>
    private void Log(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        LogLines.Add(line);
        if (LogLines.Count > 500) LogLines.RemoveAt(0);
    }

    /// <summary>Copy the entire global log to the clipboard.</summary>
    [RelayCommand]
    private void CopyLog()
    {
        if (LogLines.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, LogLines)); }
        catch { /* clipboard contention; ignore */ }
    }

    /// <summary>Clear the global log.</summary>
    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    partial void OnSelectedPresetChanged(LoraPreset value) { RebuildSlots(snapToDefault: true); RetuneIfRunning(); SyncPrimaryChannelName(); SaveSettings(); }
    partial void OnSelectedRegionChanged(Region value)     { RebuildSlots(snapToDefault: true); RetuneIfRunning(); SaveSettings(); }
    partial void OnSelectedSlotChanged(int value)
    {
        if (_suppressSlotSync || value <= 0) return;
        CenterFreqMHz = ChannelPlan.FrequencyMHz(SelectedRegion, SelectedPreset, value);
        SaveSettings();
    }
    partial void OnCenterFreqMHzChanged(double value) { if (!_suppressRetune) RetuneIfRunning(); SaveSettings(); OnPropertyChanged(nameof(SpectrumCenterHz)); }
    partial void OnLnaGainDbChanged(byte value) { _core.SetGains(value, VgaGainDb, AmpEnable); SaveSettings(); }
    partial void OnVgaGainDbChanged(byte value) { _core.SetGains(LnaGainDb, value, AmpEnable); SaveSettings(); }
    partial void OnAmpEnableChanged(bool value) { PushGains(); SaveSettings(); }
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanSelectDevice));
    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        OnPropertyChanged(nameof(IsRtlSdr));
        OnPropertyChanged(nameof(IsHackRf));
        if (_suppressDeviceUpdate || value is null) return;
        ApplyDevice(value.Kind);
    }
    partial void OnAgcEnableChanged(bool value) { SaveSettings(); }
    partial void OnAgcTargetDbfsChanged(double value) { SaveSettings(); }
    partial void OnRtlGainDbChanged(byte value) { PushGains(); SaveSettings(); }
    partial void OnBiasTeeChanged(bool value) { _core.SetDeviceOption("bias_tee", value ? 1 : 0); SaveSettings(); }

    /// <summary>Push the gain settings appropriate for the selected backend.
    /// RTL-SDR uses its single manual tuner gain (or auto when AGC is on);
    /// HackRF and friends use the LNA/VGA/AMP model.</summary>
    private void PushGains()
    {
        if (IsRtlSdr)
            _core.SetGains(RtlGainDb, 0, AmpEnable);
        else
            _core.SetGains(LnaGainDb, VgaGainDb, AmpEnable);
    }
    partial void OnThemeChanged(string value) { ThemeManager.Apply(value); SaveSettings(); }
    partial void OnWaterfallColormapChanged(string value) { SaveSettings(); }
    partial void OnWaterfallAutoLevelsChanged(bool value) { SaveSettings(); }
    partial void OnWaterfallFloorDbChanged(double value) { SaveSettings(); }
    partial void OnWaterfallCeilDbChanged(double value) { SaveSettings(); }

    private void SaveSettings()
    {
        if (!_settingsLoaded) return;
        _settings.Region = SelectedRegion.ToString();
        _settings.Preset = SelectedPreset.ToString();
        _settings.Slot = SelectedSlot;
        _settings.CenterFreqMHz = CenterFreqMHz;
        _settings.LnaGainDb = LnaGainDb;
        _settings.VgaGainDb = VgaGainDb;
        _settings.AmpEnable = AmpEnable;
        _settings.DeviceKind = SelectedDevice?.Kind.ToString() ?? "Auto";
        _settings.AgcEnable = AgcEnable;
        _settings.AgcTargetDbfs = AgcTargetDbfs;
        _settings.RtlGainDb = RtlGainDb;
        _settings.BiasTee = BiasTee;
        _settings.Theme = Theme;
        _settings.WaterfallColormap = WaterfallColormap;
        _settings.WaterfallAutoLevels = WaterfallAutoLevels;
        _settings.WaterfallFloorDb = WaterfallFloorDb;
        _settings.WaterfallCeilDb = WaterfallCeilDb;
        _settings.UserNodeNum = _myNodeNum;
        _settings.UserLongName = MyLongName ?? string.Empty;
        _settings.UserShortName = MyShortName ?? string.Empty;
        _settings.UserRole = MyRole ?? "Client";
        _settings.UserHwModel = MyHwModel ?? "UNSET";
        _settings.RebroadcastMode = RebroadcastMode ?? "ALL";
        _settings.HopLimit = Math.Clamp(HopLimit, 1, 7);
        _settings.UserPublicKey = MyPublicKey ?? string.Empty;
        _settings.UserPrivateKey = MyPrivateKey ?? string.Empty;
        _settings.HomeLatitude = HomeLatitude;
        _settings.HomeLongitude = HomeLongitude;
        _settings.Save();
    }

    // -- Identity change handlers -------------------------------------------

    partial void OnMyNodeIdTextChanged(string value)
    {
        _myNodeNum = ParseNodeId(value);
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
        SaveSettings();
        RefreshSelfNode();
    }

    partial void OnMyLongNameChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnMyShortNameChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnMyRoleChanged(string value) => SaveSettings();
    partial void OnMyHwModelChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnRebroadcastModeChanged(string value) => SaveSettings();

    partial void OnHopLimitChanged(int value)
    {
        // Keep within the firmware-valid 1..7 range; re-clamping triggers this
        // handler again only when the value actually changes.
        var clamped = Math.Clamp(value, 1, 7);
        if (clamped != value) { HopLimit = clamped; return; }
        SaveSettings();
    }
    partial void OnMyPublicKeyChanged(string value) => SaveSettings();

    partial void OnMyPrivateKeyChanged(string value)
    {
        // Derive the matching public key whenever a valid 32-byte private key
        // is entered, so the pair always stays consistent.
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                var priv = Convert.FromBase64String(value.Trim());
                if (priv.Length == 32)
                    MyPublicKey = Convert.ToBase64String(Curve25519.GetPublicKey(priv));
            }
            catch { /* not valid base64 / wrong length — leave public key as-is */ }
        }
        SaveSettings();
    }

    partial void OnHomeLatitudeTextChanged(string value)
    {
        if (!_suppressHomeTextUpdate) UpdateHomeLocation();
    }

    partial void OnHomeLongitudeTextChanged(string value)
    {
        if (!_suppressHomeTextUpdate) UpdateHomeLocation();
    }

    /// <summary>Re-parse the home lat/lon text boxes, persist, and notify the map.
    /// Each coordinate is parsed independently so an empty/partial value in one
    /// box can never clobber the other (e.g. while typing a negative longitude).</summary>
    private void UpdateHomeLocation()
    {
        // Empty box clears that coordinate; a non-empty box only updates when it
        // parses to a valid in-range value (ignores partial input like "-").
        HomeLatitude = string.IsNullOrWhiteSpace(HomeLatitudeText)
            ? null : (TryParseCoord(HomeLatitudeText, -90, 90) ?? HomeLatitude);
        HomeLongitude = string.IsNullOrWhiteSpace(HomeLongitudeText)
            ? null : (TryParseCoord(HomeLongitudeText, -180, 180) ?? HomeLongitude);
        SaveSettings();
        SendPositionCommand.NotifyCanExecuteChanged();
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Set the home location from a map click (lat/lon in degrees).</summary>
    public void SetHomeLocation(double lat, double lon)
    {
        // Update the backing values atomically so the map sees a complete home
        // marker in a single refresh (rather than the half-set intermediate
        // state produced by updating the two text boxes one at a time).
        _suppressHomeTextUpdate = true;
        HomeLatitudeText = lat.ToString("0.######", CultureInfo.InvariantCulture);
        HomeLongitudeText = lon.ToString("0.######", CultureInfo.InvariantCulture);
        _suppressHomeTextUpdate = false;

        HomeLatitude = lat;
        HomeLongitude = lon;
        SaveSettings();
        SendPositionCommand.NotifyCanExecuteChanged();
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _suppressHomeTextUpdate;

    private static double? TryParseCoord(string? text, double min, double max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), NumberStyles.Float,
                   CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
               ? v : null;
    }

    /// <summary>Generate a fresh random 32-byte private key (and clear the
    /// public key, which will be derived when TX/PKI is implemented).</summary>
    [RelayCommand]
    private void GenerateKeyPair()
    {
        var priv = Curve25519.GeneratePrivateKey();
        var pub = Curve25519.GetPublicKey(priv);
        MyPrivateKey = Convert.ToBase64String(priv);
        MyPublicKey = Convert.ToBase64String(pub);
    }

    /// <summary>Parse a node id like <c>!a1b2c3d4</c>, <c>0xA1B2C3D4</c> or a
    /// decimal number into a 32-bit node number. Returns 0 if unparseable.</summary>
    private static uint ParseNodeId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var s = text.Trim();
        if (s.StartsWith("!", StringComparison.Ordinal)) s = s[1..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            return dec;
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
            ? hex : 0;
    }

    // -- Direct-message conversations ---------------------------------------

    /// <summary>Open (or focus) a DM conversation tab for the given node.</summary>
    /// <param name="nodeNum">Peer node number.</param>
    /// <param name="name">Optional display name to set/refresh on the tab.</param>
    /// <param name="focus">When true, select the tab. Incoming DMs open the tab
    /// in the background (focus = false) so an unsolicited message doesn't yank
    /// the user away from what they're viewing.</param>
    public ConversationViewModel OpenConversation(uint nodeNum, string? name = null,
                                                  bool focus = true)
    {
        var existing = Tabs.OfType<ConversationViewModel>()
                           .FirstOrDefault(c => c.NodeNum == nodeNum);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(name)) existing.PeerName = name!;
            existing.Node = Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
            if (focus) SelectedTab = existing;
            return existing;
        }

        var convo = new ConversationViewModel(nodeNum, name ?? NodeDisplayName(nodeNum));
        convo.Node = Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
        // Restore this peer's prior message history so reopening a closed tab
        // (or relaunching the app) shows the existing conversation.
        LoadConversationHistory(convo);
        Tabs.Add(convo);
        if (focus) SelectedTab = convo;
        return convo;
    }

    /// <summary>Open a DM tab for a node row (double-clicked in the table).</summary>
    /// <summary>Assign a fresh random 32-bit node id (avoids 0 and broadcast).</summary>
    [RelayCommand]
    private void GenerateRandomNodeId()
    {
        uint id;
        do { id = (uint)Random.Shared.Next(int.MinValue, int.MaxValue); }
        while (id == 0 || id == 0xFFFFFFFF);
        MyNodeIdText = $"!{id:x8}";
    }

    [RelayCommand]
    private void OpenConversationForNode(NodeRecord? node)
    {
        if (node is null) return;
        // Don't open a DM tab for ourselves: you can't message your own node.
        if (_myNodeNum != 0 && node.NodeNum == _myNodeNum) return;
        OpenConversation(node.NodeNum, NodeDisplayName(node.NodeNum));
    }

    [RelayCommand]
    private void CloseTab(object? tab)
    {
        if (tab is ConversationViewModel convo)
        {
            int idx = Tabs.IndexOf(convo);
            Tabs.Remove(convo);
            if (ReferenceEquals(SelectedTab, convo))
                SelectedTab = Tabs.Count > 0
                    ? Tabs[Math.Min(idx, Tabs.Count - 1)]
                    : null;
        }
    }

    /// <summary>Best-known display name for a node number.</summary>
    private string NodeDisplayName(uint nodeNum)
    {
        // We're not in the Nodes list, so resolve our own name directly.
        if (_myNodeNum != 0 && nodeNum == _myNodeNum)
        {
            if (!string.IsNullOrWhiteSpace(MyLongName)) return MyLongName!;
            if (!string.IsNullOrWhiteSpace(MyShortName)) return MyShortName!;
        }
        var rec = Nodes.FirstOrDefault(n => n.NodeNum == nodeNum)
                  ?? _nodeStore.Get(nodeNum);
        if (rec is not null)
        {
            if (!string.IsNullOrWhiteSpace(rec.LongName)) return rec.LongName!;
            if (!string.IsNullOrWhiteSpace(rec.ShortName)) return rec.ShortName!;
        }
        return $"!{nodeNum:x8}";
    }

    /// <summary>Build the device-backend selector list with an availability
    /// annotation for the hardware backends.</summary>
    private IReadOnlyList<DeviceOption> BuildDeviceOptions()
    {
        string Label(RadioDeviceKind kind, string name) =>
            _core.IsDeviceAvailable(kind) ? name : $"{name} (not found)";
        return new[]
        {
            new DeviceOption(RadioDeviceKind.Auto, "Auto-detect"),
            new DeviceOption(RadioDeviceKind.HackRf, Label(RadioDeviceKind.HackRf, "HackRF")),
            new DeviceOption(RadioDeviceKind.RtlSdr, Label(RadioDeviceKind.RtlSdr, "RTL-SDR")),
            new DeviceOption(RadioDeviceKind.Null, "Synthetic (no hardware)"),
        };
    }

    /// <summary>Switch the active radio backend (only valid while stopped) and
    /// refresh the device badge / status.</summary>
    private void ApplyDevice(RadioDeviceKind kind)
    {
        if (IsRunning) return;
        _core.SetDevice(kind);
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(DeviceStatus));
        OnPropertyChanged(nameof(HasRealRadio));
        OnPropertyChanged(nameof(DeviceBadge));
        OnPropertyChanged(nameof(CanTransmit));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
        Status = $"Idle ({_core.DeviceName})";
        Log(DeviceBadge);
        if (!string.IsNullOrEmpty(_core.DeviceStatus))
            Log(_core.DeviceStatus);
        SaveSettings();
    }

    /// <summary>Restart the receiver with the current parameters if it's running.</summary>
    private void RetuneIfRunning()
    {
        if (!IsRunning) return;
        try
        {
            _core.Stop();
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            _core.StartRx(SelectedPreset, hz);
            Status = $"RX @ {CenterFreqMHz:F3} MHz / {SelectedPreset}";
            Log($"retuned \u2192 {Status}");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Status = $"Error: {ex.Message}";
            Log(Status);
        }
    }

    private void RebuildSlots(bool snapToDefault = false)
    {
        var count = ChannelPlan.SlotCount(SelectedRegion, SelectedPreset);
        var preferred = ChannelPlan.DefaultSlot(SelectedRegion, SelectedPreset);

        int desired;
        if (snapToDefault || SelectedSlot < 1 || SelectedSlot > count)
            desired = preferred;
        else
            desired = SelectedSlot;

        // Suppress the SelectedSlot side-effect while we swap the items list,
        // so the ComboBox doesn't briefly see SelectedItem=null and push that
        // back through the binding (which would fail validation on int).
        _suppressSlotSync = true;
        try
        {
            // Replace the entire collection in one atomic notification rather
            // than Clear()+Add() in a loop. This avoids the WPF binding race
            // that turns the ComboBox red when switching presets.
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

    [RelayCommand]
    private void StartRx()
    {
        try
        {
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            _core.StartRx(SelectedPreset, hz);
            IsRunning = true;
            Status = $"RX @ {CenterFreqMHz:F3} MHz / {SelectedPreset}";
            Log(Status);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Status = $"Error: {ex.Message}";
            Log(Status);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _core.Stop();
        IsRunning = false;
        IsCapturing = false;
        Status = "Stopped";
        Log(Status);
    }

    // -- Transmit (HackRF only) ---------------------------------------------

    /// <summary>True when the active radio backend can transmit (HackRF).</summary>
    public bool CanTransmit => _core.CanTransmit;

    /// <summary>Text typed into the per-channel compose box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _composeText = string.Empty;

    /// <summary>HackRF TX VGA gain in dB (0..47). Default to max for range.</summary>
    [ObservableProperty]
    private byte _txGainDb = 47;

    // Per-packet id seed; Meshtastic uses a 32-bit packet id (also the CTR
    // nonce). Start random and increment, keeping it non-zero.
    private uint _txPacketId = (uint)Random.Shared.Next(1, int.MaxValue);

    // Outgoing messages awaiting an ACK, keyed by their packet id. When a
    // ROUTING ack/nak arrives referencing the id we mark the message
    // delivered/failed; entries that age out are flagged as un-acked.
    private readonly Dictionary<uint, PendingAck> _pendingAcks = new();

    /// <summary>How long to wait for an ACK before flagging a DM as un-acked.</summary>
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);

    private sealed record PendingAck(ChannelMessage Message, DateTime SentUtc);

    private uint NextPacketId()
    {
        _txPacketId++;
        if (_txPacketId == 0) _txPacketId = 1;
        return _txPacketId;
    }

    private bool CanSendMessage() =>
        CanTransmit &&
        SelectedChannel is not null &&
        !string.IsNullOrWhiteSpace(ComposeText);

    /// <summary>
    /// Encode the composed text as a Meshtastic TEXT_MESSAGE_APP frame on the
    /// selected channel and transmit it on the current preset/frequency. The
    /// sent line is echoed into the channel's message list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private void SendMessage()
    {
        var ch = SelectedChannel;
        if (ch is null) return;
        var text = (ComposeText ?? string.Empty).Trim();
        if (text.Length == 0) return;

        if (_myNodeNum == 0)
        {
            Status = "Set your node ID (Identity) before sending.";
            Log(Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodeTextMessage(
                ch.Config, _myNodeNum, packetId, text, hopLimit: (byte)HopLimit);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = _core.Transmit(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
            if (ok)
            {
                ch.Messages.Add(new ChannelMessage
                {
                    FromId = NodeDisplayName(_myNodeNum),
                    Text = text,
                });
                if (ch.Messages.Count > 1000) ch.Messages.RemoveAt(0);
                PersistOutgoingText(0xFFFFFFFFu, packetId, text, ch.Config.Name);
                ComposeText = string.Empty;
                Status = $"Sent {frame.Length} B on {ch.DisplayName}";
            }
            else
            {
                Status = "Transmit failed (device cannot transmit).";
            }
            Log(Status);
        }
        catch (Exception ex)
        {
            Status = $"Send error: {ex.Message}";
            Log(Status);
        }
    }

    /// <summary>
    /// Encode the conversation's composed text as a Meshtastic TEXT_MESSAGE_APP
    /// frame addressed to a single peer (a direct message) and transmit it.
    ///
    /// When we hold our X25519 private key and the peer's public key (learned
    /// from their NODEINFO), the DM is sealed with PKC (X25519 + AES-CCM,
    /// channel hash 0x00) exactly like modern Meshtastic firmware — so real
    /// nodes will surface it. Otherwise it falls back to a legacy channel-PSK
    /// DM on the primary channel (which modern firmware will reject, but which
    /// works between two instances of this app sharing the channel key). The
    /// actual transmit only succeeds on a TX-capable radio (HackRF).
    /// </summary>
    [RelayCommand]
    private void SendDirectMessage(ConversationViewModel? convo)
    {
        if (convo is null) return;
        var text = (convo.ComposeText ?? string.Empty).Trim();
        if (text.Length == 0) return;

        if (_myNodeNum == 0)
        {
            Status = "Set your node ID (Identity) before sending.";
            Log(Status);
            return;
        }

        // Prefer PKC: seal with our private key + the peer's public key when
        // both are available (matches modern firmware; decodes on real nodes).
        var myPriv = TryParseKeyBase64(MyPrivateKey);
        var peerPub = TryParseHex(_nodeStore.Get(convo.NodeNum)?.PublicKey);
        bool usePkc = myPriv.Length == 32 && peerPub.Length == 32;

        // Make the chosen path (and any missing prerequisite) visible: modern
        // firmware rejects legacy channel-PSK DMs, so a silent fallback looks
        // like the message simply vanished. Tell the user exactly what's wrong.
        if (!usePkc)
        {
            if (myPriv.Length != 32)
                Log("  DM: no local X25519 private key — cannot seal with PKC.");
            else if (peerPub.Length != 32)
            {
                Log($"  DM: no public key known for {convo.TabHeader} yet — "
                  + "requesting their NodeInfo so the next DM can use PKC. "
                  + "Modern nodes reject legacy DMs; retry once their key arrives.");
                // Proactively pull the peer's key by sending our NodeInfo with
                // want_response directed at them (firmware replies with theirs).
                RequestNodeInfo(convo.NodeNum);
            }
        }

        // Legacy fallback rides the primary channel's key (index 0), matching
        // how inbound legacy DMs are decrypted and routed to a conversation.
        var ch = Channels.FirstOrDefault();
        if (!usePkc && ch is null)
        {
            Status = "No channel configured to carry the direct message.";
            Log(Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            byte[] frame = usePkc
                ? MeshEncoder.EncodePkcTextMessage(
                      _myNodeNum, convo.NodeNum, packetId, text,
                      myPriv, peerPub, hopLimit: (byte)HopLimit, wantAck: true)
                : MeshEncoder.EncodeTextMessage(
                      ch!.Config, _myNodeNum, packetId, text,
                      to: convo.NodeNum, hopLimit: (byte)HopLimit, wantAck: true);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = _core.Transmit(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
            if (ok)
            {
                var sent = new ChannelMessage
                {
                    FromId = NodeDisplayName(_myNodeNum),
                    Text = text,
                    PacketId = packetId,
                    IsOutgoing = true,
                    Delivery = MessageDelivery.Sent,
                };
                convo.Add(sent);
                TrackPendingAck(sent);
                PersistOutgoingText(convo.NodeNum, packetId, text,
                                    usePkc ? "PKC" : (ch?.Config.Name ?? string.Empty));
                convo.ComposeText = string.Empty;
                Status = usePkc
                    ? $"DM (PKC) sent {frame.Length} B to {convo.TabHeader}"
                    : $"DM (legacy PSK) sent {frame.Length} B to {convo.TabHeader}";
            }
            else
            {
                Status = "Transmit failed (device cannot transmit).";
            }
            Log(Status);
        }
        catch (Exception ex)
        {
            Status = $"DM send error: {ex.Message}";
            Log(Status);
        }
    }

    private bool CanSendNodeInfo() => CanTransmit && _myNodeNum != 0;

    /// <summary>
    /// Broadcast our identity (NODEINFO_APP <c>User</c> protobuf) on the
    /// primary channel so peers learn our node id / name / role. Always sent on
    /// the primary channel (firmware behaviour), regardless of the active tab.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendNodeInfo))]
    private void SendNodeInfo()
    {
        if (_myNodeNum == 0)
        {
            Status = "Set your node ID (Identity) before sending node info.";
            Log(Status);
            return;
        }

        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null)
        {
            Status = "No primary channel to send node info on.";
            Log(Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            uint role = RoleEnumValue(MyRole);
            byte[] pubKey = TryParseKeyBase64(MyPublicKey);
            var frame = MeshEncoder.EncodeNodeInfo(
                primary.Config, _myNodeNum, packetId,
                MyLongName ?? string.Empty, MyShortName ?? string.Empty,
                hwModel: 0, role: role, publicKey: pubKey, hopLimit: (byte)HopLimit);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = _core.Transmit(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
            Status = ok
                ? $"Sent node info ({frame.Length} B) on {primary.DisplayName}"
                : "Transmit failed (device cannot transmit).";
            Log(Status);
        }
        catch (Exception ex)
        {
            Status = $"Node info error: {ex.Message}";
            Log(Status);
        }
    }

    private bool CanSendPosition() =>
        CanTransmit && _myNodeNum != 0 &&
        HomeLatitude is not null && HomeLongitude is not null;

    /// <summary>
    /// Broadcast our location (POSITION_APP) on the primary channel, fuzzed to
    /// that channel's position precision (firmware behaviour). Uses the home
    /// latitude/longitude configured in settings.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendPosition))]
    private void SendPosition()
    {
        if (_myNodeNum == 0)
        {
            Status = "Set your node ID (Identity) before sending position.";
            Log(Status);
            return;
        }

        if (HomeLatitude is not double lat || HomeLongitude is not double lon)
        {
            Status = "Set your home latitude/longitude (Identity) before sending position.";
            Log(Status);
            return;
        }

        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null)
        {
            Status = "No primary channel to send position on.";
            Log(Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodePosition(
                primary.Config, _myNodeNum, packetId,
                lat, lon, altitudeM: null,
                precisionBits: primary.Config.PositionPrecision,
                hopLimit: (byte)HopLimit);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = _core.Transmit(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
            Status = ok
                ? $"Sent position ({frame.Length} B) on {primary.DisplayName}"
                : "Transmit failed (device cannot transmit).";
            Log(Status);
        }
        catch (Exception ex)
        {
            Status = $"Position error: {ex.Message}";
            Log(Status);
        }
    }

    /// <summary>
    /// Send our NodeInfo directed at <paramref name="to"/> with
    /// <c>want_response</c> set, prompting that node to reply with its own
    /// NodeInfo — the standard Meshtastic way to learn a peer's public key
    /// before a PKC direct message. No-op when we can't transmit.
    /// </summary>
    private void RequestNodeInfo(uint to)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;
        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null) return;

        try
        {
            uint packetId = NextPacketId();
            uint role = RoleEnumValue(MyRole);
            byte[] pubKey = TryParseKeyBase64(MyPublicKey);
            var frame = MeshEncoder.EncodeNodeInfo(
                primary.Config, _myNodeNum, packetId,
                MyLongName ?? string.Empty, MyShortName ?? string.Empty,
                hwModel: 0, role: role, publicKey: pubKey,
                to: to, hopLimit: (byte)HopLimit, wantResponse: true);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            if (_core.Transmit(SelectedPreset, hz, frame, TxGainDb, AmpEnable))
                Log($"  requested NodeInfo from !{to:x8}");
        }
        catch (Exception ex)
        {
            Log($"  NodeInfo request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reply to a directed NodeInfo request with our NodeInfo (no want_response,
    /// to avoid a request/response loop) so the requester learns our public key.
    /// </summary>
    private void RequestNodeInfoReply(uint to)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;
        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null) return;

        try
        {
            uint packetId = NextPacketId();
            uint role = RoleEnumValue(MyRole);
            byte[] pubKey = TryParseKeyBase64(MyPublicKey);
            var frame = MeshEncoder.EncodeNodeInfo(
                primary.Config, _myNodeNum, packetId,
                MyLongName ?? string.Empty, MyShortName ?? string.Empty,
                hwModel: 0, role: role, publicKey: pubKey,
                to: to, hopLimit: (byte)HopLimit, wantResponse: false);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            _core.Transmit(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
        }
        catch (Exception ex)
        {
            Log($"  NodeInfo reply failed: {ex.Message}");
        }
    }

    // -- ACK / NAK tracking --------------------------------------------------

    /// <summary>Register an outgoing message so an inbound ROUTING ack/nak can
    /// flip its delivery status.</summary>
    private void TrackPendingAck(ChannelMessage message)
    {
        if (message.PacketId == 0) return;
        _pendingAcks[message.PacketId] = new PendingAck(message, DateTime.UtcNow);
    }

    /// <summary>Persist an outgoing text message we transmitted so it survives a
    /// restart (received messages are stored in <see cref="DecodePayloadIfPossible"/>;
    /// our own sends are never heard back as decodable, so store them here).</summary>
    private void PersistOutgoingText(uint to, uint packetId, string text, string channel)
    {
        try
        {
            _messageStore.Add(new MessageRecord
            {
                PacketId = packetId,
                FromNode = _myNodeNum,
                ToNode = to,
                Channel = channel ?? string.Empty,
                PortNum = (int)PortNum.TextMessage,
                Text = text,
                Decrypted = true,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Delivery = (int)MessageDelivery.Sent,
            });
        }
        catch (Exception ex) { Log($"message store failed: {ex.Message}"); }
    }

    /// <summary>Flag outgoing DMs that never got an ACK within the timeout.</summary>
    private void SweepPendingAcks()
    {
        if (_pendingAcks.Count == 0) return;
        var now = DateTime.UtcNow;
        List<uint>? expired = null;
        foreach (var kv in _pendingAcks)
        {
            if (now - kv.Value.SentUtc < AckTimeout) continue;
            (expired ??= new()).Add(kv.Key);
        }
        if (expired is null) return;
        foreach (var id in expired)
        {
            if (_pendingAcks.Remove(id, out var pending) &&
                pending.Message.Delivery == MessageDelivery.Sent)
            {
                pending.Message.Delivery = MessageDelivery.Failed;
                PersistDelivery(pending.Message);
            }
        }
    }

    /// <summary>Handle an inbound ROUTING_APP packet addressed to us: match its
    /// request_id to a message we sent and mark it delivered (ACK) or failed
    /// (NAK), mirroring firmware <c>Router::handleReceived</c> ack handling.</summary>
    private void HandleRouting(MeshHeader header, MeshDecodeResult result)
    {
        if (_myNodeNum == 0 || header.To != _myNodeNum) return;
        if (result.RequestId == 0) return;
        if (!_pendingAcks.Remove(result.RequestId, out var pending)) return;

        bool ack = result.RoutingError == 0;
        pending.Message.Delivery = ack ? MessageDelivery.Delivered : MessageDelivery.Failed;
        PersistDelivery(pending.Message);
        Log(ack
            ? $"  ACK from {NodeDisplayName(header.From)} for id {result.RequestId:x8}"
            : $"  NAK ({result.RoutingError}) from {NodeDisplayName(header.From)} for id {result.RequestId:x8}");
    }

    /// <summary>Persist a sent message's delivery state so its ACK/NAK status
    /// survives a restart (the row was written by PersistOutgoingText).</summary>
    private void PersistDelivery(ChannelMessage message)
    {
        if (message.PacketId == 0 || _myNodeNum == 0) return;
        try { _messageStore.UpdateDelivery(message.PacketId, _myNodeNum, (int)message.Delivery); }
        catch (Exception ex) { Log($"delivery update failed: {ex.Message}"); }
    }

    /// <summary>Send a ROUTING_APP acknowledgement back to the sender of a
    /// received unicast packet that requested one (want_ack). PKC frames are
    /// acked with PKC, channel frames on the same channel.</summary>
    private void SendAck(MeshHeader origHeader, MeshDecodeResult result)
    {
        if (!CanTransmit || _myNodeNum == 0) return;
        if (origHeader.IsBroadcast || origHeader.To != _myNodeNum) return;

        try
        {
            uint packetId = NextPacketId();
            byte[]? frame = null;
            bool pkc = result.ChannelName == "PKC";

            if (pkc)
            {
                var myPriv = TryParseKeyBase64(MyPrivateKey);
                var peerPub = TryParseHex(_nodeStore.Get(origHeader.From)?.PublicKey);
                if (myPriv.Length == 32 && peerPub.Length == 32)
                    frame = MeshEncoder.EncodePkcRouting(
                        _myNodeNum, origHeader.From, packetId, origHeader.PacketId,
                        myPriv, peerPub, errorReason: 0, hopLimit: (byte)HopLimit);
            }
            else
            {
                var ch = Channels.FirstOrDefault(c => c.Config.Name == result.ChannelName)
                         ?? Channels.FirstOrDefault();
                if (ch is not null)
                    frame = MeshEncoder.EncodeRouting(
                        ch.Config, _myNodeNum, origHeader.From, packetId,
                        origHeader.PacketId, errorReason: 0, hopLimit: (byte)HopLimit);
            }

            if (frame is null) return;
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            if (_core.Transmit(SelectedPreset, hz, frame, TxGainDb, AmpEnable))
                Log($"  sent ACK to {NodeDisplayName(origHeader.From)} for id {origHeader.PacketId:x8}");
        }
        catch (Exception ex)
        {
            Log($"  ACK send failed: {ex.Message}");
        }
    }

    // meshtastic Config.DeviceConfig.Role enum values (order differs from the
    // UI option list, so map by name rather than index).
    private static uint RoleEnumValue(string? role) => role switch
    {
        "Client"       => 0,
        "ClientMute"   => 1,
        "Router"       => 2,
        "RouterClient" => 3,
        "Repeater"     => 4,
        "Tracker"      => 5,
        "Sensor"       => 6,
        "TAK"          => 7,
        "ClientHidden" => 8,
        "LostAndFound" => 9,
        "TakTracker"   => 10,
        _              => 0,
    };

    // Parse a hex string (optional whitespace / colons) into bytes; returns an
    // empty array when the input is blank or malformed.
    private static byte[] TryParseHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<byte>();
        var clean = new string(s.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length == 0 || (clean.Length & 1) != 0) return Array.Empty<byte>();
        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return bytes;
    }

    // Parse a base64-encoded X25519 key (how our keypair is stored/displayed)
    // into 32 bytes; returns empty when blank, malformed, or the wrong length.
    private static byte[] TryParseKeyBase64(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<byte>();
        try
        {
            var bytes = Convert.FromBase64String(s.Trim());
            return bytes.Length == 32 ? bytes : Array.Empty<byte>();
        }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>
    /// Attempt to decrypt a direct message sealed with PKC (X25519 + AES-CCM)
    /// using our private key and the sender's stored public key. Returns null
    /// when we lack a key, the sender is unknown, or the tag doesn't verify.
    /// </summary>
    private MeshDecodeResult? TryDecodePkc(byte[] frame, MeshHeader header)
    {
        var myPriv = TryParseKeyBase64(MyPrivateKey);
        if (myPriv.Length != 32) return null;

        var sender = _nodeStore.Get(header.From);
        var senderPub = TryParseHex(sender?.PublicKey);
        if (senderPub.Length != 32) return null;

        try { return MeshDecoder.DecodePkc(frame, myPriv, senderPub); }
        catch { return null; }
    }

    /// <summary>Single bound command for the toolbar toggle button.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (IsRunning) Stop();
        else           StartRx();
    }

    /// <summary>
    /// Toggle raw IQ capture of the decimated modem-input stream to a .cf32
    /// file. Prompts for a path when starting; flushes/closes when stopping.
    /// </summary>
    [RelayCommand]
    private void ToggleCapture()
    {
        if (_core.IsCapturing)
        {
            _core.StopCapture();
            IsCapturing = false;
            Status = "Capture stopped";
            Log(Status);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Capture modem IQ (.cf32)",
            Filter = "Complex float32 IQ (*.cf32)|*.cf32|All files (*.*)|*.*",
            DefaultExt = ".cf32",
            FileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.cf32",
        };
        if (dlg.ShowDialog() != true) return;

        if (_core.StartCapture(dlg.FileName))
        {
            IsCapturing = true;
            Status = $"Capturing IQ -> {dlg.FileName}";
            Log(Status);
        }
        else
        {
            Status = "Capture failed to open file";
            Log(Status);
        }
    }

    /// <summary>
    /// Toggle recording of decoded LoRa payloads to a JSON Lines (.jsonl) file.
    /// Prompts for a path when starting; each successfully demodulated payload
    /// is appended as one JSON object (timestamp, length, CRC status, full hex
    /// bytes). Closes the file when stopping.
    /// </summary>
    [RelayCommand]
    private void ToggleRecordPayloads()
    {
        if (_payloadWriter is not null)
        {
            StopPayloadRecording();
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Record decoded payloads (.jsonl)",
            Filter = "JSON Lines (*.jsonl)|*.jsonl|All files (*.*)|*.*",
            DefaultExt = ".jsonl",
            FileName = $"payloads_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _payloadWriter = new StreamWriter(dlg.FileName, append: true) { AutoFlush = true };
            _payloadCount = 0;
            IsRecordingPayloads = true;
            Status = $"Recording payloads -> {dlg.FileName}";
            Log(Status);
        }
        catch (Exception ex)
        {
            _payloadWriter = null;
            IsRecordingPayloads = false;
            Status = $"Payload record failed: {ex.Message}";
            Log(Status);
        }
    }

    private void StopPayloadRecording()
    {
        if (_payloadWriter is null) return;
        try { _payloadWriter.Flush(); _payloadWriter.Dispose(); } catch { /* ignore */ }
        _payloadWriter = null;
        IsRecordingPayloads = false;
        Status = $"Payload recording stopped ({_payloadCount} payloads)";
        Log(Status);
    }

    // Matches the native payload event line, e.g.
    //   "  payload[OK] len=31 crc=E511/E511 FFFFFFFF594FA54F..."
    //   "  payload[BAD] len=29 crc=CAC0/1FCF FF..."
    //   "  payload len=31 FF..."   (no CRC)
    private static readonly Regex PayloadLineRegex = new(
        @"payload(?:\[(?<status>OK|BAD)\])?\s+len=(?<len>\d+)(?:\s+crc=(?<rx>[0-9A-Fa-f]+)/(?<calc>[0-9A-Fa-f]+))?\s+(?<hex>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    // Pulls the peak-above-noise figure out of a preamble line, e.g.
    //   "preamble: SF9 BW250k cfo=+101.6k peak=28.3dB"
    // We use this as the per-packet SNR estimate for the message/node tables.
    private static readonly Regex PreamblePeakRegex = new(
        @"peak=(?<peak>-?\d+(?:\.\d+)?)dB", RegexOptions.Compiled);

    /// <summary>Peak-above-noise (dB) from the most recent preamble, applied as
    /// the SNR of the next decoded packet. NaN until a preamble is seen.</summary>
    private float _lastPreamblePeakDb = float.NaN;

    /// <summary>If payload recording is active and <paramref name="ev"/> is a
    /// decoded-payload event, append a structured JSON record to the file.</summary>
    private void RecordPayloadIfActive(string ev)
    {
        if (_payloadWriter is null) return;
        if (ev.IndexOf("payload", StringComparison.Ordinal) < 0) return;

        var m = PayloadLineRegex.Match(ev);
        if (!m.Success) return;

        var status = m.Groups["status"].Success ? m.Groups["status"].Value : "NOCRC";
        var crcOk = status == "OK";
        var len = m.Groups["len"].Value;
        var rx = m.Groups["rx"].Success ? m.Groups["rx"].Value : "";
        var calc = m.Groups["calc"].Success ? m.Groups["calc"].Value : "";
        var hex = m.Groups["hex"].Value;

        var ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        var freq = CenterFreqMHz.ToString("F3", CultureInfo.InvariantCulture);
        var json =
            $"{{\"time\":\"{ts}\",\"freq_mhz\":{freq},\"preset\":\"{SelectedPreset}\"," +
            $"\"status\":\"{status}\",\"crc_ok\":{(crcOk ? "true" : "false")}," +
            $"\"len\":{len},\"crc_rx\":\"{rx}\",\"crc_calc\":\"{calc}\",\"hex\":\"{hex}\"}}";

        try
        {
            _payloadWriter.WriteLine(json);
            _payloadCount++;
        }
        catch (Exception ex)
        {
            Log($"payload record write failed: {ex.Message}");
            StopPayloadRecording();
        }
    }

    /// <summary>
    /// If <paramref name="ev"/> is a CRC-valid decoded payload, parse the
    /// Meshtastic header, try each channel key to AES-CTR decrypt it, and
    /// populate the node + message databases. CRC-bad frames are skipped (the
    /// bytes are unreliable).
    /// </summary>
    private void DecodePayloadIfPossible(string ev)
    {
        if (ev.IndexOf("payload", StringComparison.Ordinal) < 0) return;
        var m = PayloadLineRegex.Match(ev);
        if (!m.Success) return;
        // Only trust frames whose CRC verified.
        if (!(m.Groups["status"].Success && m.Groups["status"].Value == "OK")) return;

        var frame = HexToBytes(m.Groups["hex"].Value);
        if (frame.Length < MeshHeader.Size) return;
        if (!MeshHeader.TryParse(frame, out var header)) return;

        // Own packet heard back (Meshtastic `isFromUs`): when a neighbour
        // rebroadcasts a frame we sent, we receive our own transmission. The
        // firmware never re-processes these — it treats hearing your own packet
        // only as an implicit ACK that it was relayed, and never adds it to the
        // node DB or message list again. We already echoed the message locally
        // when the user pressed send, so just note the confirmation and drop it.
        if (_myNodeNum != 0 && header.From == _myNodeNum)
        {
            Log($"  tx confirmed (heard own packet id {header.PacketId:x8})");
            return;
        }

        var rxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var channels = Channels.Select(c => c.Config).ToList();
        var result = MeshDecoder.Decode(frame, channels);

        // PKC fallback: modern firmware seals direct messages to us with
        // X25519 + AES-CCM (channel-hash byte 0x00) instead of a channel PSK,
        // so MeshDecoder.Decode (PSK-only) can't read them. Mirroring firmware
        // perhapsDecode, attempt a PKC decrypt when the frame is a unicast DM
        // addressed to us, the channel hash is 0, and we hold both keys.
        if (result is null && _myNodeNum != 0 &&
            header.To == _myNodeNum && !header.IsBroadcast &&
            header.ChannelHash == 0x00)
        {
            result = TryDecodePkc(frame, header);
        }

        // SNR estimate captured from this frame's preamble (peak above noise).
        float? snrDb = float.IsNaN(_lastPreamblePeakDb) ? null : _lastPreamblePeakDb;
        _lastPreamblePeakDb = float.NaN; // consume it so it can't bleed to the next

        // Always record the sender sighting (RSSI/last-heard), decoded or not.
        try
        {
            _nodeStore.RecordSighting(header.From,
                rssiDbm: float.IsNegativeInfinity(RssiDbfs) ? null : RssiDbfs,
                snrDb: snrDb,
                hopsAway: (byte)(header.HopStart >= header.HopLimit
                                 ? header.HopStart - header.HopLimit : 0));
        }
        catch { /* DB best-effort */ }

        var record = new MessageRecord
        {
            PacketId = header.PacketId,
            FromNode = header.From,
            ToNode = header.To,
            PortNum = (int)(result?.Port ?? PortNum.Unknown),
            Channel = result?.ChannelName ?? string.Empty,
            Decrypted = result is not null,
            RxEpoch = rxEpoch,
            RssiDbfs = float.IsNegativeInfinity(RssiDbfs) ? null : RssiDbfs,
            SnrDb = snrDb,
        };

        if (result is null)
        {
            // Couldn't decrypt with any known channel key. Store the raw frame.
            record.PayloadHex = m.Groups["hex"].Value;
        }
        else
        {
            record.PayloadHex = BytesToHex(result.AppPayload);
            if (result.Port == PortNum.TextMessage)
                record.Text = result.Text ?? string.Empty;
        }

        // Dedup: Meshtastic floods packets, so the same message arrives several
        // times (different relays). MessageStore.Add returns false for a packet
        // we've already stored — skip ALL UI updates for repeats so each unique
        // message shows exactly once, like the Meshtastic app.
        bool isNew;
        try { isNew = _messageStore.Add(record); }
        catch (Exception ex) { Log($"message store failed: {ex.Message}"); isNew = false; }

        if (!isNew)
        {
            // Still refresh the sighting timestamp (done above), but don't echo.
            ReloadNodes();
            return;
        }

        Messages.Insert(0, record);

        bool nodeChanged = false;
        if (result is null)
        {
            Log($"  rx undecoded from {header.FromId} (chan hash {header.ChannelHash:X2})");
            // Help the user find the right key: if a single-byte "default key
            // family" PSK decodes this frame, tell them exactly what to enter.
            var idx = MeshDecoder.DiscoverDefaultKeyIndex(frame);
            if (idx is int ki)
            {
                var b64 = Convert.ToBase64String(new[] { (byte)ki });
                var hint = ki == 1 ? "\"default\"" : $"\"base64:{b64}\"";
                Log($"  hint: this frame decodes with PSK index {ki} — set a " +
                    $"channel's key to {hint} to read it");
            }
        }
        else
        {
            var senderName = NodeDisplayName(header.From);
            switch (result.Port)
            {
                case PortNum.TextMessage:
                    // Direct message addressed to us → route to a conversation tab.
                    if (_myNodeNum != 0 && !header.IsBroadcast && header.To == _myNodeNum)
                    {
                        // Open the peer's DM tab if it isn't already, but don't
                        // steal focus from whatever the user is currently viewing.
                        // OpenConversation loads persisted history (including the
                        // record we just stored above), so a freshly-opened tab is
                        // already complete; only append when the tab pre-existed.
                        bool existed = Tabs.OfType<ConversationViewModel>()
                                           .Any(c => c.NodeNum == header.From);
                        var convo = OpenConversation(header.From, senderName, focus: false);
                        if (existed)
                            convo.Add(new ChannelMessage
                            {
                                FromId = senderName,
                                Text = record.Text,
                                RssiDbm = record.RssiDbfs,
                                SnrDb = record.SnrDb,
                                PacketId = header.PacketId,
                            });
                        Log($"  DM from {senderName}: {record.Text}");
                        // Acknowledge if the sender asked for one (firmware does
                        // this for any unicast packet addressed to it).
                        if (header.WantAck) SendAck(header, result);
                    }
                    else
                    {
                        // Broadcast text → populate the owning channel tab like a chat room.
                        var chanVm = ResolveChannelTab(result.ChannelName);
                        chanVm?.Messages.Add(new ChannelMessage
                        {
                            FromId = senderName,
                            Text = record.Text,
                            RssiDbm = record.RssiDbfs,
                            SnrDb = record.SnrDb,
                        });
                        if (chanVm is not null && chanVm.Messages.Count > 1000)
                            chanVm.Messages.RemoveAt(0);
                        Log($"  [{result.ChannelName}] {senderName}: {record.Text}");
                    }
                    break;
                case PortNum.Routing:
                    // ACK / NAK for one of our sent messages.
                    HandleRouting(header, result);
                    break;
                case PortNum.NodeInfo when result.User is not null:
                    nodeChanged = true;
                    {
                        string newKeyHex = result.User.PublicKey.Length == 32
                            ? Convert.ToHexString(result.User.PublicKey)
                            : string.Empty;
                        var existingNode = _nodeStore.Get(header.From);
                        // A mismatch is a NEW non-empty key that differs from a
                        // key we already trust. We keep the old key (don't
                        // silently accept a substitution) and flag it red until
                        // the user explicitly requests new keys.
                        bool keyMismatch = newKeyHex.Length > 0
                            && !string.IsNullOrEmpty(existingNode?.PublicKey)
                            && !string.Equals(existingNode!.PublicKey, newKeyHex,
                                               StringComparison.OrdinalIgnoreCase);

                        _nodeStore.Upsert(new NodeRecord
                        {
                            NodeNum = header.From,
                            UserId = string.IsNullOrEmpty(result.User.Id) ? header.FromId : result.User.Id,
                            LongName = result.User.LongName,
                            ShortName = result.User.ShortName,
                            HwModel = result.User.HwModel.ToString(),
                            // Empty on mismatch keeps the previously trusted key.
                            PublicKey = keyMismatch ? string.Empty : newKeyHex,
                            // Only touch the flag when this NodeInfo carried a key.
                            KeyMismatch = newKeyHex.Length > 0 ? keyMismatch : (bool?)null,
                            LastHeardEpoch = rxEpoch,
                        });

                        if (keyMismatch)
                            Log($"  nodeinfo {header.FromId}: KEY MISMATCH — public key changed; "
                                + "keeping the old key. Right-click the node → Request new keys to accept it.");
                        else
                            Log($"  nodeinfo {header.FromId}: {result.User.LongName} ({result.User.ShortName})"
                                + (newKeyHex.Length > 0 ? " [PKC key]" : string.Empty));
                    }
                    // If they directed a NodeInfo request at us (want_response),
                    // reply with ours so they learn our public key. Firmware does
                    // the same — this is how PKC key exchange bootstraps.
                    if (result.WantResponse && _myNodeNum != 0 &&
                        header.To == _myNodeNum && !header.IsBroadcast)
                    {
                        Log($"  NodeInfo requested by {senderName} — replying");
                        RequestNodeInfoReply(header.From);
                    }
                    break;
                case PortNum.Position when result.Position is not null:
                    nodeChanged = true;
                    _nodeStore.Upsert(new NodeRecord
                    {
                        NodeNum = header.From,
                        Latitude = result.Position.Latitude,
                        Longitude = result.Position.Longitude,
                        AltitudeM = result.Position.AltitudeM,
                        LastHeardEpoch = rxEpoch,
                    });
                    Log($"  position {header.FromId}: {result.Position.Latitude:F5}, {result.Position.Longitude:F5}");
                    break;
                case PortNum.Telemetry when result.Telemetry is not null:
                    nodeChanged = true;
                    var t = result.Telemetry;
                    _nodeStore.Upsert(new NodeRecord
                    {
                        NodeNum = header.From,
                        LastHeardEpoch = rxEpoch,
                        BatteryPct = t.BatteryLevel,
                        VoltageV = t.Voltage,
                        ChannelUtilPct = t.ChannelUtilization,
                        AirUtilTxPct = t.AirUtilTx,
                        UptimeSeconds = t.UptimeSeconds,
                        TemperatureC = t.TemperatureC,
                        RelativeHumidityPct = t.RelativeHumidityPct,
                        BarometricPressureHpa = t.BarometricPressureHpa,
                        GasResistanceMohm = t.GasResistanceMohm,
                        Iaq = t.Iaq,
                    });
                    if (t.HasEnvironmentMetrics)
                        Log($"  telemetry {header.FromId}: {t.TemperatureC:F1}\u00B0C {t.RelativeHumidityPct:F0}% {t.BarometricPressureHpa:F0}hPa");
                    else
                        Log($"  telemetry {header.FromId}: batt {t.BatteryLevel}% {t.Voltage:F2}V");
                    break;
                default:
                    Log($"  [{result.ChannelName}] {header.FromId} {result.Port} ({result.AppPayload.Length} B)");
                    break;
            }
        }

        ReloadNodes();
        if (nodeChanged) { /* names already refreshed by ReloadNodes */ }
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

    private static string BytesToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var x in bytes) sb.Append(x.ToString("X2"));
        return sb.ToString();
    }

    /// <summary>Pulls fresh signal stats from the native core.</summary>
    public void RefreshStats()
    {
        if (!_core.IsRunning) return;
        var s = _core.GetSignalStats();
        RssiDbfs = s.RssiDbfs;
        PeakDbfs = s.PeakDbfs;
        TotalSamples = s.TotalSamples;

        if (AgcEnable) StepAgc();

        // Flag any outgoing DMs that never got an ACK within the timeout.
        SweepPendingAcks();

        // Drain any queued demodulator events into the log. Cap per tick so a
        // burst can't lock up the UI thread.
        for (int i = 0; i < 16; i++)
        {
            var ev = _core.PullEvent();
            if (ev is null) break;
            Log(ev);
            // A "preamble: ..." line marks the start of a received frame; grab
            // its peak-above-noise as the SNR for the payload that follows.
            if (ev.StartsWith("preamble", StringComparison.Ordinal))
            {
                var pm = PreamblePeakRegex.Match(ev);
                if (pm.Success &&
                    float.TryParse(pm.Groups["peak"].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var pk))
                    _lastPreamblePeakDb = pk;
            }
            RecordPayloadIfActive(ev);
            DecodePayloadIfPossible(ev);
            // The View uses the preamble to freeze a spectrogram of the packet.
            if (ev.StartsWith("preamble", StringComparison.Ordinal))
                PacketDetected?.Invoke();
            // A CRC-valid payload means this was a real packet (not a false
            // positive or a corrupt frame); only then is the snapshot worth
            // keeping. The View uses this to commit the frozen spectrogram.
            else if (IsCrcOkPayload(ev))
                PacketDecoded?.Invoke();
        }
    }

    // True when the event line is a decoded payload whose CRC verified.
    private static bool IsCrcOkPayload(string ev)
    {
        if (ev.IndexOf("payload", StringComparison.Ordinal) < 0) return false;
        var m = PayloadLineRegex.Match(ev);
        return m.Success && m.Groups["status"].Success &&
               m.Groups["status"].Value == "OK";
    }

    /// <summary>Raised on the UI thread when the demodulator detects a packet
    /// (preamble). The View captures a spectrogram snapshot around it.</summary>
    public event Action? PacketDetected;

    /// <summary>Raised on the UI thread when a detected packet decodes with a
    /// valid CRC (i.e. a genuine frame, not a false positive or corrupt one).
    /// The View commits the frozen last-packet spectrogram only on this.</summary>
    public event Action? PacketDecoded;

    // AGC: nudge LNA/VGA toward a target peak power. Runs at the UI tick
    // (20 Hz) but only acts ~once per second, with small +/- 2 dB steps so
    // the receiver never oscillates badly. We move VGA first (cheap), then
    // LNA when VGA hits a rail.
    private int _agcDecimator;
    private void StepAgc()
    {
        if ((++_agcDecimator % 20) != 0) return; // ~1 Hz
        var peak = PeakDbfs;
        if (float.IsNaN(peak) || float.IsInfinity(peak)) return;
        var target = (float)AgcTargetDbfs;
        var err = peak - target;
        const float deadband = 2.0f;
        if (Math.Abs(err) < deadband) return;

        if (err > 0) // too hot -> lower gain
        {
            if (VgaGainDb >= 2) VgaGainDb -= 2;
            else if (LnaGainDb >= 8) LnaGainDb -= 8;
            else if (AmpEnable) AmpEnable = false;
        }
        else        // too cold -> raise gain
        {
            if (VgaGainDb <= 60) VgaGainDb += 2;
            else if (LnaGainDb <= 32) LnaGainDb += 8;
            // (Don't auto-enable AMP — it adds noise; user opts in.)
        }
    }

    public void Dispose()
    {
        StopPayloadRecording();
        _core.Dispose();
        _nodeStore.Dispose();
        _channelStore.Dispose();
        _messageStore.Dispose();
    }
}
