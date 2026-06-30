// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.App.Audio;
using MeshRF.App.Location;
using MeshRF.App.Units;
using MeshRF.App.Views;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MeshtasticCore _core = new();
    private readonly NodeStore _nodeStore = new();
    private readonly WaypointStore _waypointStore = new();
    private readonly ChannelStore _channelStore = new();
    private readonly MessageStore _messageStore = new();
    private readonly UsbSerialGpsService _gpsService = new();
    private readonly AppSettings _settings;
    private DateTime? _lastRxPlayUtc;
    private bool _settingsLoaded;
    private double? _manualHomeLatitude;
    private double? _manualHomeLongitude;
    private int?    _manualHomeAltitude;

    // Serializes all transmit calls so concurrent sends (user + auto-reply)
    // don't race on the shared native Core handle.
    private readonly SemaphoreSlim _txSemaphore = new(1, 1);
    private int _sharedHackRfTxStatusDepth;

    // Set when a received packet updates node state; consumed by the 20 Hz
    // timer tick so ReloadNodes() runs at most once per tick rather than once
    // per received packet (avoids stutter from Nodes.Clear + full rebind).
    private bool _nodesDirty;
    private bool _waypointsDirty;
    private bool _suspendNodeReload;
    private readonly HashSet<uint> _dirtyNodeNums = new();
    private readonly Dictionary<uint, NodeRecord> _nodesByNum = new();
    private readonly Dictionary<uint, int> _nodeLocationHistoryCounts = new();
    private readonly Dictionary<uint, int> _nodeMapStateSignatures = new();
    private readonly Dictionary<uint, int> _nodeTooltipSignatures = new();
    private readonly Dictionary<uint, string> _nodeTooltipCache = new();
    private readonly Dictionary<uint, byte[]> _pkcSenderPublicKeyBytes = new();
    private readonly Queue<ulong> _recentUndecodedPacketOrder = new();
    private readonly HashSet<ulong> _recentUndecodedPacketKeys = new();
    
    // Filter optimization: pre-computed set of nodes that pass current filter criteria.
    // Computed on background thread and marshaled to UI thread for display.
    private readonly HashSet<uint> _nodeFilterCache = new();
    private FilterCriteria _currentFilterCriteria = FilterCriteria.CreateEmpty();
    private readonly object _filterCriteriaSyncLock = new();
    private readonly DispatcherTimer _filterChangeDebounceTimer;
    private CancellationTokenSource? _filterComputeCts;
    
    private DateTime _lastNodesViewRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan NodesViewRefreshInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxDirtyNodeUpdatesPerTick = 64;
    private const int MaxRxEventsPerTick = 8;
    private const double MaxRxDrainMsPerTick = 4.0;
    private const int RecentUndecodedPacketLimit = 512;
    private bool _nodesViewRefreshPending;
    private readonly DispatcherTimer _nodesViewRefreshTimer;

    private const string ManualLocationSourceValue = "Manual";
    private const string UsbSerialLocationSourceValue = "UsbSerial";
    private const double HomeMapUpdateThresholdKm = 0.02; // 20 m
    private const string UiDateTimeFormat = "M/d/yyyy h:mm:ss tt";

    // Plays the RTTTL ringtone when a text message arrives.
    private readonly RtttlPlayer _ringtone = new();
    // Payload recording: open StreamWriter when active and emit a single valid
    // JSON array document (one object per payload). Null when not recording.
    private StreamWriter? _payloadWriter;
    private int _payloadCount;
    private bool _payloadJsonHasEntries;

    [ObservableProperty]
    private LoraPreset _selectedPreset = LoraPreset.LongFast;

    /// <summary>Spreading factor (5–12). Auto-filled from preset; editable for custom use.</summary>
    [ObservableProperty]
    private byte _overrideSf = 11;

    /// <summary>Bandwidth in kHz. Auto-filled from preset; editable for custom use.</summary>
    [ObservableProperty]
    private double _overrideBwKhz = 250.0;

    /// <summary>Coding rate denominator (5–8 → 4/N). Auto-filled from preset; editable.</summary>
    [ObservableProperty]
    private byte _overrideCr = 5;

    /// <summary>Returns true when the current SF/BW/CR values differ from the
    /// preset defaults — used by the UI to hint that custom params are active.</summary>
    public bool IsCustomLoraParams
    {
        get
        {
            var p = MeshRF.LoraParamsHelper.FromPreset(SelectedPreset);
            return OverrideSf != p.Sf || Math.Abs(OverrideBwKhz - p.BwKhz) > 0.01 || OverrideCr != p.Cr;
        }
    }

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

    /// <summary>RTL-SDR tuner automatic gain control.</summary>
    [ObservableProperty]
    private bool _rtlAgcEnable;

    /// <summary>RTL-SDR 5 V bias-T on the antenna port. Off by default.</summary>
    [ObservableProperty]
    private bool _biasTee;

    /// <summary>Enable the IIR DC-blocker that suppresses the LO leakage spike at
    /// the tuned centre frequency. Default on; turn off for diagnostics.</summary>
    [ObservableProperty]
    private bool _dcBlockEnable = true;

    [ObservableProperty]
    private string _theme = "System";

    [ObservableProperty]
    private string _unitSystemName = nameof(UnitSystem.Metric);

    public IReadOnlyList<string> Themes { get; } = new[] { "System", "Light", "Dark" };
    public IReadOnlyList<string> UnitSystems { get; } = new[] { nameof(UnitSystem.Metric), nameof(UnitSystem.Imperial) };
    public UnitSystem CurrentUnitSystem => DisplayUnits.Parse(UnitSystemName);
    public bool UseImperial => DisplayUnits.IsImperial(CurrentUnitSystem);
    public bool UseFahrenheit => UseImperial;
    public bool UseMiles => UseImperial;

    /// <summary>Incoming-message ringtone duration.</summary>
    [ObservableProperty]
    private string _ringtoneMode = "Play once";

    public IReadOnlyList<string> RingtoneModes { get; } = new[]
    {
        "Off", "Play once", "5 seconds", "10 seconds", "30 seconds",
    };

    /// <summary>Ringtone volume, 0..100.</summary>
    [ObservableProperty]
    private double _ringtoneVolume = 70;

    /// <summary>RTTTL ringtone string (Meshtastic format).</summary>
    [ObservableProperty]
    private string _ringtoneRtttl = RtttlPlayer.MeshtasticDefault;

    partial void OnRingtoneModeChanged(string value) => SaveSettings();
    partial void OnRingtoneVolumeChanged(double value) => SaveSettings();
    partial void OnRingtoneRtttlChanged(string value) => SaveSettings();

    /// <summary>Preview the current ringtone settings.</summary>
    [RelayCommand]
    private void TestRingtone() =>
        _ringtone.Play(RingtoneRtttl, ParseRingtoneMode(RingtoneMode), RingtoneVolume / 100.0);

    /// <summary>Map the user-facing mode label to the player enum.</summary>
    private static MeshRF.App.Audio.RingtoneMode ParseRingtoneMode(string mode) => mode switch
    {
        "Off" => MeshRF.App.Audio.RingtoneMode.Off,
        "5 seconds" => MeshRF.App.Audio.RingtoneMode.Seconds5,
        "10 seconds" => MeshRF.App.Audio.RingtoneMode.Seconds10,
        "30 seconds" => MeshRF.App.Audio.RingtoneMode.Seconds30,
        _ => MeshRF.App.Audio.RingtoneMode.PlayOnce,
    };

    /// <summary>Play the configured ringtone for an incoming text message.</summary>
    private void PlayRingtone() =>
        _ringtone.Play(RingtoneRtttl, ParseRingtoneMode(RingtoneMode), RingtoneVolume / 100.0);

    [ObservableProperty]
    private string _waterfallColormap = "Turbo";

    public IReadOnlyList<string> WaterfallColormaps { get; } = new[] { "Turbo", "Inferno", "Meshtastic" };

    [ObservableProperty]
    private bool _waterfallAutoLevels = true;

    [ObservableProperty]
    private double _waterfallFloorDb = -100.0;

    [ObservableProperty]
    private double _waterfallCeilDb = 0.0;

    /// <summary>Waterfall scroll speed in rows per second. One row spans
    /// 1/this seconds of received signal, so higher = faster scroll and finer
    /// time resolution (independent of frequency/FFT resolution). Clamped to a
    /// sane range; persisted across sessions.</summary>
    [ObservableProperty]
    private double _waterfallRowsPerSecond = 60.0;

    /// <summary>Displayed spectrum/waterfall span in Hz (= device sample rate).
    /// Updated from the running pipeline; 0 when stopped. Drives the frequency
    /// axis labels.</summary>
    [ObservableProperty]
    private double _spectrumSpanHz;

    /// <summary>Center frequency of the displayed spectrum in Hz. When running
    /// this is the actual LO frequency (channel + offset-tune shift), so the
    /// frequency axis labels are accurate. Falls back to the channel frequency
    /// when stopped.</summary>
    [ObservableProperty]
    private double _spectrumCenterHz;

    [ObservableProperty]
    private string _status = "Idle";

    public string DeviceName => _core.DeviceName;
    public string TxDeviceName => _core.TxDeviceName;
    public bool HasRealRadio => _core.HasRealRadio;
    public string DeviceStatus => _core.DeviceStatus;
    public string DeviceBadge => _core.HasRealRadio
        ? $"RX: {_core.DeviceName}  TX: {_core.TxDeviceName}"
        : $"RX: {_core.DeviceName} (unavailable)  TX: {_core.TxDeviceName}";

    /// <summary>An entry in the device-backend selector.</summary>
    public sealed record DeviceOption(RadioDeviceKind Kind, string Label);

    /// <summary>An entry in the RX sample-rate selector.</summary>
    public sealed record SampleRateOption(uint Hz, string Label);

    /// <summary>An entry in the home-location source selector.</summary>
    public sealed record LocationSourceOption(string Value, string Label);

    /// <summary>Selectable RX radio backends (HackRF / RTL-SDR / None).
    /// Populated at construction with an availability annotation.</summary>
    public IReadOnlyList<DeviceOption> DeviceOptions { get; private set; } =
        Array.Empty<DeviceOption>();

    public IReadOnlyList<DeviceOption> TxDeviceOptions { get; private set; } =
        Array.Empty<DeviceOption>();

    public IReadOnlyList<SampleRateOption> SampleRateOptions { get; private set; } =
        Array.Empty<SampleRateOption>();

    [ObservableProperty]
    private DeviceOption? _selectedDevice;

    [ObservableProperty]
    private DeviceOption? _selectedTxDevice;

    [ObservableProperty]
    private SampleRateOption? _selectedRxSampleRate;

    private bool _suppressDeviceUpdate;
    private bool _suppressSampleRateUpdate;

    /// <summary>True when the selected RX backend is an RTL-SDR (drives which
    /// receiver controls the toolbar shows).</summary>
    public bool IsRtlSdr => SelectedDevice?.Kind == RadioDeviceKind.RtlSdr;

    /// <summary>True for everything that isn't an RTL-SDR; those use the
    /// HackRF-style LNA/VGA/AMP gain model.</summary>
    public bool IsHackRf => !IsRtlSdr;

    /// <summary>True when the selected TX backend is a HackRF.</summary>
    public bool IsTxHackRf => SelectedTxDevice?.Kind == RadioDeviceKind.HackRf;

    /// <summary>The device selectors are only editable while RX is stopped.</summary>
    public bool CanSelectDevice => !IsRunning;

    public bool CanSelectRxSampleRate =>
        !IsRunning && SelectedDevice?.Kind != RadioDeviceKind.Null && SampleRateOptions.Count > 0;

    [ObservableProperty]
    private float _rssiDbfs = float.NegativeInfinity;

    [ObservableProperty]
    private float _peakDbfs = float.NegativeInfinity;

    [ObservableProperty]
    private ulong _totalSamples;

    [ObservableProperty]
    private float _liveChannelUtilizationPct;

    [ObservableProperty]
    private float _liveAirUtilTxPct;

    [ObservableProperty]
    private double _uiFrameRateHz;

    [ObservableProperty]
    private string _uiPerfSummary = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private bool _isRecordingPayloads;

    private const uint HackRfStableMaxRateHz = 16_000_000;
    private const uint HackRfMaxSelectableRateHz = 20_000_000;
    private const uint RtlSdrDecodeSafeMaxRateHz = 2_560_000;

    private static readonly uint[] HackRfSampleRatesHz =
    [
        2_000_000,
        2_400_000,
        4_000_000,
        8_000_000,
        10_000_000,
        12_500_000,
        16_000_000,
        20_000_000,
    ];

    private static readonly uint[] RtlSdrSampleRatesHz =
    [
        960_000,
        1_024_000,
        1_200_000,
        1_440_000,
        1_600_000,
        1_800_000,
        1_920_000,
        2_048_000,
        2_400_000,
        2_560_000,
        2_880_000,
        3_200_000,
    ];

    public IReadOnlyList<LoraPreset> Presets { get; } = Enum.GetValues<LoraPreset>();
    public IReadOnlyList<Region> Regions { get; } = Enum.GetValues<Region>();

    [ObservableProperty]
    private ObservableCollection<int> _slots = new();

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<NodeRecord> Nodes { get; } = new();
    public ObservableCollection<WaypointRecord> Waypoints { get; } = new();

    // -- Node list filters ---------------------------------------------------

    /// <summary>Filtered, sorted view of <see cref="Nodes"/> bound to the DataGrid.
    /// Uses a single <see cref="ICollectionView.Refresh"/> call so WPF virtualization
    /// keeps scrolling smooth regardless of list size.</summary>
    public ICollectionView NodesView { get; private set; } = null!;

    [ObservableProperty] private string _nodeSearchText        = string.Empty;
    [ObservableProperty] private string _nodeHopsFilter        = "Any";
    [ObservableProperty] private string _nodeKeyFilter         = "Any";
    [ObservableProperty] private string _nodeLocationFilter    = "Any";
    [ObservableProperty] private bool _hideInvalidNodeLocations;
    [ObservableProperty] private string _nodeIgnoredFilter     = "Show all";
    [ObservableProperty] private string _nodeMqttFilter        = "Any";
    [ObservableProperty] private string _nodeTemperatureFilter = "Any";
    [ObservableProperty] private string _nodeHumidityFilter    = "Any";
    [ObservableProperty] private string _nodePressureFilter    = "Any";
    [ObservableProperty] private string _mapNodeLabelMode      = "Node Number";
    [ObservableProperty] private string _nodeDistanceKmText    = string.Empty;
    [ObservableProperty] private string _nodeMaxAgeMinutesText = string.Empty;

    public IReadOnlyList<string> NodeHopsFilterOptions     { get; } = ["Any", "Direct", "≤1 hop", "≤2 hops", "≤3 hops", "≤4 hops"];
    public IReadOnlyList<string> NodeKeyFilterOptions      { get; } = ["Any", "Good key", "Mismatch", "No key"];
    public IReadOnlyList<string> NodeLocationFilterOptions { get; } = ["Any", "Has position", "Has position history (>1)", "No position"];
    public IReadOnlyList<string> NodeIgnoredFilterOptions  { get; } = ["Show all", "Hide ignored", "Only ignored"];
    public IReadOnlyList<string> NodeMqttFilterOptions     { get; } = ["Any", "Hide via MQTT", "Only via MQTT"];
    public IReadOnlyList<string> TelemetryHasFilterOptions { get; } = ["Any", "Has value", "No value"];
    public IReadOnlyList<string> MapNodeLabelModeOptions   { get; } =
        ["Node Number", "Long Name", "Short Name", "Temperature", "Humidity", "Pressure"];

    /// <summary>True when a home location is set (enables the distance filter).</summary>
    public bool HasHomeLocation => HomeLatitude.HasValue && HomeLongitude.HasValue;
    public string HomeLocationLabel => $"Location (lat, lon, alt {DisplayUnits.AltitudeUnitShort(CurrentUnitSystem)})";
    public string HomeAltitudeToolTip => UseImperial
        ? "Altitude in feet above sea level (optional)"
        : "Altitude in metres above sea level (optional)";

    public string DistanceUnitShort => DisplayUnits.DistanceUnitShort(CurrentUnitSystem);
    public string DistanceUnitLong => DisplayUnits.DistanceUnitLong(CurrentUnitSystem);
    public string MaxDistanceFilterToolTip =>
        UseImperial
            ? "Max distance from location in miles (blank = any; requires location to be set)"
            : "Max distance from location in km (blank = any; requires location to be set)";

    partial void OnNodeSearchTextChanged(string value)          => RefreshNodesFilter();
    partial void OnNodeHopsFilterChanged(string value)          => RefreshNodesFilter();
    partial void OnNodeKeyFilterChanged(string value)           => RefreshNodesFilter();
    partial void OnNodeLocationFilterChanged(string value)      => RefreshNodesFilter();
    partial void OnHideInvalidNodeLocationsChanged(bool value)  => RefreshNodesFilter();
    partial void OnNodeIgnoredFilterChanged(string value)
    {
        RefreshNodesFilter();
        if (_settingsLoaded)
            LoadChatHistory();
    }
    partial void OnNodeMqttFilterChanged(string value)          => RefreshNodesFilter();
    partial void OnNodeTemperatureFilterChanged(string value)   => RefreshNodesFilter();
    partial void OnNodeHumidityFilterChanged(string value)      => RefreshNodesFilter();
    partial void OnNodePressureFilterChanged(string value)      => RefreshNodesFilter();
    partial void OnMapNodeLabelModeChanged(string value)        => RefreshNodesFilter();
    partial void OnNodeDistanceKmTextChanged(string value)      => RefreshNodesFilter();
    partial void OnNodeMaxAgeMinutesTextChanged(string value)   => RefreshNodesFilter();

    [RelayCommand]
    private void ClearNodeFilters()
    {
        NodeSearchText        = string.Empty;
        NodeHopsFilter        = "Any";
        NodeKeyFilter         = "Any";
        NodeLocationFilter    = "Any";
        HideInvalidNodeLocations = false;
        NodeIgnoredFilter     = "Show all";
        NodeMqttFilter        = "Any";
        NodeTemperatureFilter = "Any";
        NodeHumidityFilter    = "Any";
        NodePressureFilter    = "Any";
        NodeDistanceKmText    = string.Empty;
        NodeMaxAgeMinutesText = string.Empty;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Most-recent decoded mesh messages, newest first.</summary>
    public ObservableCollection<MessageRecord> Messages { get; } = new();

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    /// <summary>Tabs shown in the channel/conversation TabControl: channels
    /// followed by any open direct-message conversations.</summary>
    public ObservableCollection<object> Tabs { get; } = new();

    [ObservableProperty]
    private object? _selectedTab;

    private int _lastSelectedChannelIndex = -1;

    /// <summary>Most recently selected channel index, used as fallback when closing DM tabs.</summary>
    public int LastSelectedChannelIndex => _lastSelectedChannelIndex;

    /// <summary>The selected tab when it is a channel (null for DM tabs).</summary>
    public ChannelViewModel? SelectedChannel => SelectedTab as ChannelViewModel;

    partial void OnSelectedTabChanged(object? value)
    {
        if (value is ITabItem tab)
            tab.TabNeedsAttention = false;

        if (value is ChannelViewModel channel)
        {
            _lastSelectedChannelIndex = channel.Config.Index;
            OnPropertyChanged(nameof(LastSelectedChannelIndex));
        }

        OnPropertyChanged(nameof(SelectedChannel));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
        SendDeviceMetricsCommand.NotifyCanExecuteChanged();
    }

    private void MarkTabNeedsAttention(ITabItem? tab)
    {
        if (tab is null) return;
        if (ReferenceEquals(SelectedTab, tab)) return;
        tab.TabNeedsAttention = true;
    }

    public bool CanReorderTabPair(object? dragged, object? target)
    {
        if (dragged is null || target is null || ReferenceEquals(dragged, target))
            return false;

        if (dragged is ChannelViewModel dragChannel && target is ChannelViewModel targetChannel)
            return dragChannel.Config.Role != ChannelRole.Primary &&
                   targetChannel.Config.Role != ChannelRole.Primary;

        return dragged is ConversationViewModel && target is ConversationViewModel;
    }

    /// <summary>True when hover-time reordering is safe and cheap (DM tabs only).</summary>
    public bool CanLiveReorderTabPair(object? dragged, object? target)
    {
        return dragged is ConversationViewModel && target is ConversationViewModel &&
               CanReorderTabPair(dragged, target);
    }

    public bool ReorderTabPair(object? dragged, object? target)
    {
        if (!CanReorderTabPair(dragged, target))
            return false;

        if (dragged is ChannelViewModel dragChannel && target is ChannelViewModel targetChannel)
            return ReorderChannelsByDrag(dragChannel, targetChannel);

        if (dragged is ConversationViewModel dragConvo && target is ConversationViewModel targetConvo)
            return ReorderConversationsByDrag(dragConvo, targetConvo);

        return false;
    }

    private bool ReorderConversationsByDrag(ConversationViewModel dragged, ConversationViewModel target)
    {
        int dragIndex = Tabs.IndexOf(dragged);
        int targetIndex = Tabs.IndexOf(target);
        if (dragIndex < 0 || targetIndex < 0 || dragIndex == targetIndex)
            return false;

        // Conversations are always grouped after channels.
        if (dragIndex < Channels.Count || targetIndex < Channels.Count)
            return false;

        Tabs.Move(dragIndex, targetIndex);
        SelectedTab = dragged;
        SaveSettings();
        return true;
    }

    private bool ReorderChannelsByDrag(ChannelViewModel dragged, ChannelViewModel target)
    {
        if (dragged.Config.Role == ChannelRole.Primary || target.Config.Role == ChannelRole.Primary)
            return false;

        var allConfigs = _channelStore.All().ToList();
        var secondaries = allConfigs
            .Where(c => c.Role != ChannelRole.Primary)
            .OrderBy(c => c.Index)
            .ToList();
        if (secondaries.Count < 2)
            return false;

        int dragPos = secondaries.FindIndex(c => c.Index == dragged.Config.Index);
        int targetPos = secondaries.FindIndex(c => c.Index == target.Config.Index);
        if (dragPos < 0 || targetPos < 0 || dragPos == targetPos)
            return false;

        var availableIndices = secondaries.Select(c => c.Index).OrderBy(i => i).ToList();
        var draggedConfig = secondaries[dragPos];
        secondaries.RemoveAt(dragPos);
        secondaries.Insert(targetPos, draggedConfig);

        foreach (var idx in availableIndices)
            _channelStore.Delete(idx);

        for (int i = 0; i < secondaries.Count; i++)
        {
            secondaries[i].Index = availableIndices[i];
            _channelStore.Upsert(secondaries[i]);
        }

        ReloadChannels();
        LoadChatHistory();
        SelectedTab = Channels.FirstOrDefault(c => c.Config.Index == draggedConfig.Index)
                      ?? (object?)Channels.FirstOrDefault();
        return true;
    }

    // -- Local node identity -------------------------------------------------

    [ObservableProperty] private string _myNodeIdText = string.Empty;
    [ObservableProperty] private string _myLongName = string.Empty;

    /// <summary>MAC address derived from the node number: <c>02:00:xx:xx:xx:xx</c>
    /// where the last four bytes are the node number. Matches the Meshtastic
    /// convention that the 32-bit node number is the low four bytes of the MAC.</summary>
    public string MyMacAddress =>
        _myNodeNum == 0
            ? string.Empty
            : $"02:00:{(_myNodeNum >> 24) & 0xFF:x2}:{(_myNodeNum >> 16) & 0xFF:x2}:{(_myNodeNum >> 8) & 0xFF:x2}:{_myNodeNum & 0xFF:x2}";
    [ObservableProperty] private string _myShortName = string.Empty;
    [ObservableProperty] private string _myRole = "Client";

    /// <summary>Our resolved 32-bit node number (0 = unset).</summary>
    private uint _myNodeNum;

    public IReadOnlyList<string> NodeRoleOptions { get; } = new[]
    {
        "Client", "ClientMute", "ClientHidden", "Router", "RouterClient",
        "Repeater", "Tracker", "Sensor", "TAK", "TakTracker", "LostAndFound",
        "RouterLate", "ClientBase",
    };

    // -- Hardware model / rebroadcast / TX keys / home location -------------

    [ObservableProperty] private string _myHwModel = "UNSET";
    [ObservableProperty] private string _rebroadcastMode = "ALL";
    [ObservableProperty] private string _myPublicKey = string.Empty;
    [ObservableProperty] private string _myPrivateKey = string.Empty;
    private byte[] _myPrivateKeyBytes = Array.Empty<byte>();

    /// <summary>Default hop limit for transmitted packets (1..7). Mirrors the
    /// firmware LoRa config; broadcasts and DMs are sent with this many hops.</summary>
    [ObservableProperty] private int _hopLimit = 3;

    /// <summary>When set, transmitted packets flag <c>ok_to_mqtt</c> so gateways
    /// may uplink them to the public MQTT broker.</summary>
    [ObservableProperty] private bool _okToMqtt;
    [ObservableProperty] private bool _routingRelayEnabled;

    [ObservableProperty] private bool _autoReportNodeInfoEnabled;
    [ObservableProperty] private int _autoReportNodeInfoSeconds = 300;
    [ObservableProperty] private bool _autoReportPositionEnabled;
    [ObservableProperty] private int _autoReportPositionSeconds = 300;
    [ObservableProperty] private bool _autoReportDeviceMetricsEnabled;
    [ObservableProperty] private int _autoReportDeviceMetricsSeconds = 300;
    [ObservableProperty] private string _autoReportLastSentSummary = "Auto last: NI never | POS never | MET never";

    private DateTime _lastAutoNodeInfoUtc = DateTime.MinValue;
    private DateTime _lastAutoPositionUtc = DateTime.MinValue;
    private DateTime _lastAutoDeviceMetricsUtc = DateTime.MinValue;
    private DateTime _nextAutoNodeInfoUtc = DateTime.MinValue;
    private DateTime _nextAutoPositionUtc = DateTime.MinValue;
    private DateTime _nextAutoDeviceMetricsUtc = DateTime.MinValue;
    private int _autoReportTickInFlight;
    private static readonly TimeSpan RxBusyDefaultHold = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RxBusyMaxWait = TimeSpan.FromMilliseconds(450);
    private const int RxBusyPollMs = 20;
    private readonly object _rxBusyLock = new();
    private DateTime _rxBusyUntilUtc = DateTime.MinValue;
    private readonly object _relayScheduleLock = new();
    private readonly Dictionary<ulong, CancellationTokenSource> _pendingRelayCancels = new();
    private readonly Dispatcher _uiDispatcher;
    private readonly Channel<PkcDecodeWorkItem> _pkcDecodeQueue;
    private readonly CancellationTokenSource _pkcDecodeCts = new();
    private const int MaxQueuedPkcDecodes = 256;

    // Dedicated async DB writer for receive-path node/waypoint writes.
    // Uses its own SQLite connections so UI-thread store instances never
    // execute cross-thread, while expensive writes are removed from the 10 Hz
    // stats tick critical path.
    private readonly Channel<Action<NodeStore, WaypointStore>> _dbWriteQueue;
    private readonly CancellationTokenSource _dbWriteCts = new();
    private readonly NodeStore _dbWriteNodeStore;
    private readonly WaypointStore _dbWriteWaypointStore;
    private readonly Task _dbWriteWorkerTask;
    private const int MaxQueuedDbWrites = 1024;

    private sealed record PkcDecodeWorkItem(
        byte[] Frame,
        MeshHeader Header,
        long RxEpoch,
        float? SnrDb,
        float? PacketRssiDbm,
        byte HopsAway,
        byte[] MyPrivateKey,
        byte[] SenderPublicKey);

    private void UpdateAutoReportLastSentSummary()
    {
        static string Stamp(DateTime utc) =>
            utc == DateTime.MinValue
                ? "never"
                : utc.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

        AutoReportLastSentSummary =
            $"Auto last: NI {Stamp(_lastAutoNodeInfoUtc)} | POS {Stamp(_lastAutoPositionUtc)} | MET {Stamp(_lastAutoDeviceMetricsUtc)}";
    }

    [ObservableProperty] private string _homeLatitudeText  = string.Empty;
    [ObservableProperty] private string _homeLongitudeText = string.Empty;
    [ObservableProperty] private string _homeAltitudeText  = string.Empty;
    [ObservableProperty] private LocationSourceOption? _selectedLocationSource;
    [ObservableProperty] private string _gpsStatus = "Manual location selected.";
    [ObservableProperty] private string _gpsPortName = string.Empty;
    [ObservableProperty] private string _gpsBaudRateText = string.Empty;
    [ObservableProperty] private string _selectedWaypointEmoji = "📍";
    [ObservableProperty] private bool _useWaypointExpiry;
    [ObservableProperty] private DateTime? _waypointExpiryDate = DateTime.Today;
    [ObservableProperty] private string _waypointExpiryHour12 = "12";
    [ObservableProperty] private string _waypointExpiryMinute = "00";
    [ObservableProperty] private string _waypointExpirySecond = "00";
    [ObservableProperty] private string _waypointExpiryMeridiem = "PM";
    [ObservableProperty] private string _waypointNameInput = string.Empty;
    [ObservableProperty] private string _waypointDescriptionInput = string.Empty;

    public IReadOnlyList<string> WaypointExpiryHourOptions { get; } =
        Enumerable.Range(1, 12).Select(h => h.ToString("00", CultureInfo.InvariantCulture)).ToArray();

    public IReadOnlyList<string> WaypointExpiryMinuteOptions { get; } =
        Enumerable.Range(0, 60).Select(m => m.ToString("00", CultureInfo.InvariantCulture)).ToArray();

    public IReadOnlyList<string> WaypointExpirySecondOptions { get; } =
        Enumerable.Range(0, 60).Select(s => s.ToString("00", CultureInfo.InvariantCulture)).ToArray();

    public IReadOnlyList<string> WaypointExpiryMeridiemOptions { get; } = new[] { "AM", "PM" };

    public IReadOnlyList<string> WaypointEmojiOptions { get; } = new[]
    {
        "📍", "📌", "🏠", "⛺", "⚠", "🚧", "🛰", "🏴",
    };

    [RelayCommand]
    private void PickWaypointEmoji()
    {
        string? picked = EmojiPickerWindow.PickEmoji(
            Application.Current?.MainWindow,
            EmojiPickerWindow.EmojiPickerMode.WaypointIcon);
        if (!string.IsNullOrWhiteSpace(picked))
            SelectedWaypointEmoji = picked.Trim();
    }

    public IReadOnlyList<LocationSourceOption> LocationSourceOptions { get; } =
    [
        new(ManualLocationSourceValue, "Manual"),
        new(UsbSerialLocationSourceValue, "USB serial (auto)"),
    ];

    public bool IsManualLocationSource =>
        !string.Equals(SelectedLocationSource?.Value, UsbSerialLocationSourceValue, StringComparison.Ordinal);

    public bool IsUsbSerialLocationSource => !IsManualLocationSource;

    /// <summary>Resolved home location (null when unset/invalid).</summary>
    public double? HomeLatitude  { get; private set; }
    public double? HomeLongitude { get; private set; }
    public int?    HomeAltitude  { get; private set; }

    /// <summary>Raised when the home location or node positions change so the
    /// map view can refresh its markers.</summary>
    public event EventHandler? MapDataChanged;

    /// <summary>Raised when only specific node rows changed and the map can
    /// update those node markers without rebuilding the full marker layer.</summary>
    public event Action<IReadOnlyCollection<uint>>? NodeMarkersChanged;

    /// <summary>A point shown on the map: a node position or the home marker.</summary>
    public sealed record MapMarker(
        double Lat, double Lon, string Label, string Title,
        bool IsHome, bool IsWaypoint = false, bool IsExpired = false,
        uint? NodeNum = null, long? WaypointRowId = null);

    /// <summary>A polyline on the map (used for per-node location history).</summary>
    public sealed record MapPolyline(string Label, IReadOnlyList<(double Lat, double Lon)> Points);

    /// <summary>
    /// Markers for the map view: the home location (if set) plus every node
    /// that has a known position.
    /// </summary>
    public IReadOnlyList<MapMarker> GetMapMarkers()
    {
        var list = new List<MapMarker>();
        if (HomeLatitude is double hlat && HomeLongitude is double hlon)
        {
            var label = GetLocationMarkerLabel();
            list.Add(new MapMarker(hlat, hlon, label, "Location", IsHome: true));
        }

        foreach (var n in Nodes.Where(NodePassesFilter))
        {
            if (n.Latitude is not double lat || n.Longitude is not double lon) continue;
            var label = GetMapNodeLabel(n);
            list.Add(new MapMarker(lat, lon, label, GetNodeTooltipCached(n), IsHome: false, NodeNum: n.NodeNum));
        }

        foreach (var wp in Waypoints)
        {
            list.Add(new MapMarker(
                wp.Latitude,
                wp.Longitude,
                string.IsNullOrWhiteSpace(wp.Name)
                    ? (string.IsNullOrWhiteSpace(wp.IconText) ? "Waypoint" : wp.IconText)
                    : $"{wp.IconText} {wp.Name}".Trim(),
                BuildWaypointTooltip(wp),
                IsHome: false,
                IsWaypoint: true,
                IsExpired: wp.IsExpired,
                WaypointRowId: wp.Id));
        }
        return list;
    }

    /// <summary>Location history is rendered in the per-node popup minimap.</summary>
    public IReadOnlyList<MapPolyline> GetMapPolylines() => Array.Empty<MapPolyline>();

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
            _nodesByNum.Remove(n.NodeNum);
            _nodeMapStateSignatures.Remove(n.NodeNum);
            _nodeTooltipSignatures.Remove(n.NodeNum);
            _nodeTooltipCache.Remove(n.NodeNum);
            if (n.Latitude is not null && n.Longitude is not null) removedPositioned = true;
        }

        if (removedPositioned)
            MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remove persisted waypoints and refresh the map.</summary>
    public void RemoveWaypoints(IEnumerable<WaypointRecord> waypoints)
    {
        var targets = waypoints?.Where(w => w is not null).ToList();
        if (targets is null || targets.Count == 0) return;

        _waypointStore.ForgetRange(targets.Select(w => w.Id));
        foreach (var wp in targets)
            Waypoints.Remove(wp);
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forget the stored public key for the given node(s) and ask them
    /// to re-send their NodeInfo, so a changed (mismatched) key can be re-learned
    /// and trusted. Wired to the node list's "Request new keys" menu item.</summary>
    public void RequestKeys(IEnumerable<MeshRF.Nodes.NodeRecord> nodes,
                            ChannelConfig? channel = null)
    {
        var targets = nodes?.Where(n => n is not null).ToList();
        if (targets is null || targets.Count == 0) return;
        var requestChannel = ResolveRequestChannel(channel);
        if (requestChannel is null) return;

        foreach (var n in targets)
        {
            if (_myNodeNum != 0 && n.NodeNum == _myNodeNum) continue;
            _nodeStore.ClearPublicKey(n.NodeNum);
            uint packetId = NextPacketId();
            SendNodeInfoExchangeRequest(n.NodeNum, requestChannel, packetId);
            var name = NodeDisplayName(n.NodeNum);
            Log($"  requested new keys from {name}");
            // Surface the request in the node's DM tab (and persist it) so the
            // user sees it happened and the later NodeInfo reply lands here.
            var convo = OpenConversation(n.NodeNum, name, focus: false);
            var noteText = $"Requested new keys from {name}\u2026";
            convo.Add(new ChannelMessage
            {
                FromId = "keys",
                Text = noteText,
                IsOutgoing = true,
                PacketId = packetId,
            });
            PersistConversationNote(n.NodeNum, outgoing: true, packetId,
                                    "keys", noteText);
        }

        ReloadNodes();
    }

    /// <summary>Ask the given node(s) for NodeInfo using a custom request-only
    /// packet (empty NODEINFO payload + want_response), so we do not advertise
    /// our own NodeInfo in the request packet.</summary>
    public void RequestNodeInfoOnly(IEnumerable<MeshRF.Nodes.NodeRecord> nodes,
                                    ChannelConfig? channel = null)
    {
        var targets = nodes?.Where(n => n is not null).ToList();
        if (targets is null || targets.Count == 0) return;
        var requestChannel = ResolveRequestChannel(channel);
        if (requestChannel is null) return;

        foreach (var n in targets)
        {
            if (_myNodeNum != 0 && n.NodeNum == _myNodeNum) continue;
            uint packetId = NextPacketId();
            SendNodeInfoRequestOnly(n.NodeNum, requestChannel, packetId);
            var name = NodeDisplayName(n.NodeNum);
            Log($"  requested NodeInfo from {name}");
            var convo = OpenConversation(n.NodeNum, name, focus: false);
            var noteText = $"Requested NodeInfo from {name}\u2026";
            convo.Add(new ChannelMessage
            {
                FromId = "nodeinfo",
                Text = noteText,
                IsOutgoing = true,
                PacketId = packetId,
            });
            PersistConversationNote(n.NodeNum, outgoing: true, packetId,
                                    "nodeinfo", noteText);
        }
    }

    /// <summary>Ask the given node(s) to exchange NodeInfo with us without
    /// clearing any stored keys. This sends our NodeInfo directed at the peer
    /// with want_response set so it replies with its own NodeInfo.</summary>
    public void ExchangeNodeInfo(IEnumerable<MeshRF.Nodes.NodeRecord> nodes,
                                 ChannelConfig? channel = null)
    {
        var targets = nodes?.Where(n => n is not null).ToList();
        if (targets is null || targets.Count == 0) return;
        var requestChannel = ResolveRequestChannel(channel);
        if (requestChannel is null) return;

        foreach (var n in targets)
        {
            if (_myNodeNum != 0 && n.NodeNum == _myNodeNum) continue;
            uint packetId = NextPacketId();
            SendNodeInfoExchangeRequest(n.NodeNum, requestChannel, packetId);
            var name = NodeDisplayName(n.NodeNum);
            Log($"  exchanged NodeInfo with {name}");
            var convo = OpenConversation(n.NodeNum, name, focus: false);
            var noteText = $"Exchanged NodeInfo with {name}\u2026";
            convo.Add(new ChannelMessage
            {
                FromId = "nodeinfo",
                Text = noteText,
                IsOutgoing = true,
                PacketId = packetId,
            });
            PersistConversationNote(n.NodeNum, outgoing: true, packetId,
                                    "nodeinfo", noteText);
        }
    }

    /// <summary>
    /// Send a Meshtastic-style traceroute (TRACEROUTE_APP) to <paramref name="node"/>:
    /// an empty RouteDiscovery with <c>want_response</c> set, addressed to the
    /// node on the primary channel. The destination (and any relays) reply with
    /// the accumulated hop path, which we render in the log and the node's DM
    /// tab. Rate-limited to one request per <see cref="TracerouteCooldown"/>.
    /// </summary>
    public async Task TracerouteAsync(MeshRF.Nodes.NodeRecord? node)
    {
        if (node is null) return;
        if (_myNodeNum != 0 && node.NodeNum == _myNodeNum)
        {
            Status = "You can't traceroute your own node.";
            Log("  " + Status);
            return;
        }
        if (!CanTransmit || _myNodeNum == 0)
        {
            Status = "Traceroute needs a transmit-capable device and your node id set.";
            Log("  " + Status);
            return;
        }

        var remaining = TracerouteCooldown - (DateTime.UtcNow - _lastTracerouteUtc);
        if (remaining > TimeSpan.Zero)
        {
            Status = $"Traceroute on cooldown — wait {Math.Ceiling(remaining.TotalSeconds):F0}s.";
            Log("  " + Status);
            return;
        }

        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null)
        {
            Status = "Traceroute needs a primary channel.";
            Log("  " + Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodeTraceroute(
                primary.Config, _myNodeNum, node.NodeNum, packetId,
                hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            if (await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable))
            {
                _lastTracerouteUtc = DateTime.UtcNow;
                _pendingTraceroutes[packetId] = node.NodeNum;
                var name = NodeDisplayName(node.NodeNum);
                Status = $"Traceroute requested to {name}";
                Log($"  traceroute → {name} (id {packetId:x8})");
                // Echo the request into the node's DM tab so the reply lands there.
                var convo = OpenConversation(node.NodeNum, name, focus: false);
                var noteText = $"Traceroute requested to {name}\u2026";
                convo.Add(new ChannelMessage
                {
                    FromId = "traceroute",
                    Text = noteText,
                    IsOutgoing = true,
                    PacketId = packetId,
                });
                PersistConversationNote(node.NodeNum, outgoing: true, packetId,
                                        "traceroute", noteText);
            }
            else
            {
                Status = "Transmit failed (device cannot transmit).";
                Log("  " + Status);
            }
        }
        catch (Exception ex)
        {
            Status = $"Traceroute error: {ex.Message}";
            Log("  " + Status);
        }
    }

    /// <summary>
    /// Request <paramref name="node"/>'s current position (POSITION_APP): an
    /// empty Position payload with <c>want_response</c> set, addressed to the
    /// node on the primary channel, prompting it to reply with its location.
    /// The reply is handled by the normal POSITION_APP receive path (updating
    /// the node row / map). Rate-limited to one request per
    /// <see cref="PositionRequestCooldown"/>.
    /// </summary>
    public async Task RequestPositionAsync(MeshRF.Nodes.NodeRecord? node,
                                           ChannelConfig? channel = null)
    {
        if (node is null) return;
        if (_myNodeNum != 0 && node.NodeNum == _myNodeNum)
        {
            Status = "You can't request a position from your own node.";
            Log("  " + Status);
            return;
        }
        if (!CanTransmit || _myNodeNum == 0)
        {
            Status = "Position request needs a transmit-capable device and your node id set.";
            Log("  " + Status);
            return;
        }

        var remaining = PositionRequestCooldown - (DateTime.UtcNow - _lastPositionRequestUtc);
        if (remaining > TimeSpan.Zero)
        {
            Status = $"Position request on cooldown — wait {Math.Ceiling(remaining.TotalSeconds):F0}s.";
            Log("  " + Status);
            return;
        }

        var requestChannel = ResolveRequestChannel(channel);
        if (requestChannel is null)
        {
            Status = "Position request needs a channel.";
            Log("  " + Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodePositionRequest(
                requestChannel, _myNodeNum, node.NodeNum, packetId,
                hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            if (await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable))
            {
                _lastPositionRequestUtc = DateTime.UtcNow;
                var name = NodeDisplayName(node.NodeNum);
                Status = $"Position requested from {name}";
                Log($"  position request → {name} (id {packetId:x8})");
                // Echo the request into the node's DM tab so the reply lands there.
                var convo = OpenConversation(node.NodeNum, name, focus: false);
                var noteText = $"Position requested from {name}\u2026";
                convo.Add(new ChannelMessage
                {
                    FromId = "position",
                    Text = noteText,
                    IsOutgoing = true,
                    PacketId = packetId,
                });
                PersistConversationNote(node.NodeNum, outgoing: true, packetId,
                                        "position", noteText);
            }
            else
            {
                Status = "Transmit failed (device cannot transmit).";
                Log("  " + Status);
            }
        }
        catch (Exception ex)
        {
            Status = $"Position request error: {ex.Message}";
            Log("  " + Status);
        }
    }

    /// <summary>
    /// Request <paramref name="node"/>'s current telemetry (TELEMETRY_APP)
    /// using the selected shared channel.
    /// </summary>
    public async Task RequestTelemetryAsync(MeshRF.Nodes.NodeRecord? node,
                                            ChannelConfig? channel = null)
    {
        if (node is null) return;
        if (_myNodeNum != 0 && node.NodeNum == _myNodeNum)
        {
            Status = "You can't request telemetry from your own node.";
            Log("  " + Status);
            return;
        }
        if (!CanTransmit || _myNodeNum == 0)
        {
            Status = "Telemetry request needs a transmit-capable device and your node id set.";
            Log("  " + Status);
            return;
        }

        var requestChannel = ResolveRequestChannel(channel);
        if (requestChannel is null)
        {
            Status = "Telemetry request needs a channel.";
            Log("  " + Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodeTelemetryRequest(
                requestChannel, _myNodeNum, node.NodeNum, packetId,
                hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            if (await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable))
            {
                var name = NodeDisplayName(node.NodeNum);
                Status = $"Telemetry requested from {name}";
                Log($"  telemetry request → {name} (id {packetId:x8})");
                var convo = OpenConversation(node.NodeNum, name, focus: false);
                var noteText = $"Telemetry requested from {name}\u2026";
                convo.Add(new ChannelMessage
                {
                    FromId = "telemetry",
                    Text = noteText,
                    IsOutgoing = true,
                    PacketId = packetId,
                });
                PersistConversationNote(node.NodeNum, outgoing: true, packetId,
                                        "telemetry", noteText);
            }
            else
            {
                Status = "Transmit failed (device cannot transmit).";
                Log("  " + Status);
            }
        }
        catch (Exception ex)
        {
            Status = $"Telemetry request error: {ex.Message}";
            Log("  " + Status);
        }
    }

    /// <summary>
    /// Exchange location with <paramref name="node"/> by requesting its
    /// position and also sending our current position directly to it.
    /// </summary>
    public async Task ExchangeLocationAsync(MeshRF.Nodes.NodeRecord? node,
                                            ChannelConfig? channel = null)
    {
        if (node is null) return;
        var requestChannel = ResolveRequestChannel(channel);
        if (requestChannel is null) return;
        await RequestPositionAsync(node, requestChannel);
        ReplyWithPosition(node.NodeNum, channel: requestChannel);
    }

    /// <summary>Builds the multi-line tooltip shown when hovering a node on the
    /// map: identity, telemetry, and how long ago it was last heard.</summary>
    private string BuildNodeTooltip(MeshRF.Nodes.NodeRecord n)
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
            sb.Append("\nHW: ").Append(HardwareModels.Display(n.HwModel));

        // Position.
        if (n.Latitude is double la && n.Longitude is double lo)
        {
            sb.Append('\n').Append(la.ToString("F5", CultureInfo.InvariantCulture))
              .Append(", ").Append(lo.ToString("F5", CultureInfo.InvariantCulture));
            if (n.AltitudeM is int alt) sb.Append("  ").Append(FormatAltitude(alt));
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
        if (n.TemperatureC is float t) env.Add(FormatTemperature(t));
        if (n.RelativeHumidityPct is float h) env.Add($"{h:F0}% RH");
        if (n.BarometricPressureHpa is float p) env.Add(FormatPressure(p));
        if (env.Count > 0) sb.Append('\n').Append(string.Join("  ", env));

        // Last heard (relative).
        sb.Append("\nHeard ").Append(FormatAge(n.LastHeardEpoch));
        return sb.ToString();
    }

    private string BuildWaypointTooltip(WaypointRecord wp)
    {
          var fromName = NodeDisplayName(wp.FromNode);
        var sb = new System.Text.StringBuilder();
                sb.Append(string.IsNullOrWhiteSpace(wp.IconText) ? wp.DisplayName : $"{wp.IconText} {wp.DisplayName}")
            .Append("\nFrom ").Append(fromName)
          .Append("\n").Append(wp.Latitude.ToString("F5", CultureInfo.InvariantCulture))
          .Append(", ").Append(wp.Longitude.ToString("F5", CultureInfo.InvariantCulture));
        if (wp.AltitudeM is int alt) sb.Append("  ").Append(FormatAltitude(alt));
        if (!string.IsNullOrWhiteSpace(wp.Description))
            sb.Append("\n").Append(wp.Description);
        if (wp.LockedTo != 0)
            sb.Append("\nLocked to !").Append(wp.LockedTo.ToString("x8", CultureInfo.InvariantCulture));
        if (wp.ExpireEpoch != 0)
            sb.Append("\nExpires ").Append(FormatLocalDateTime(DateTimeOffset.FromUnixTimeSeconds(wp.ExpireEpoch).LocalDateTime))
              .Append(wp.IsExpired ? "  [EXPIRED]" : string.Empty);
        sb.Append("\nReceived ").Append(FormatAge(wp.RxEpoch));
        return sb.ToString();
    }

    private static string FormatLocalDateTime(DateTime localTime) =>
        localTime.ToString(UiDateTimeFormat, CultureInfo.CurrentCulture);

    private string FormatTemperature(float temperatureC) =>
        DisplayUnits.FormatTemperature(temperatureC, CurrentUnitSystem);

    private string FormatPressure(float pressureHpa) =>
        DisplayUnits.FormatPressure(pressureHpa, CurrentUnitSystem);

    private string FormatAltitude(int altitudeMeters) =>
        DisplayUnits.FormatAltitude(altitudeMeters, CurrentUnitSystem);

    private string GetMapNodeLabel(NodeRecord n) => MapNodeLabelMode switch
    {
        "Node Number" => n.DisplayId,
        "Long Name" => !string.IsNullOrWhiteSpace(n.LongName) ? n.LongName : n.DisplayId,
        "Short Name" => !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName : n.DisplayId,
        "Temperature" => n.TemperatureC is float t ? FormatTemperature(t) : n.DisplayId,
        "Humidity" => n.RelativeHumidityPct is float h ? $"{h:F0}%" : n.DisplayId,
        "Pressure" => n.BarometricPressureHpa is float p ? FormatPressure(p) : n.DisplayId,
        _ => !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName : n.DisplayId,
    };

    private string GetLocationMarkerLabel()
    {
        if (_myNodeNum == 0)
            return "Location";

        var self = new NodeRecord
        {
            NodeNum = _myNodeNum,
            UserId = $"!{_myNodeNum:x8}",
            LongName = MyLongName ?? string.Empty,
            ShortName = MyShortName ?? string.Empty,
        };

        return GetMapNodeLabel(self);
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

    /// <summary>Meshtastic hardware models (full firmware <c>HardwareModel</c>
    /// enum). "UNSET" leaves it unspecified.</summary>
    public IReadOnlyList<string> HwModelOptions { get; } = HardwareModels.AllNames;

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
    private bool _suppressLoraParamSync;

    public MainViewModel()
    {
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _pkcDecodeQueue = Channel.CreateBounded<PkcDecodeWorkItem>(new BoundedChannelOptions(MaxQueuedPkcDecodes)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _ = Task.Run(RunPkcDecodeWorkerAsync);

        _dbWriteNodeStore = new NodeStore(NodeStore.DefaultPath);
        _dbWriteWaypointStore = new WaypointStore(NodeStore.DefaultPath);
        _dbWriteQueue = Channel.CreateBounded<Action<NodeStore, WaypointStore>>(new BoundedChannelOptions(MaxQueuedDbWrites)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _dbWriteWorkerTask = Task.Run(RunDbWriteWorkerAsync);

        _nodesViewRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = NodesViewRefreshInterval,
        };
        _nodesViewRefreshTimer.Tick += OnNodesViewRefreshTimerTick;
        
        _filterChangeDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100), // Batch filter changes within 100ms
        };
        _filterChangeDebounceTimer.Tick += OnFilterChangeDebounceTimerTick;

        _settings = AppSettings.Load();
        _lastSelectedChannelIndex = _settings.LastSelectedChannelIndex >= 0
            ? _settings.LastSelectedChannelIndex
            : _settings.SelectedChannelIndex;
        var soon = DateTime.Now.AddHours(1);
        WaypointExpiryHour12 = ((soon.Hour + 11) % 12 + 1).ToString("00", CultureInfo.InvariantCulture);
        WaypointExpiryMinute = soon.Minute.ToString("00", CultureInfo.InvariantCulture);
        WaypointExpirySecond = soon.Second.ToString("00", CultureInfo.InvariantCulture);
        WaypointExpiryMeridiem = soon.Hour >= 12 ? "PM" : "AM";
        NodesView = CollectionViewSource.GetDefaultView(Nodes);
        NodesView.Filter = o => o is NodeRecord n && NodePassesFilter(n);
        _gpsService.StatusChanged += HandleGpsStatusChanged;
        _gpsService.FixReceived += HandleGpsFixReceived;

        // Apply persisted values BEFORE wiring change handlers fire usefully.
        // We rely on the [ObservableProperty] setters to fire OnXChanged,
        // which we guard against re-saving until _settingsLoaded becomes true.
        if (Enum.TryParse<Region>(_settings.Region, out var r)) SelectedRegion = r;
        if (Enum.TryParse<LoraPreset>(_settings.Preset, out var p)) SelectedPreset = p;
        // Restore manual overrides if they were saved; otherwise derive from the preset.
        if (_settings.OverrideSf != 0 || _settings.OverrideBwHz != 0 || _settings.OverrideCr != 0)
        {
            var preset = SelectedPreset;
            var defaults = MeshRF.LoraParamsHelper.FromPreset(preset);
            OverrideSf    = _settings.OverrideSf    != 0 ? _settings.OverrideSf    : defaults.Sf;
            OverrideBwKhz = _settings.OverrideBwHz  != 0 ? _settings.OverrideBwHz / 1000.0 : defaults.BwKhz;
            OverrideCr    = _settings.OverrideCr    != 0 ? _settings.OverrideCr    : defaults.Cr;
        }
        else
        {
            ApplyPresetToLoraParams(SelectedPreset);
        }
        LnaGainDb = _settings.LnaGainDb;
        VgaGainDb = _settings.VgaGainDb;
        AmpEnable = _settings.AmpEnable;
        TxGainDb = _settings.TxGainDb;
        TxAmpEnable = _settings.TxAmpEnable;
        AgcEnable = _settings.AgcEnable;
        AgcTargetDbfs = _settings.AgcTargetDbfs;
        RtlGainDb = _settings.RtlGainDb;
        RtlAgcEnable = _settings.RtlAgcEnable;
        BiasTee = _settings.BiasTee;
        DcBlockEnable = _settings.DcBlockEnable;
        Theme = _settings.Theme;
        UnitSystemName = _settings.UnitSystem;
        WaterfallColormap = _settings.WaterfallColormap;
        RingtoneMode = _settings.RingtoneMode;
        RingtoneVolume = _settings.RingtoneVolume;
        RingtoneRtttl = _settings.RingtoneRtttl;
        WaterfallAutoLevels = _settings.WaterfallAutoLevels;
        WaterfallFloorDb = _settings.WaterfallFloorDb;
        WaterfallCeilDb = _settings.WaterfallCeilDb;
        WaterfallRowsPerSecond = Math.Clamp(_settings.WaterfallRowsPerSecond, 5.0, 240.0);

        // Local node identity (for recognising direct messages).
        _myNodeNum = _settings.UserNodeNum;
        MyNodeIdText = _myNodeNum != 0 ? $"!{_myNodeNum:x8}" : string.Empty;
        MyLongName = _settings.UserLongName;
        MyShortName = _settings.UserShortName;
        MyRole = string.IsNullOrEmpty(_settings.UserRole) ? "Client" : _settings.UserRole;

        MyHwModel = string.IsNullOrEmpty(_settings.UserHwModel) ? "UNSET" : _settings.UserHwModel;
        RebroadcastMode = string.IsNullOrEmpty(_settings.RebroadcastMode) ? "ALL" : _settings.RebroadcastMode;
        HopLimit = Math.Clamp(_settings.HopLimit, 1, 7);
        OkToMqtt = _settings.OkToMqtt;
        RoutingRelayEnabled = _settings.RoutingRelayEnabled;
        AutoReportNodeInfoEnabled = _settings.AutoReportNodeInfoEnabled;
        AutoReportNodeInfoSeconds = Math.Max(5, _settings.AutoReportNodeInfoSeconds);
        AutoReportPositionEnabled = _settings.AutoReportPositionEnabled;
        AutoReportPositionSeconds = Math.Max(5, _settings.AutoReportPositionSeconds);
        AutoReportDeviceMetricsEnabled = _settings.AutoReportDeviceMetricsEnabled;
        AutoReportDeviceMetricsSeconds = Math.Max(5, _settings.AutoReportDeviceMetricsSeconds);
        var now = DateTime.UtcNow;
        _nextAutoNodeInfoUtc = AutoReportNodeInfoEnabled
            ? now.AddSeconds(Math.Max(5, AutoReportNodeInfoSeconds))
            : DateTime.MinValue;
        _nextAutoPositionUtc = AutoReportPositionEnabled
            ? now.AddSeconds(Math.Max(5, AutoReportPositionSeconds))
            : DateTime.MinValue;
        _nextAutoDeviceMetricsUtc = AutoReportDeviceMetricsEnabled
            ? now.AddSeconds(Math.Max(5, AutoReportDeviceMetricsSeconds))
            : DateTime.MinValue;
        UpdateAutoReportLastSentSummary();
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
        RefreshMyPrivateKeyCache();

        _manualHomeLatitude  = _settings.HomeLatitude;
        _manualHomeLongitude = _settings.HomeLongitude;
        _manualHomeAltitude  = _settings.HomeAltitude;
        GpsPortName = _settings.GpsSerialPort ?? string.Empty;
        GpsBaudRateText = _settings.GpsBaudRate > 0
            ? _settings.GpsBaudRate.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        // Populate the text boxes without retriggering UpdateHomeLocation: doing
        // so one box at a time would transiently null the not-yet-set coordinate
        // (and persist that null) before the second box is assigned.
        _suppressHomeTextUpdate = true;
        HomeLatitudeText  = _manualHomeLatitude?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
        HomeLongitudeText = _manualHomeLongitude?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
        HomeAltitudeText  = DisplayUnits.FormatAltitudeInput(_manualHomeAltitude, CurrentUnitSystem);
        _suppressHomeTextUpdate = false;
        HomeAltitude = _manualHomeAltitude;
        SelectedLocationSource = LocationSourceOptions.FirstOrDefault(o =>
            string.Equals(o.Value, _settings.HomeLocationSource, StringComparison.OrdinalIgnoreCase))
            ?? LocationSourceOptions[0];

        // Restore node list filter state.
        NodeSearchText        = _settings.NodeFilterSearch;
        NodeHopsFilter        = _settings.NodeFilterHops;
        NodeKeyFilter         = _settings.NodeFilterKey;
        if (string.Equals(_settings.NodeFilterLocation, "Invalid", StringComparison.Ordinal))
        {
            NodeLocationFilter = "Any";
            HideInvalidNodeLocations = true;
        }
        else
        {
            NodeLocationFilter = NodeLocationFilterOptions.Contains(_settings.NodeFilterLocation)
                ? _settings.NodeFilterLocation
                : "Any";
            HideInvalidNodeLocations = _settings.NodeFilterHideInvalidLocations;
        }
        NodeIgnoredFilter     = _settings.NodeFilterIgnored;
        NodeMqttFilter        = NodeMqttFilterOptions.Contains(_settings.NodeFilterMqtt)
            ? _settings.NodeFilterMqtt
            : "Any";
        NodeTemperatureFilter = _settings.NodeFilterTemperature;
        NodeHumidityFilter    = _settings.NodeFilterHumidity;
        NodePressureFilter    = _settings.NodeFilterPressure;
        MapNodeLabelMode      = string.IsNullOrWhiteSpace(_settings.MapNodeLabelMode)
            ? "Node Number"
            : _settings.MapNodeLabelMode;
        NodeDistanceKmText    = _settings.NodeFilterDistanceKm;
        NodeMaxAgeMinutesText = _settings.NodeFilterMaxAgeMinutes;

        RebuildSlots(snapToDefault: false);
        // Restore the user's last slot/freq if it's still valid for this preset.
        if (_settings.Slot >= 1 && _settings.Slot <= Slots.Count)
        {
            SelectedSlot = _settings.Slot;
            CenterFreqMHz = _settings.CenterFreqMHz;
        }

        // Push gains into the native core so they take effect when RX starts.
        _core.SetGains(LnaGainDb, VgaGainDb, AmpEnable);

        // Select persisted RX/TX backends before probing names/status below.
        // Older settings only had DeviceKind; treat it as RX and default TX to HackRF.
        var rxDeviceText = string.IsNullOrWhiteSpace(_settings.RxDeviceKind)
            ? _settings.DeviceKind
            : _settings.RxDeviceKind;
        var rxDeviceKind = Enum.TryParse<RadioDeviceKind>(rxDeviceText, out var rxDk)
            ? rxDk : RadioDeviceKind.Auto;
        if (rxDeviceKind == RadioDeviceKind.Auto) rxDeviceKind = RadioDeviceKind.Null;
        var txDeviceKind = Enum.TryParse<RadioDeviceKind>(_settings.TxDeviceKind, out var txDk)
            ? txDk : RadioDeviceKind.HackRf;
        if (txDeviceKind is RadioDeviceKind.Auto or RadioDeviceKind.RtlSdr)
            txDeviceKind = RadioDeviceKind.Null;
        _core.SetRxDevice(rxDeviceKind);
        _core.SetTxDevice(txDeviceKind);
        DeviceOptions = BuildRxDeviceOptions();
        TxDeviceOptions = BuildTxDeviceOptions();
        _suppressDeviceUpdate = true;
        SelectedDevice = DeviceOptions.FirstOrDefault(o => o.Kind == rxDeviceKind)
                             ?? DeviceOptions[0];
        SelectedTxDevice = TxDeviceOptions.FirstOrDefault(o => o.Kind == txDeviceKind)
                             ?? TxDeviceOptions.FirstOrDefault(o => o.Kind == RadioDeviceKind.HackRf)
                             ?? TxDeviceOptions[0];
        _suppressDeviceUpdate = false;
        RefreshSampleRateSelection(rxDeviceKind, GetSavedRxSampleRateHz(rxDeviceKind));

        // Now that the backend is known, push the gains appropriate for it and
        // apply the RTL-SDR bias-T option.
        PushGains();
        _core.SetDeviceOption("bias_tee", BiasTee ? 1 : 0);
        _core.SetDcBlock(DcBlockEnable);
        OnPropertyChanged(nameof(IsRtlSdr));
        OnPropertyChanged(nameof(IsHackRf));
        OnPropertyChanged(nameof(CanTransmit));

        // Bring up channel and node tabs before logging anything, so boot
        // messages land on the Primary tab.
        ReloadChannels();
        UpsertSelf();
        ReloadNodes();
        ReloadWaypoints();
        ReloadMessages();
        LoadChatHistory();

        Status = $"Idle (RX {_core.DeviceName}, TX {_core.TxDeviceName})";
        Log(DeviceBadge);
        if (ShouldLogDeviceStatus(_core.DeviceStatus))
            Log(_core.DeviceStatus);

        _settingsLoaded = true;
        SpectrumCenterHz = CenterFreqMHz * 1_000_000.0;
        ApplyLocationSourceSelection(startOrStopGps: true, saveSettings: false);
    }

    /// <summary>Refresh the in-memory <see cref="Nodes"/> collection from disk.</summary>
    public void ReloadNodes()
    {
        _dirtyNodeNums.Clear();
        _nodesByNum.Clear();
        _pkcSenderPublicKeyBytes.Clear();
        _nodeLocationHistoryCounts.Clear();
        foreach (var pair in _nodeStore.LocationHistoryCounts())
            _nodeLocationHistoryCounts[pair.Key] = pair.Value;
        _nodeMapStateSignatures.Clear();
        _nodeTooltipSignatures.Clear();
        _nodeTooltipCache.Clear();
        Nodes.Clear();
        // Our own node lives in the database so chats can show our name, but we
        // don't list ourselves among the discovered peers.
        foreach (var n in _nodeStore.All())
            if (_myNodeNum == 0 || n.NodeNum != _myNodeNum)
            {
                Nodes.Add(n);
                _nodesByNum[n.NodeNum] = n;
            }
        // Keep any open DM tabs' telemetry panels in sync with the latest data.
        foreach (var convo in Tabs.OfType<ConversationViewModel>())
            convo.Node = _nodesByNum.GetValueOrDefault(convo.NodeNum);

        // Keep the filter membership cache aligned with the currently loaded
        // node set so view filtering remains accurate after full reloads.
        lock (_filterCriteriaSyncLock)
        {
            _nodeFilterCache.Clear();
            foreach (var n in _nodesByNum.Values)
                if (NodePassesFilterWithCriteria(n, _currentFilterCriteria))
                    _nodeFilterCache.Add(n.NodeNum);
        }

        RebuildNodeMapStateSignatures();
        NodesView?.Refresh();
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkNodeDirty(uint nodeNum)
    {
        if (nodeNum == 0) return;
        if (_myNodeNum != 0 && nodeNum == _myNodeNum) return;
        _dirtyNodeNums.Add(nodeNum);
        _nodesDirty = true;
    }

    private void RefreshConversationNode(uint nodeNum)
    {
        var node = _nodesByNum.GetValueOrDefault(nodeNum);
        foreach (var convo in Tabs.OfType<ConversationViewModel>())
            if (convo.NodeNum == nodeNum)
                convo.Node = node;
    }

    private void RefreshNodeDisplayNameReferences(uint nodeNum)
    {
        var resolvedName = NodeDisplayName(nodeNum);

        foreach (var channel in Channels)
        {
            foreach (var message in channel.Messages)
            {
                if (message.SenderNodeNum == nodeNum &&
                    !string.Equals(message.FromId, resolvedName, StringComparison.Ordinal))
                    message.FromId = resolvedName;
            }
        }

        foreach (var convo in Tabs.OfType<ConversationViewModel>())
        {
            if (convo.NodeNum == nodeNum &&
                !string.Equals(convo.PeerName, resolvedName, StringComparison.Ordinal))
                convo.PeerName = resolvedName;

            foreach (var message in convo.Messages)
            {
                if (message.SenderNodeNum == nodeNum &&
                    !string.Equals(message.FromId, resolvedName, StringComparison.Ordinal))
                    message.FromId = resolvedName;
            }
        }
    }

    private bool RememberUndecodedPacket(MeshHeader header)
    {
        ulong key = ((ulong)header.From << 32) ^ header.PacketId;
        if (header.PacketId == 0)
        {
            key ^= ((ulong)header.To << 1);
            key ^= header.ChannelHash;
        }

        if (!_recentUndecodedPacketKeys.Add(key))
            return false;

        _recentUndecodedPacketOrder.Enqueue(key);
        while (_recentUndecodedPacketOrder.Count > RecentUndecodedPacketLimit)
            _recentUndecodedPacketKeys.Remove(_recentUndecodedPacketOrder.Dequeue());
        return true;
    }

    /// <summary>Apply database-backed updates for only the node ids that changed.</summary>
    private bool ApplyDirtyNodeUpdates()
    {
        if (_dirtyNodeNums.Count == 0)
        {
            // If no concrete node ids are marked dirty there is nothing
            // incremental to apply; avoid a full reload (Nodes.Clear + map
            // refresh) because that causes visible UI flicker.
            return false;
        }

        // The first on-air discovery after startup is safest as a full reload,
        // which avoids grid view state lagging behind until user interaction.
        if (Nodes.Count == 0)
        {
            ReloadNodes();
            return false;
        }

        var changedNodeNums = _dirtyNodeNums
            .Take(MaxDirtyNodeUpdatesPerTick)
            .ToArray();
        var mapChangedNodeNums = new List<uint>(changedNodeNums.Length);
        bool waypointTooltipsNeedRefresh = false;

        foreach (var nodeNum in changedNodeNums)
        {
            _pkcSenderPublicKeyBytes.Remove(nodeNum);
            var latest = _nodeStore.Get(nodeNum);
            var existing = Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);

            if (latest is null)
            {
                if (existing is not null)
                    Nodes.Remove(existing);
                _nodesByNum.Remove(nodeNum);
                lock (_filterCriteriaSyncLock)
                    _nodeFilterCache.Remove(nodeNum);
                if (UpdateNodeMapStateSignature(nodeNum, null))
                    mapChangedNodeNums.Add(nodeNum);
                _nodeTooltipSignatures.Remove(nodeNum);
                _nodeTooltipCache.Remove(nodeNum);
                RefreshConversationNode(nodeNum);
                continue;
            }

            lock (_filterCriteriaSyncLock)
            {
                if (NodePassesFilterWithCriteria(latest, _currentFilterCriteria))
                    _nodeFilterCache.Add(nodeNum);
                else
                    _nodeFilterCache.Remove(nodeNum);
            }

            bool keepDefaultOrder = ShouldKeepDefaultNodeOrder();
            if (existing is null)
            {
                if (keepDefaultOrder)
                    InsertNodeByDefaultOrder(latest);
                else
                    Nodes.Add(latest);
            }
            else
            {
                if (keepDefaultOrder)
                {
                    int existingIndex = Nodes.IndexOf(existing);
                    // Keep existing rows in place so a fresh sighting only
                    // updates the content instead of re-sorting the whole grid.
                    // That avoids the row-move stutter the user sees when many
                    // packets arrive in a burst.
                    if (existingIndex >= 0)
                    {
                        Nodes[existingIndex] = latest;
                    }
                    else
                    {
                        int targetIndex = FindDefaultInsertIndex(latest);
                        targetIndex = Math.Clamp(targetIndex, 0, Nodes.Count);
                        Nodes.Insert(targetIndex, latest);
                    }
                }
                else
                {
                    int index = Nodes.IndexOf(existing);
                    if (index >= 0)
                        Nodes[index] = latest;
                }
            }

            _nodesByNum[nodeNum] = latest;
            if (UpdateNodeMapStateSignature(nodeNum, latest))
                mapChangedNodeNums.Add(nodeNum);

            RefreshConversationNode(nodeNum);
            RefreshNodeDisplayNameReferences(nodeNum);
            if (!waypointTooltipsNeedRefresh && Waypoints.Any(w => w.FromNode == nodeNum))
                waypointTooltipsNeedRefresh = true;
        }

        foreach (var nodeNum in changedNodeNums)
            _dirtyNodeNums.Remove(nodeNum);
        RefreshNodesViewIfNeeded();
        if (mapChangedNodeNums.Count > 0)
            NodeMarkersChanged?.Invoke(mapChangedNodeNums);
        if (waypointTooltipsNeedRefresh)
            MapDataChanged?.Invoke(this, EventArgs.Empty);
        return _dirtyNodeNums.Count > 0;
    }

    private void RebuildNodeMapStateSignatures()
    {
        _nodeMapStateSignatures.Clear();
        foreach (var n in _nodesByNum.Values)
            _nodeMapStateSignatures[n.NodeNum] = ComputeNodeMapStateSignature(n);
    }

    private bool UpdateNodeMapStateSignature(uint nodeNum, NodeRecord? node)
    {
        if (node is null)
            return _nodeMapStateSignatures.Remove(nodeNum);

        int sig = ComputeNodeMapStateSignature(node);
        if (_nodeMapStateSignatures.TryGetValue(nodeNum, out int oldSig) && oldSig == sig)
            return false;

        _nodeMapStateSignatures[nodeNum] = sig;
        return true;
    }

    private int ComputeNodeMapStateSignature(NodeRecord n)
    {
        var h = new HashCode();
        var latOpt = n.Latitude;
        var lonOpt = n.Longitude;
        if (!NodePassesFilter(n) || latOpt is null || lonOpt is null)
        {
            h.Add(false);
            return h.ToHashCode();
        }

        bool visible = true;
        h.Add(visible);

        double lat = latOpt.Value;
        double lon = lonOpt.Value;
        var label = GetMapNodeLabel(n);
        h.Add(lat);
        h.Add(lon);
        h.Add(label, StringComparer.Ordinal);
        return h.ToHashCode();
    }

    private bool ShouldKeepDefaultNodeOrder()
    {
        var view = NodesView;
        if (view is null) return true;
        bool hasSort = view.SortDescriptions.Count > 0;
        bool hasGroup = view.GroupDescriptions?.Count > 0;
        return !hasSort && !hasGroup;
    }

    private void InsertNodeByDefaultOrder(NodeRecord node)
    {
        int insertAt = FindDefaultInsertIndex(node);
        Nodes.Insert(insertAt, node);
    }

    private int FindDefaultInsertIndex(NodeRecord node, uint? skipNodeNum = null)
    {
        int insertAt = 0;
        foreach (var current in Nodes)
        {
            if (skipNodeNum.HasValue && current.NodeNum == skipNodeNum.Value)
                continue;

            if (node.NodeNum < current.NodeNum) break;
            insertAt++;
        }

        return insertAt;
    }

    private void RefreshNodesViewIfNeeded()
    {
        var view = NodesView;
        if (view is null) return;

        bool hasActiveFilter =
            !string.IsNullOrWhiteSpace(NodeSearchText) ||
            !string.Equals(NodeHopsFilter, "Any", StringComparison.Ordinal) ||
            !string.Equals(NodeKeyFilter, "Any", StringComparison.Ordinal) ||
            !string.Equals(NodeLocationFilter, "Any", StringComparison.Ordinal) ||
            HideInvalidNodeLocations ||
            !string.Equals(NodeIgnoredFilter, "Show all", StringComparison.Ordinal) ||
            !string.Equals(NodeMqttFilter, "Any", StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(NodeDistanceKmText) ||
            !string.IsNullOrWhiteSpace(NodeMaxAgeMinutesText);

        bool hasSortOrGroup = view.SortDescriptions.Count > 0 || view.GroupDescriptions?.Count > 0;

        // Collection change notifications already keep sorted/grouped views in
        // sync for our add/remove/replace updates; avoid forcing extra refresh
        // pulses unless a filter is active.
        if (!hasActiveFilter)
        {
            _nodesViewRefreshPending = false;
            if (_nodesViewRefreshTimer.IsEnabled)
                _nodesViewRefreshTimer.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = now - _lastNodesViewRefreshUtc;
        if (elapsed < NodesViewRefreshInterval)
        {
            _nodesViewRefreshPending = true;
            var remaining = NodesViewRefreshInterval - elapsed;
            _nodesViewRefreshTimer.Interval = remaining <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : remaining;
            if (!_nodesViewRefreshTimer.IsEnabled)
                _nodesViewRefreshTimer.Start();
            return;
        }

        _lastNodesViewRefreshUtc = now;
        _nodesViewRefreshPending = false;
        if (_nodesViewRefreshTimer.IsEnabled)
            _nodesViewRefreshTimer.Stop();
        view.Refresh();
    }

    private void OnNodesViewRefreshTimerTick(object? sender, EventArgs e)
    {
        _nodesViewRefreshTimer.Stop();
        if (!_nodesViewRefreshPending)
            return;

        _nodesViewRefreshPending = false;
        _lastNodesViewRefreshUtc = DateTime.UtcNow;
        NodesView?.Refresh();
    }

    /// <summary>Temporarily pause node list reloads (for example while the
    /// node context menu is open), then flush one pending reload on resume.</summary>
    public void SetNodeReloadSuspended(bool suspended)
    {
        _suspendNodeReload = suspended;
        if (!suspended && _nodesDirty)
        {
            if (_dirtyNodeNums.Count == 0)
                _nodesDirty = false;
            else
                _nodesDirty = ApplyDirtyNodeUpdates();
        }
    }

    /// <summary>Returns map markers for only the specified node ids.
    /// Used by incremental map updates so large node lists do not require
    /// rebuilding marker data for every node on each change.</summary>
    public IReadOnlyDictionary<uint, MapMarker> GetNodeMapMarkers(IReadOnlyCollection<uint> nodeNums)
    {
        if (nodeNums.Count == 0)
            return new Dictionary<uint, MapMarker>();

        var markers = new Dictionary<uint, MapMarker>(nodeNums.Count);

        foreach (var nodeNum in nodeNums)
        {
            if (!_nodesByNum.TryGetValue(nodeNum, out var n)) continue;
            if (!NodePassesFilter(n)) continue;
            if (n.Latitude is not double lat || n.Longitude is not double lon) continue;

            var label = GetMapNodeLabel(n);
            markers[nodeNum] = new MapMarker(lat, lon, label, GetNodeTooltipCached(n), IsHome: false, NodeNum: nodeNum);
        }

        return markers;
    }

    public string GetLiveNodeTooltip(uint nodeNum)
    {
        if (_nodesByNum.TryGetValue(nodeNum, out var node))
            return BuildNodeTooltip(node);

        var stored = _nodeStore.Get(nodeNum);
        return stored is not null
            ? BuildNodeTooltip(stored)
            : $"!{nodeNum:x8}";
    }

    public string GetLiveWaypointTooltip(long waypointRowId)
    {
        var waypoint = Waypoints.FirstOrDefault(w => w.Id == waypointRowId)
            ?? _waypointStore.All().FirstOrDefault(w => w.Id == waypointRowId);
        return waypoint is not null
            ? BuildWaypointTooltip(waypoint)
            : string.Empty;
    }

    private string GetNodeTooltipCached(MeshRF.Nodes.NodeRecord n)
    {
        int sig = ComputeNodeTooltipSignature(n);
        if (_nodeTooltipSignatures.TryGetValue(n.NodeNum, out int oldSig) &&
            oldSig == sig &&
            _nodeTooltipCache.TryGetValue(n.NodeNum, out var cached))
            return cached;

        var fresh = BuildNodeTooltip(n);
        _nodeTooltipSignatures[n.NodeNum] = sig;
        _nodeTooltipCache[n.NodeNum] = fresh;
        return fresh;
    }

    private static int ComputeNodeTooltipSignature(MeshRF.Nodes.NodeRecord n)
    {
        var h = new HashCode();
        h.Add(n.LongName, StringComparer.Ordinal);
        h.Add(n.ShortName, StringComparer.Ordinal);
        h.Add(n.DisplayId, StringComparer.Ordinal);
        h.Add(n.Role, StringComparer.Ordinal);
        h.Add(n.HwModel, StringComparer.Ordinal);
        h.Add(n.Latitude);
        h.Add(n.Longitude);
        h.Add(n.AltitudeM);
        h.Add(n.RssiDbm);
        h.Add(n.SnrDb);
        h.Add(n.HopsAway);
        h.Add(n.BatteryPct);
        h.Add(n.VoltageV);
        h.Add(n.ChannelUtilPct);
        h.Add(n.AirUtilTxPct);
        h.Add(n.TemperatureC);
        h.Add(n.RelativeHumidityPct);
        h.Add(n.BarometricPressureHpa);
        h.Add(n.LastHeardEpoch);
        return h.ToHashCode();
    }

    public void ReloadWaypoints()
    {
        Waypoints.Clear();
        foreach (var wp in _waypointStore.All())
            Waypoints.Add(wp);
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    // -- Transmit helpers ----------------------------------------------------

    /// <summary>
    /// Mark RX as busy for at least <paramref name="hold"/> from now. Called
    /// from the demod event drain when a preamble/frame is detected.
    /// </summary>
    private void MarkRxBusy(DateTime nowUtc, TimeSpan hold)
    {
        var until = nowUtc + hold;
        lock (_rxBusyLock)
        {
            if (until > _rxBusyUntilUtc)
                _rxBusyUntilUtc = until;
        }
    }

    /// <summary>
    /// Clear RX busy state when a payload line indicates the frame decode ended.
    /// </summary>
    private void MarkRxFrameComplete(DateTime nowUtc)
    {
        lock (_rxBusyLock)
            _rxBusyUntilUtc = nowUtc;
    }

    private bool IsRxBusy(DateTime nowUtc)
    {
        lock (_rxBusyLock)
            return nowUtc < _rxBusyUntilUtc;
    }

    /// <summary>
    /// Wait briefly for the channel to go idle when we are actively receiving.
    /// Bounded so critical responses are delayed, not blocked indefinitely.
    /// </summary>
    private async Task WaitForRxIdleAsync(TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        if (maxWait <= TimeSpan.Zero)
            return;

        var start = DateTime.UtcNow;
        while (true)
        {
            var now = DateTime.UtcNow;
            if (!IsRxBusy(now))
                return;

            var elapsed = now - start;
            if (elapsed >= maxWait)
                return;

            var remainMs = Math.Max(1, (int)(maxWait - elapsed).TotalMilliseconds);
            await Task.Delay(Math.Min(RxBusyPollMs, remainMs), cancellationToken)
                      .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opportunistic CSMA-like defer: wait for RX idle up to a small bound, then
    /// add a short random backoff to reduce synchronized key-ups.
    /// </summary>
    private async Task WaitForTxOpportunityAsync(CancellationToken cancellationToken = default)
    {
        await WaitForRxIdleAsync(RxBusyMaxWait, cancellationToken).ConfigureAwait(false);
        await Task.Delay(Random.Shared.Next(8, 24), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Offloads a blocking <see cref="MeshtasticCore.Transmit"/> call to a
    /// thread-pool thread, serialized through <see cref="_txSemaphore"/> so
    /// concurrent sends never race on the shared native Core handle.
    /// Await this from async <c>[RelayCommand]</c> methods to keep the UI thread
    /// responsive during transmit (stop-RX + USB streaming + restart-RX).
    /// </summary>
    private async Task<bool> TransmitAsync(LoraPreset preset, ulong hz, byte[] frame,
                                           byte gain, bool amp)
    {
        bool showPausedStatus = IsSharedHackRfRxTxActive();
        if (showPausedStatus)
            await SetSharedHackRfTxStatusAsync(active: true).ConfigureAwait(false);

        await _txSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await WaitForTxOpportunityAsync().ConfigureAwait(false);
            bool ok = await Task.Run(() => _core.Transmit(preset, hz, frame, gain, amp))
                                .ConfigureAwait(false);
            if (ok)
                RecordAirtimeSample(EstimatePacketAirtimeMs(preset, frame?.Length ?? 0), isTx: true);
            return ok;
        }
        finally
        {
            _txSemaphore.Release();
            if (showPausedStatus)
                await SetSharedHackRfTxStatusAsync(active: false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fire-and-forget transmit for auto-reply helpers (ACK, NodeInfo reply,
    /// position reply, traceroute reply) that are triggered on packet receipt.
    /// Serialized through <see cref="_txSemaphore"/> like <see cref="TransmitAsync"/>.
    /// Any exception is silently swallowed — auto-replies are best-effort.
    /// </summary>
    private void TransmitBackground(LoraPreset preset, ulong hz, byte[] frame,
                                    byte gain, bool amp)
    {
        _ = Task.Run(async () =>
        {
            await _txSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await WaitForTxOpportunityAsync().ConfigureAwait(false);
                if (_core.Transmit(preset, hz, frame, gain, amp))
                    RecordAirtimeSample(EstimatePacketAirtimeMs(preset, frame?.Length ?? 0), isTx: true);
            }
            catch { /* best-effort */ }
            finally { _txSemaphore.Release(); }
        });
    }

    private bool IsSharedHackRfRxTxActive() =>
        IsRunning &&
        SelectedDevice?.Kind == RadioDeviceKind.HackRf &&
        SelectedTxDevice?.Kind == RadioDeviceKind.HackRf;

    private async Task SetSharedHackRfTxStatusAsync(bool active)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (active)
        {
            if (System.Threading.Interlocked.Increment(ref _sharedHackRfTxStatusDepth) != 1)
                return;

            await dispatcher.InvokeAsync(() =>
            {
                Status = "TX (RX paused)";
            });
            return;
        }

        if (System.Threading.Interlocked.Decrement(ref _sharedHackRfTxStatusDepth) != 0)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            if (Status == "TX (RX paused)")
                Status = IsRunning ? BuildRxStatus() : "Stopped";
        });
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

        var pendingChannelReactions = new List<MessageRecord>();

        // Rebuild channel (broadcast) chat rooms from history.
        foreach (var msg in _messageStore.TextHistory())
        {
            if (!ChatMessagePassesIgnoredFilter(msg.FromNode)) continue;

            bool isDm = msg.ToNode != 0xFFFFFFFFu &&
                        (msg.FromNode == _myNodeNum || msg.ToNode == _myNodeNum);
            if (isDm) continue; // DMs are restored per-conversation below.

            var chanVm = ResolveChannelTab(msg.Channel);
            if (chanVm is null) continue;

            if (IsReactionRecord(msg))
            {
                pendingChannelReactions.Add(msg);
                continue;
            }

            if (IsOrphanReactionRow(msg))
                continue;

            if (IsNonStandaloneReplyRecord(msg))
            {
                chanVm.Messages.Add(BuildReplyLinkedMessage(msg, chanVm.Messages));
                if (chanVm.Messages.Count > 1000) chanVm.Messages.RemoveAt(0);
                continue;
            }

            if (string.IsNullOrEmpty(msg.Text)) continue;
            chanVm.Messages.Add(BuildHistoryMessage(msg));
            if (chanVm.Messages.Count > 1000) chanVm.Messages.RemoveAt(0);
        }

        foreach (var reaction in pendingChannelReactions)
        {
            var chanVm = ResolveChannelTab(reaction.Channel);
            if (chanVm is null) continue;
            if (!TryApplyReaction(chanVm.Messages, reaction.ReplyId, reaction.Text, reaction.Emoji, reaction.FromNode))
            {
                InsertMessageChronologically(chanVm.Messages,
                    BuildStandaloneReactionMessage(reaction));
                if (chanVm.Messages.Count > 1000) chanVm.Messages.RemoveAt(0);
            }
        }

        // Reopen only the DM tabs that were left open last session (not every
        // peer we have history with). Snapshot the saved list first: opening a
        // tab calls SaveSettings, which would otherwise rewrite the list we're
        // iterating (it's a no-op here anyway, since _settingsLoaded is still
        // false during this initial load, but the snapshot keeps it robust).
        if (_myNodeNum != 0)
        {
            var toReopen = (_settings.OpenConversations ?? new List<uint>()).ToList();
            foreach (var peer in toReopen)
            {
                if (peer == 0 || peer == 0xFFFFFFFFu || peer == _myNodeNum) continue;
                if (!ChatMessagePassesIgnoredFilter(peer)) continue;
                OpenConversation(peer, NodeDisplayName(peer), focus: false);
            }
        }

        // Restoring DM tabs moves selection. Return focus to the most recently
        // selected channel when possible.
        int preferredChannel = _settings.LastSelectedChannelIndex >= 0
            ? _settings.LastSelectedChannelIndex
            : _settings.SelectedChannelIndex;
        SelectedTab = Channels.FirstOrDefault(c => c.Config.Index == preferredChannel)
            ?? Channels.FirstOrDefault();
    }

    /// <summary>Load the full persisted history for a peer into a conversation
    /// tab (idempotent: clears first so reopening doesn't duplicate rows).</summary>
    private void LoadConversationHistory(ConversationViewModel convo)
    {
        convo.Messages.Clear();
        if (_myNodeNum == 0) return;

        var pendingReactions = new List<MessageRecord>();
        foreach (var msg in _messageStore.Conversation(convo.NodeNum, _myNodeNum))
        {
            if (!ChatMessagePassesIgnoredFilter(msg.FromNode)) continue;

            if (IsReactionRecord(msg))
            {
                pendingReactions.Add(msg);
                continue;
            }

            if (IsOrphanReactionRow(msg))
                continue;

            if (IsNonStandaloneReplyRecord(msg))
            {
                convo.Add(BuildReplyLinkedMessage(msg, convo.Messages));
                continue;
            }

            if (string.IsNullOrEmpty(msg.Text)) continue;
            convo.Add(BuildHistoryMessage(msg));
        }

        foreach (var reaction in pendingReactions)
        {
            if (!TryApplyReaction(convo.Messages, reaction.ReplyId, reaction.Text, reaction.Emoji, reaction.FromNode))
            {
                InsertMessageChronologically(convo.Messages,
                    BuildStandaloneReactionMessage(reaction));
                if (convo.Messages.Count > 1000) convo.Messages.RemoveAt(0);
            }
        }
    }

    /// <summary>Turn a stored record into a <see cref="ChannelMessage"/>,
    /// restoring our own sends' persisted ACK/NAK delivery status.</summary>
    private ChannelMessage BuildHistoryMessage(MessageRecord msg)
    {
        bool outgoing = _myNodeNum != 0 && msg.FromNode == _myNodeNum;

        // App-generated conversation notes (traceroute results, position-request
        // echoes) store their display tag in the channel column and never carry
        // a delivery status.
        if (msg.PortNum == MessageStore.ConversationNotePort)
        {
            return new ChannelMessage
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(msg.RxEpoch).LocalDateTime,
                FromId = string.IsNullOrEmpty(msg.Channel) ? "info" : msg.Channel,
                Text = msg.Text,
                RssiDbm = msg.RssiDbfs,
                SnrDb = msg.SnrDb,
                PacketId = msg.PacketId,
                IsOutgoing = outgoing,
                IsIgnoredSender = !outgoing && IsNodeIgnored(msg.FromNode),
                Delivery = MessageDelivery.None,
            };
        }

        // Broadcasts (to 0xFFFFFFFF) are never ACKed, so they carry no delivery
        // status — only our own directed sends (DMs) show sent/delivered/failed.
        bool isBroadcast = msg.ToNode == 0xFFFFFFFFu;
        return new ChannelMessage
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(msg.RxEpoch).LocalDateTime,
            FromId = NodeDisplayName(msg.FromNode),
            SenderNodeNum = msg.FromNode,
            Text = msg.Text,
            RssiDbm = msg.RssiDbfs,
            SnrDb = msg.SnrDb,
            PacketId = msg.PacketId,
            IsOutgoing = outgoing,
            IsIgnoredSender = !outgoing && IsNodeIgnored(msg.FromNode),
            Delivery = outgoing && !isBroadcast
                ? (MessageDelivery)msg.Delivery
                : MessageDelivery.None,
        };
    }

    private static bool IsReactionRecord(MessageRecord msg) =>
        msg.IsReaction ||
        (msg.PortNum == (int)PortNum.TextMessage
         && msg.ReplyId != 0
         && msg.Emoji != 0);

    private static bool IsOrphanReactionRow(MessageRecord msg) =>
        msg.PortNum == (int)PortNum.TextMessage
        && msg.ReplyId == 0
        && msg.Emoji != 0;

    private static bool IsNonStandaloneReplyRecord(MessageRecord msg) =>
        msg.PortNum == (int)PortNum.TextMessage
        && msg.ReplyId != 0
        && msg.Emoji == 0;

    private bool TryApplyReaction(IList<ChannelMessage> messages, uint replyId,
                                  string? reactionText, uint emoji, uint fromNode)
    {
        if (replyId == 0 || emoji == 0 || messages.Count == 0) return false;
        var glyph = ResolveReactionGlyph(reactionText, emoji);
        if (glyph.Length == 0) return false;

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.PacketId != replyId) continue;
            msg.AddReaction(glyph, NodeDisplayName(fromNode));
            return true;
        }

        return false;
    }

    private static string ResolveReactionGlyph(string? reactionText, uint emoji)
    {
        var text = (reactionText ?? string.Empty).Trim();
        if (text.Length > 0) return text;
        return CodePointToEmoji(emoji);
    }

    private ChannelMessage BuildStandaloneReactionMessage(MessageRecord reaction)
    {
        bool outgoing = _myNodeNum != 0 && reaction.FromNode == _myNodeNum;
        bool isBroadcast = reaction.ToNode == 0xFFFFFFFFu;
        var glyph = ResolveReactionGlyph(reaction.Text, reaction.Emoji);
        if (glyph.Length == 0) glyph = "(reaction)";
        var targetText = reaction.ReplyId != 0
            ? $"{reaction.ReplyId:x8}"
            : "unknown";

        return new ChannelMessage
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(reaction.RxEpoch).LocalDateTime,
            FromId = NodeDisplayName(reaction.FromNode),
            SenderNodeNum = reaction.FromNode,
            Text = $"reacted {glyph} (original message {targetText} not found)",
            RssiDbm = reaction.RssiDbfs,
            SnrDb = reaction.SnrDb,
            PacketId = reaction.PacketId,
            IsOutgoing = outgoing,
            IsIgnoredSender = !outgoing && IsNodeIgnored(reaction.FromNode),
            Delivery = outgoing && !isBroadcast
                ? (MessageDelivery)reaction.Delivery
                : MessageDelivery.None,
        };
    }

    private ChannelMessage BuildReplyLinkedMessage(
        MessageRecord reply,
        IList<ChannelMessage> messages)
    {
        bool outgoing = _myNodeNum != 0 && reply.FromNode == _myNodeNum;
        bool isBroadcast = reply.ToNode == 0xFFFFFFFFu;
        var body = string.IsNullOrWhiteSpace(reply.Text)
            ? "(empty reply)"
            : reply.Text;

        ChannelMessage? target = null;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var candidate = messages[i];
            if (candidate.PacketId != reply.ReplyId) continue;
            target = candidate;
            break;
        }

        string context = target is not null
            ? BuildReplyContextText(target)
            : $"replying to {reply.ReplyId:x8} (original message not found)";

        return new ChannelMessage
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(reply.RxEpoch).LocalDateTime,
            FromId = NodeDisplayName(reply.FromNode),
            SenderNodeNum = reply.FromNode,
            Text = $"{context}\n{body}",
            RssiDbm = reply.RssiDbfs,
            SnrDb = reply.SnrDb,
            PacketId = reply.PacketId,
            IsOutgoing = outgoing,
            IsIgnoredSender = !outgoing && IsNodeIgnored(reply.FromNode),
            IsReplyLinked = true,
            ReplyTargetFound = target is not null,
            ReplyToPacketId = reply.ReplyId,
            Delivery = outgoing && !isBroadcast
                ? (MessageDelivery)reply.Delivery
                : MessageDelivery.None,
        };
    }

    private static string BuildReplyContextText(ChannelMessage message)
    {
        var from = string.IsNullOrWhiteSpace(message.FromId)
            ? "unknown"
            : message.FromId.Trim();
        var original = TrimForReplyPreview(ExtractReplyLeafText(message.Text));
        return $"replying to {from}: \"{original}\"";
    }

    private static string ExtractReplyLeafText(string? text)
    {
        var raw = text ?? string.Empty;
        if (raw.Length == 0) return string.Empty;

        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return string.Empty;

        // Replies are rendered as "replying to ..." + newline + body. When
        // replying to a reply, only carry forward the latest body line.
        return lines[^1].Trim();
    }

    private static string TrimForReplyPreview(string? text)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        if (normalized.Length == 0) return "(empty)";
        return normalized.Length <= 80 ? normalized : normalized[..80] + "...";
    }

    private string BuildOutgoingReplyDisplayText(string body, uint replyId)
    {
        var context = PendingReplyContext.Length > 0
            ? PendingReplyContext
            : $"replying to {replyId:x8} (original message not found)";
        return $"{context}\n{body}";
    }

    private void ClearPendingReplyState()
    {
        PendingReplyPacketId = 0;
        PendingReplyContext = string.Empty;
    }

    private static void InsertMessageChronologically(
        IList<ChannelMessage> messages,
        ChannelMessage message)
    {
        // History replay is chronological. Standalone reactions are resolved in
        // a second pass; insert by timestamp so they keep the original ordering
        // instead of bunching at the end.
        int index = messages.Count;
        while (index > 0)
        {
            if (messages[index - 1].Timestamp <= message.Timestamp) break;
            index--;
        }

        messages.Insert(index, message);
    }

    private static uint ResolveReactionTargetId(MeshDecodeResult result)
    {
        if (result.ReplyId != 0) return result.ReplyId;
        // Some firmware paths reuse request_id for reply-linked packets.
        if (result.RequestId != 0)
            return result.RequestId;
        return 0;
    }

    private static string CodePointToEmoji(uint codePoint)
    {
        if (codePoint is 0 or > 0x10FFFFu) return string.Empty;
        try { return char.ConvertFromUtf32((int)codePoint); }
        catch { return string.Empty; }
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
        // Primary always first, then secondaries sorted by index
        var sorted = existing
            .OrderByDescending(c => c.Role == ChannelRole.Primary)
            .ThenBy(c => c.Index);
        foreach (var c in sorted)
            Channels.Add(new ChannelViewModel(c, OnChannelSaved,
                IsChannelRtttlMuted(c.Index), OnChannelRtttlMuteChanged, CurrentUnitSystem));
        SyncPrimaryChannelName();
        RebuildTabs();
        SelectedTab = Channels.FirstOrDefault();
    }

    private bool IsChannelRtttlMuted(int channelIndex) =>
        _settings.MutedRingtoneChannels.Contains(channelIndex);

    private void OnChannelRtttlMuteChanged(ChannelViewModel channel, bool muted)
    {
        int index = channel.Config.Index;
        if (muted)
        {
            if (!_settings.MutedRingtoneChannels.Contains(index))
                _settings.MutedRingtoneChannels.Add(index);
        }
        else
        {
            _settings.MutedRingtoneChannels.Remove(index);
        }
        SaveSettings();
    }

    public void SetNodeRtttlMuted(NodeRecord node, bool muted)
    {
        node.MuteRtttl = muted;
        _nodeStore.SetMuteRtttl(node.NodeNum, muted);
    }

    public void SetNodesRtttlMuted(IEnumerable<NodeRecord> nodes, bool muted)
    {
        foreach (var node in nodes)
            SetNodeRtttlMuted(node, muted);
    }

    private bool IsNodeRtttlMuted(uint nodeNum) =>
        Nodes.FirstOrDefault(n => n.NodeNum == nodeNum)?.MuteRtttl == true;

    public void SetNodeIgnored(NodeRecord node, bool ignored)
    {
        node.Ignored = ignored;
        _nodeStore.SetIgnored(node.NodeNum, ignored);
        MarkNodeDirty(node.NodeNum);
        if (!_suspendNodeReload)
            _nodesDirty = ApplyDirtyNodeUpdates();
        LoadChatHistory();
    }

    public void SetNodesIgnored(IEnumerable<NodeRecord> nodes, bool ignored)
    {
        foreach (var node in nodes)
        {
            node.Ignored = ignored;
            _nodeStore.SetIgnored(node.NodeNum, ignored);
            MarkNodeDirty(node.NodeNum);
        }
        if (!_suspendNodeReload)
            _nodesDirty = ApplyDirtyNodeUpdates();
        LoadChatHistory();
    }

    public void SetNodesFavorite(IEnumerable<NodeRecord> nodes, bool favorite)
    {
        foreach (var node in nodes)
        {
            node.Favorite = favorite;
            _nodeStore.SetFavorite(node.NodeNum, favorite);
            MarkNodeDirty(node.NodeNum);
        }
        if (!_suspendNodeReload)
            _nodesDirty = ApplyDirtyNodeUpdates();
    }

    private bool IsNodeIgnored(uint nodeNum) =>
        Nodes.FirstOrDefault(n => n.NodeNum == nodeNum)?.Ignored == true;

    private bool ChatMessagePassesIgnoredFilter(uint fromNode)
    {
        bool ignored = IsNodeIgnored(fromNode);
        return NodeIgnoredFilter switch
        {
            "Hide ignored" => !ignored,
            "Only ignored" => ignored,
            _ => true,
        };
    }

    private void OnConversationMuteRtttlChanged(ConversationViewModel convo, bool muted)
    {
        var node = Nodes.FirstOrDefault(n => n.NodeNum == convo.NodeNum);
        if (node is not null)
            SetNodeRtttlMuted(node, muted);
    }

    private void OnConversationLocationHistoryChanged(ConversationViewModel convo)
    {
        _nodeLocationHistoryCounts[convo.NodeNum] = convo.LocationHistory.Count;
        if (NodeLocationFilter == "Has position history (>1)")
            RefreshNodesFilter();
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
        int idx = 1;
        while (taken.Contains(idx)) idx++;
        var cfg = new ChannelConfig
        {
            Index = idx,
            Name = $"Channel {idx}",
            Psk = ChannelConfig.NewRandomPsk(),
            Role = ChannelRole.Secondary,
            PositionPrecision = 0,
        };
        _channelStore.Upsert(cfg);
        ReloadChannels();
        LoadChatHistory();
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
        LoadChatHistory();
    }

    [RelayCommand]
    private void MoveChannelUp()
    {
        var ch = SelectedChannel;
        if (ch is null) return;
        if (ch.Config.Role == ChannelRole.Primary) return; // can't move primary
        // Get secondaries only, sorted by index
        var secondaries = Channels
            .Where(c => c.Config.Role != ChannelRole.Primary)
            .OrderBy(c => c.Config.Index)
            .ToList();
        int pos = secondaries.FindIndex(c => c.Config.Index == ch.Config.Index);
        if (pos <= 0) return; // already first secondary
        var prev = secondaries[pos - 1];
        SwapChannelIndices(ch.Config, prev.Config);
    }

    [RelayCommand]
    private void MoveChannelDown()
    {
        var ch = SelectedChannel;
        if (ch is null) return;
        if (ch.Config.Role == ChannelRole.Primary) return; // can't move primary
        // Get secondaries only, sorted by index
        var secondaries = Channels
            .Where(c => c.Config.Role != ChannelRole.Primary)
            .OrderBy(c => c.Config.Index)
            .ToList();
        int pos = secondaries.FindIndex(c => c.Config.Index == ch.Config.Index);
        if (pos < 0 || pos >= secondaries.Count - 1) return; // already last
        var next = secondaries[pos + 1];
        SwapChannelIndices(ch.Config, next.Config);
    }

    private void SwapChannelIndices(ChannelConfig a, ChannelConfig b)
    {
        int idxA = a.Index;
        int idxB = b.Index;
        // Delete both, then re-insert with swapped indices
        _channelStore.Delete(idxA);
        _channelStore.Delete(idxB);
        a.Index = idxB;
        b.Index = idxA;
        _channelStore.Upsert(a);
        _channelStore.Upsert(b);
        ReloadChannels();
        LoadChatHistory();
        SelectedTab = Channels.FirstOrDefault(c => c.Config.Index == idxB);
    }

    /// <summary>
    /// Append a timestamped line to the global running log. The log is shared
    /// across all tabs (channels and direct messages), not channel-specific.
    /// </summary>
    private void Log(string text)
    {
        var line = $"[{DateTime.Now.ToString(UiDateTimeFormat, CultureInfo.CurrentCulture)}] {text}";
        RunOnUiThread(() =>
        {
            LogLines.Add(line);
            if (LogLines.Count > 500) LogLines.RemoveAt(0);
        });
    }

    private void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }

    private static bool ShouldLogDeviceStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        return !status.StartsWith("HackRF open OK", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copy the entire global log to the clipboard.</summary>
    [RelayCommand]
    private void CopyLog()
    {
        RunOnUiThread(() =>
        {
            if (LogLines.Count == 0) return;
            try
            {
                string text = string.Join(Environment.NewLine, LogLines.ToArray());
                System.Windows.Clipboard.SetText(text);
            }
            catch { /* clipboard contention; ignore */ }
        });
    }

    /// <summary>Clear the global log.</summary>
    [RelayCommand]
    private void ClearLog() => RunOnUiThread(LogLines.Clear);

    partial void OnSelectedPresetChanged(LoraPreset value)
    {
        // Autofill SF/BW/CR from the new preset (unless the user is in the middle
        // of typing — but overwriting is the right UX here: preset is the anchor).
        ApplyPresetToLoraParams(value);
        RebuildSlots(snapToDefault: true);
        RetuneIfRunning();
        SyncPrimaryChannelName();
        SaveSettings();
    }
    partial void OnOverrideSfChanged(byte value)    { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); RetuneIfRunning(); SaveSettings(); } }
    partial void OnOverrideBwKhzChanged(double value) { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); RetuneIfRunning(); SaveSettings(); } }
    partial void OnOverrideCrChanged(byte value)    { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); RetuneIfRunning(); SaveSettings(); } }
    partial void OnSelectedRegionChanged(Region value)     { RebuildSlots(snapToDefault: true); RetuneIfRunning(); SaveSettings(); }
    partial void OnSelectedSlotChanged(int value)
    {
        if (_suppressSlotSync || value <= 0) return;
        CenterFreqMHz = ChannelPlan.FrequencyMHz(SelectedRegion, SelectedPreset, value);
        SaveSettings();
    }
    partial void OnCenterFreqMHzChanged(double value) { if (!_suppressRetune) RetuneIfRunning(); SaveSettings(); SpectrumCenterHz = CenterFreqMHz * 1_000_000.0; }
    partial void OnLnaGainDbChanged(byte value) { _core.SetGains(value, VgaGainDb, AmpEnable); SaveSettings(); }
    partial void OnVgaGainDbChanged(byte value) { _core.SetGains(LnaGainDb, value, AmpEnable); SaveSettings(); }
    partial void OnAmpEnableChanged(bool value) { PushGains(); SaveSettings(); }
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectDevice));
        OnPropertyChanged(nameof(CanSelectRxSampleRate));
    }
    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        OnPropertyChanged(nameof(IsRtlSdr));
        OnPropertyChanged(nameof(IsHackRf));
        OnPropertyChanged(nameof(CanSelectRxSampleRate));
        if (value is not null)
            RefreshSampleRateSelection(value.Kind, GetSavedRxSampleRateHz(value.Kind));
        if (_suppressDeviceUpdate || value is null) return;
        ApplyRxDevice(value.Kind);
    }
    partial void OnSelectedTxDeviceChanged(DeviceOption? value)
    {
        OnPropertyChanged(nameof(IsTxHackRf));
        if (_suppressDeviceUpdate || value is null) return;
        ApplyTxDevice(value.Kind);
    }
    private uint GetSavedRxSampleRateHz(RadioDeviceKind kind) => kind switch
    {
        RadioDeviceKind.HackRf => _settings.HackRfRxSampleRateHz != 2_400_000u || _settings.RxSampleRateHz == 2_400_000u
            ? _settings.HackRfRxSampleRateHz
            : _settings.RxSampleRateHz,
        RadioDeviceKind.RtlSdr => _settings.RtlSdrRxSampleRateHz != 2_400_000u || _settings.RxSampleRateHz == 2_400_000u
            ? _settings.RtlSdrRxSampleRateHz
            : _settings.RxSampleRateHz,
        _ => _settings.RxSampleRateHz,
    };

    private void StoreSavedRxSampleRateHz(RadioDeviceKind kind, uint hz)
    {
        switch (kind)
        {
            case RadioDeviceKind.HackRf:
                _settings.HackRfRxSampleRateHz = hz;
                break;
            case RadioDeviceKind.RtlSdr:
                _settings.RtlSdrRxSampleRateHz = hz;
                break;
        }
    }

    private RadioDeviceKind CurrentRxDeviceKind => SelectedDevice?.Kind ?? RadioDeviceKind.Null;

    partial void OnSelectedRxSampleRateChanged(SampleRateOption? value)
    {
        if (_suppressSampleRateUpdate || value is null) return;
        _core.SetDeviceOption("rx_sample_rate_hz", checked((int)value.Hz));
        StoreSavedRxSampleRateHz(CurrentRxDeviceKind, value.Hz);
        SaveSettings();
    }
    partial void OnAgcEnableChanged(bool value) { SaveSettings(); }
    partial void OnAgcTargetDbfsChanged(double value) { SaveSettings(); }
    partial void OnRtlGainDbChanged(byte value) { PushGains(); SaveSettings(); }
    partial void OnRtlAgcEnableChanged(bool value) { PushGains(); SaveSettings(); }
    partial void OnTxGainDbChanged(byte value) { SaveSettings(); }
    partial void OnTxAmpEnableChanged(bool value) { SaveSettings(); }
    partial void OnBiasTeeChanged(bool value) { _core.SetDeviceOption("bias_tee", value ? 1 : 0); SaveSettings(); }
    partial void OnDcBlockEnableChanged(bool value) { _core.SetDcBlock(value); SaveSettings(); }

    /// <summary>Push the gain settings appropriate for the selected backend.
    /// RTL-SDR uses its single manual tuner gain (or auto when AGC is on);
    /// HackRF and friends use the LNA/VGA/AMP model.</summary>
    private void PushGains()
    {
        if (IsRtlSdr)
            _core.SetGains(RtlGainDb, 0, RtlAgcEnable);
        else
            _core.SetGains(LnaGainDb, VgaGainDb, AmpEnable);
    }
    partial void OnThemeChanged(string value) { ThemeManager.Apply(value); SaveSettings(); }
    partial void OnUnitSystemNameChanged(string value)
    {
        SaveSettings();
        OnPropertyChanged(nameof(CurrentUnitSystem));
        OnPropertyChanged(nameof(UseImperial));
        OnPropertyChanged(nameof(UseFahrenheit));
        OnPropertyChanged(nameof(UseMiles));
        OnPropertyChanged(nameof(DistanceUnitShort));
        OnPropertyChanged(nameof(DistanceUnitLong));
        OnPropertyChanged(nameof(MaxDistanceFilterToolTip));
        OnPropertyChanged(nameof(HomeLocationLabel));
        OnPropertyChanged(nameof(HomeAltitudeToolTip));
        if (!_settingsLoaded) return;

        _suppressHomeTextUpdate = true;
        HomeAltitudeText = DisplayUnits.FormatAltitudeInput(_manualHomeAltitude, CurrentUnitSystem);
        _suppressHomeTextUpdate = false;

        _nodeTooltipSignatures.Clear();
        _nodeTooltipCache.Clear();
        foreach (var channel in Channels)
            channel.UpdatePositionPrecisionOptions(CurrentUnitSystem);
        foreach (var convo in Tabs.OfType<ConversationViewModel>())
            convo.RefreshTelemetryFormatting();
        MapDataChanged?.Invoke(this, EventArgs.Empty);
        RefreshNodesFilter();
    }
    partial void OnWaterfallColormapChanged(string value) { SaveSettings(); }
    partial void OnWaterfallAutoLevelsChanged(bool value) { SaveSettings(); }
    partial void OnWaterfallFloorDbChanged(double value) { SaveSettings(); }
    partial void OnWaterfallCeilDbChanged(double value) { SaveSettings(); }
    partial void OnWaterfallRowsPerSecondChanged(double value) { SaveSettings(); }

    private void SaveSettings()
    {
        if (!_settingsLoaded) return;
        _settings.Region = SelectedRegion.ToString();
        _settings.Preset = SelectedPreset.ToString();
        _settings.Slot = SelectedSlot;
        _settings.CenterFreqMHz = CenterFreqMHz;
        _settings.OverrideSf    = OverrideSf;
        _settings.OverrideBwHz  = (uint)Math.Round(OverrideBwKhz * 1000.0);
        _settings.OverrideCr    = OverrideCr;
        _settings.LnaGainDb = LnaGainDb;
        _settings.VgaGainDb = VgaGainDb;
        _settings.AmpEnable = AmpEnable;
        _settings.DeviceKind = SelectedDevice?.Kind.ToString() ?? "Auto";
        _settings.RxDeviceKind = SelectedDevice?.Kind.ToString() ?? "Auto";
        var selectedRxKind = CurrentRxDeviceKind;
        var selectedRxSampleRateHz = SelectedRxSampleRate?.Hz ?? GetSavedRxSampleRateHz(selectedRxKind);
        _settings.RxSampleRateHz = selectedRxSampleRateHz;
        StoreSavedRxSampleRateHz(selectedRxKind, selectedRxSampleRateHz);
        _settings.TxDeviceKind = SelectedTxDevice?.Kind.ToString() ?? "HackRf";
        _settings.TxGainDb = TxGainDb;
        _settings.TxAmpEnable = TxAmpEnable;
        _settings.AgcEnable = AgcEnable;
        _settings.AgcTargetDbfs = AgcTargetDbfs;
        _settings.RtlGainDb = RtlGainDb;
        _settings.RtlAgcEnable = RtlAgcEnable;
        _settings.BiasTee = BiasTee;
        _settings.DcBlockEnable = DcBlockEnable;
        _settings.Theme = Theme;
        _settings.UnitSystem = UnitSystemName;
        _settings.UseFahrenheit = UseImperial;
        _settings.UseMiles = UseImperial;
        _settings.WaterfallColormap = WaterfallColormap;
        _settings.MutedRingtoneChannels = Channels
            .Where(c => c.MuteRtttl)
            .Select(c => c.Config.Index)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
        _settings.RingtoneMode = RingtoneMode;
        _settings.RingtoneVolume = (int)Math.Round(RingtoneVolume);
        _settings.RingtoneRtttl = RingtoneRtttl;
        _settings.WaterfallAutoLevels = WaterfallAutoLevels;
        _settings.WaterfallFloorDb = WaterfallFloorDb;
        _settings.WaterfallCeilDb = WaterfallCeilDb;
        _settings.WaterfallRowsPerSecond = WaterfallRowsPerSecond;
        _settings.UserNodeNum = _myNodeNum;
        _settings.UserLongName = MyLongName ?? string.Empty;
        _settings.UserShortName = MyShortName ?? string.Empty;
        _settings.UserRole = MyRole ?? "Client";
        _settings.UserHwModel = MyHwModel ?? "UNSET";
        _settings.RebroadcastMode = RebroadcastMode ?? "ALL";
        _settings.HopLimit = Math.Clamp(HopLimit, 1, 7);
        _settings.OkToMqtt = OkToMqtt;
        _settings.RoutingRelayEnabled = RoutingRelayEnabled;
        _settings.AutoReportNodeInfoEnabled = AutoReportNodeInfoEnabled;
        _settings.AutoReportNodeInfoSeconds = Math.Max(5, AutoReportNodeInfoSeconds);
        _settings.AutoReportPositionEnabled = AutoReportPositionEnabled;
        _settings.AutoReportPositionSeconds = Math.Max(5, AutoReportPositionSeconds);
        _settings.AutoReportDeviceMetricsEnabled = AutoReportDeviceMetricsEnabled;
        _settings.AutoReportDeviceMetricsSeconds = Math.Max(5, AutoReportDeviceMetricsSeconds);
        _settings.UserPublicKey = MyPublicKey ?? string.Empty;
        _settings.UserPrivateKey = MyPrivateKey ?? string.Empty;
        _settings.NodeFilterMqtt = NodeMqttFilter;
        _settings.HomeLocationSource = SelectedLocationSource?.Value ?? ManualLocationSourceValue;
        _settings.HomeLatitude  = _manualHomeLatitude;
        _settings.HomeLongitude = _manualHomeLongitude;
        _settings.HomeAltitude  = _manualHomeAltitude;
        _settings.GpsSerialPort = GpsPortName?.Trim() ?? string.Empty;
        _settings.GpsBaudRate = ParseGpsBaudRateOrNull() ?? 0;
        _settings.OpenConversations = Tabs.OfType<ConversationViewModel>()
                                          .Select(c => c.NodeNum)
                                          .ToList();
        _settings.LastSelectedChannelIndex = _lastSelectedChannelIndex;
        _settings.Save();
    }

    // -- Identity change handlers -------------------------------------------

    partial void OnMyNodeIdTextChanged(string value)
    {
        _myNodeNum = ParseNodeId(value);
        OnPropertyChanged(nameof(MyMacAddress));
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
        SendDeviceMetricsCommand.NotifyCanExecuteChanged();
        SaveSettings();
        RefreshSelfNode();
    }

    partial void OnMyLongNameChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnMyShortNameChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnMyRoleChanged(string value) => SaveSettings();
    partial void OnMyHwModelChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnRebroadcastModeChanged(string value) => SaveSettings();
    partial void OnOkToMqttChanged(bool value) => SaveSettings();
    partial void OnRoutingRelayEnabledChanged(bool value) => SaveSettings();

    partial void OnAutoReportNodeInfoEnabledChanged(bool value)
    {
        _lastAutoNodeInfoUtc = DateTime.MinValue;
        _nextAutoNodeInfoUtc = value
            ? DateTime.UtcNow.AddSeconds(Math.Max(5, AutoReportNodeInfoSeconds))
            : DateTime.MinValue;
        UpdateAutoReportLastSentSummary();
        SaveSettings();
    }

    partial void OnAutoReportPositionEnabledChanged(bool value)
    {
        _lastAutoPositionUtc = DateTime.MinValue;
        _nextAutoPositionUtc = value
            ? DateTime.UtcNow.AddSeconds(Math.Max(5, AutoReportPositionSeconds))
            : DateTime.MinValue;
        UpdateAutoReportLastSentSummary();
        SaveSettings();
    }

    partial void OnAutoReportDeviceMetricsEnabledChanged(bool value)
    {
        _lastAutoDeviceMetricsUtc = DateTime.MinValue;
        _nextAutoDeviceMetricsUtc = value
            ? DateTime.UtcNow.AddSeconds(Math.Max(5, AutoReportDeviceMetricsSeconds))
            : DateTime.MinValue;
        UpdateAutoReportLastSentSummary();
        SaveSettings();
    }

    partial void OnAutoReportNodeInfoSecondsChanged(int value)
    {
        if (value < 5) { AutoReportNodeInfoSeconds = 5; return; }
        if (AutoReportNodeInfoEnabled)
            _nextAutoNodeInfoUtc = DateTime.UtcNow.AddSeconds(Math.Max(5, AutoReportNodeInfoSeconds));
        SaveSettings();
    }

    partial void OnAutoReportPositionSecondsChanged(int value)
    {
        if (value < 5) { AutoReportPositionSeconds = 5; return; }
        if (AutoReportPositionEnabled)
            _nextAutoPositionUtc = DateTime.UtcNow.AddSeconds(Math.Max(5, AutoReportPositionSeconds));
        SaveSettings();
    }

    partial void OnAutoReportDeviceMetricsSecondsChanged(int value)
    {
        if (value < 5) { AutoReportDeviceMetricsSeconds = 5; return; }
        if (AutoReportDeviceMetricsEnabled)
            _nextAutoDeviceMetricsUtc = DateTime.UtcNow.AddSeconds(Math.Max(5, AutoReportDeviceMetricsSeconds));
        SaveSettings();
    }

    partial void OnHopLimitChanged(int value)
    {
        // Keep within the firmware-valid 1..7 range; re-clamping triggers this
        // handler again only when the value actually changes.
        var clamped = Math.Clamp(value, 1, 7);
        if (clamped != value) { HopLimit = clamped; return; }
        SaveSettings();
    }

    [RelayCommand]
    private void GenerateNodeIdFromKey()
    {
        if (!PkiNodeNumber.TryFromPublicKey(TryParseKeyBase64(MyPublicKey), out var nodeNum))
            return;

        MyNodeIdText = $"!{nodeNum:x8}";
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
        RefreshMyPrivateKeyCache();
        SaveSettings();
    }

    private void RefreshMyPrivateKeyCache()
    {
        var parsed = TryParseKeyBase64(MyPrivateKey);
        _myPrivateKeyBytes = parsed.Length == 32 ? parsed : Array.Empty<byte>();
    }

    partial void OnHomeLatitudeTextChanged(string value)
    {
        if (!_suppressHomeTextUpdate) UpdateHomeLocation();
    }

    partial void OnHomeLongitudeTextChanged(string value)
    {
        if (!_suppressHomeTextUpdate) UpdateHomeLocation();
    }

    partial void OnHomeAltitudeTextChanged(string value)
    {
        if (!_suppressHomeTextUpdate) UpdateHomeLocation();
    }

    partial void OnSelectedLocationSourceChanged(LocationSourceOption? value)
    {
        ApplyLocationSourceSelection(startOrStopGps: true, saveSettings: _settingsLoaded);
    }

    partial void OnGpsPortNameChanged(string value)
    {
        HandleGpsConfigChanged();
    }

    partial void OnGpsBaudRateTextChanged(string value)
    {
        HandleGpsConfigChanged();
    }

    /// <summary>Re-parse the home lat/lon/alt text boxes, persist, and notify the map.
    /// Each field is parsed independently so an empty/partial value in one
    /// box can never clobber the others (e.g. while typing a negative longitude).</summary>
    private void UpdateHomeLocation()
    {
        // Empty box clears that coordinate; a non-empty box only updates when it
        // parses to a valid in-range value (ignores partial input like "-").
        _manualHomeLatitude = string.IsNullOrWhiteSpace(HomeLatitudeText)
            ? null : (TryParseCoord(HomeLatitudeText, -90, 90) ?? _manualHomeLatitude);
        _manualHomeLongitude = string.IsNullOrWhiteSpace(HomeLongitudeText)
            ? null : (TryParseCoord(HomeLongitudeText, -180, 180) ?? _manualHomeLongitude);
        _manualHomeAltitude = string.IsNullOrWhiteSpace(HomeAltitudeText)
            ? null : (DisplayUnits.ParseAltitudeInput(HomeAltitudeText, CurrentUnitSystem) ?? _manualHomeAltitude);
        HomeAltitude = _manualHomeAltitude;
        if (IsManualLocationSource)
            ApplyResolvedHomeLocation(_manualHomeLatitude, _manualHomeLongitude);
        SaveSettings();
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

        _manualHomeLatitude = lat;
        _manualHomeLongitude = lon;
        if (IsManualLocationSource)
            ApplyResolvedHomeLocation(lat, lon);
        SaveSettings();
    }

    private bool _suppressHomeTextUpdate;

    private void ApplyLocationSourceSelection(bool startOrStopGps, bool saveSettings)
    {
        OnPropertyChanged(nameof(IsManualLocationSource));
        OnPropertyChanged(nameof(IsUsbSerialLocationSource));
        var gpsOptions = BuildGpsSerialOptions();
        _gpsService.UpdateOptions(gpsOptions);

        if (IsUsbSerialLocationSource)
        {
            if (startOrStopGps)
                _gpsService.Restart();
            GpsStatus = BuildGpsWaitingStatus(gpsOptions);
            ApplyResolvedHomeLocation(null, null);
        }
        else
        {
            if (startOrStopGps)
                _gpsService.Stop();
            GpsStatus = "Manual location selected.";
            ApplyResolvedHomeLocation(_manualHomeLatitude, _manualHomeLongitude);
        }

        if (saveSettings)
            SaveSettings();
    }

    private void HandleGpsConfigChanged()
    {
        if (!_settingsLoaded)
            return;

        var options = BuildGpsSerialOptions();
        _gpsService.UpdateOptions(options);
        if (IsUsbSerialLocationSource)
        {
            _gpsService.Restart();
            GpsStatus = BuildGpsWaitingStatus(options);
            ApplyResolvedHomeLocation(null, null);
        }
        else if (ParseGpsBaudRateOrNull() is null && !string.IsNullOrWhiteSpace(GpsBaudRateText))
        {
            GpsStatus = "USB GPS: baud must be a positive integer; using auto when blank.";
        }
        SaveSettings();
    }

    private void ApplyResolvedHomeLocation(double? latitude, double? longitude)
    {
        bool hadHome = HomeLatitude.HasValue && HomeLongitude.HasValue;
        bool hasHome = latitude.HasValue && longitude.HasValue;
        bool mapChanged = !AreHomeCoordsEquivalentForMap(HomeLatitude, HomeLongitude, latitude, longitude);

        HomeLatitude = latitude;
        HomeLongitude = longitude;

        if (hadHome != hasHome)
        {
            OnPropertyChanged(nameof(HasHomeLocation));
            SendPositionCommand.NotifyCanExecuteChanged();
        }

        if (!mapChanged) return;
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool AreHomeCoordsEquivalentForMap(
        double? oldLat,
        double? oldLon,
        double? newLat,
        double? newLon)
    {
        if (oldLat is null || oldLon is null || newLat is null || newLon is null)
            return oldLat == newLat && oldLon == newLon;

        return HaversineKm(oldLat.Value, oldLon.Value, newLat.Value, newLon.Value)
            <= HomeMapUpdateThresholdKm;
    }

    private void HandleGpsStatusChanged(string status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ApplyGpsStatus(status);
            return;
        }

        _ = dispatcher.InvokeAsync(() => ApplyGpsStatus(status));
    }

    private void ApplyGpsStatus(string status)
    {
        if (!IsUsbSerialLocationSource) return;
        GpsStatus = status;
    }

    private void HandleGpsFixReceived(GpsFix fix)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ApplyGpsFix(fix);
            return;
        }

        _ = dispatcher.InvokeAsync(() => ApplyGpsFix(fix));
    }

    private void ApplyGpsFix(GpsFix fix)
    {
        var altStr = fix.AltitudeM is int a ? $"  alt {FormatAltitude(a)}" : string.Empty;
        GpsStatus = $"USB GPS: {fix.PortName} @ {fix.BaudRate} baud  {fix.Latitude:F6}, {fix.Longitude:F6}{altStr}";
        if (!IsUsbSerialLocationSource) return;
        if (fix.AltitudeM is int alt)
            HomeAltitude = alt;
        ApplyResolvedHomeLocation(fix.Latitude, fix.Longitude);
    }

    private GpsSerialOptions BuildGpsSerialOptions() => new(
        string.IsNullOrWhiteSpace(GpsPortName) ? null : GpsPortName.Trim(),
        ParseGpsBaudRateOrNull());

    private int? ParseGpsBaudRateOrNull()
    {
        if (string.IsNullOrWhiteSpace(GpsBaudRateText))
            return null;
        return int.TryParse(GpsBaudRateText.Trim(), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out var baudRate) && baudRate > 0
            ? baudRate
            : null;
    }

    private static string BuildGpsWaitingStatus(GpsSerialOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PortName) && options.BaudRate is int baudRate)
            return $"USB GPS: waiting for NMEA on {options.PortName} @ {baudRate} baud...";
        if (!string.IsNullOrWhiteSpace(options.PortName))
            return $"USB GPS: waiting for NMEA on {options.PortName} at common GPS baud rates...";
        if (options.BaudRate is int forcedBaud)
            return $"USB GPS: scanning serial ports at {forcedBaud} baud...";
        return "USB GPS: scanning serial ports...";
    }

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

        var convo = new ConversationViewModel(nodeNum, name ?? NodeDisplayName(nodeNum),
            OnConversationMuteRtttlChanged,
            OnConversationLocationHistoryChanged,
            FormatTemperature,
            FormatPressure,
            FormatAltitude,
            _nodeStore);
        convo.LoadNodeHistories();
        convo.Node = Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
        // Add the tab immediately so the UI is responsive while history loads.
        Tabs.Add(convo);
        if (focus) SelectedTab = convo;
        // Remember that this tab is open so it (and only it) reopens next launch.
        SaveSettings();
        // Restore persisted DM history (including per-message reactions) using
        // the same replay path used during startup.
        LoadConversationHistory(convo);
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
            {
                var preferredChannel = Channels.FirstOrDefault(c =>
                    c.Config.Index == _lastSelectedChannelIndex);
                SelectedTab = preferredChannel
                    ?? (Tabs.Count > 0 ? Tabs[Math.Min(idx, Tabs.Count - 1)] : null);
            }
            // Closing a DM tab means it should not reopen next launch.
            SaveSettings();
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

    /// <summary>Build the RX device selector list with an availability
    /// annotation for hardware backends.</summary>
    private IReadOnlyList<DeviceOption> BuildRxDeviceOptions()
    {
        string Label(RadioDeviceKind kind, string name) =>
            _core.IsDeviceAvailable(kind) ? name : $"{name} (not found)";
        return new[]
        {
            new DeviceOption(RadioDeviceKind.HackRf, Label(RadioDeviceKind.HackRf, "HackRF")),
            new DeviceOption(RadioDeviceKind.RtlSdr, Label(RadioDeviceKind.RtlSdr, "RTL-SDR")),
            new DeviceOption(RadioDeviceKind.Null, "None"),
        };
    }

    /// <summary>Build the TX device selector list. RTL-SDR is receive-only, so
    /// it is intentionally not offered here.</summary>
    private IReadOnlyList<DeviceOption> BuildTxDeviceOptions()
    {
        string Label(RadioDeviceKind kind, string name) =>
            _core.IsDeviceAvailable(kind) ? name : $"{name} (not found)";
        return new[]
        {
            new DeviceOption(RadioDeviceKind.HackRf, Label(RadioDeviceKind.HackRf, "HackRF")),
            new DeviceOption(RadioDeviceKind.Null, "None"),
        };
    }

    private IReadOnlyList<SampleRateOption> BuildRxSampleRateOptions(RadioDeviceKind kind)
    {
        uint[] baseRates = kind switch
        {
            RadioDeviceKind.HackRf => HackRfSampleRatesHz,
            RadioDeviceKind.RtlSdr => RtlSdrSampleRatesHz,
            _ => Array.Empty<uint>(),
        };

        uint maxRateHz = kind switch
        {
            RadioDeviceKind.HackRf => HackRfMaxSelectableRateHz,
            RadioDeviceKind.RtlSdr => RtlSdrDecodeSafeMaxRateHz,
            _ => 0u,
        };

        IEnumerable<uint> rates = maxRateHz > 0
            ? baseRates.Where(rate => rate <= maxRateHz)
            : baseRates;

        return rates.Select(rate => new SampleRateOption(rate, FormatSampleRateLabel(kind, rate))).ToArray();
    }

    private void RefreshSampleRateSelection(RadioDeviceKind kind, uint requestedHz)
    {
        SampleRateOptions = BuildRxSampleRateOptions(kind);
        OnPropertyChanged(nameof(SampleRateOptions));

        _suppressSampleRateUpdate = true;
        try
        {
            SelectedRxSampleRate = SelectNearestSampleRate(SampleRateOptions, requestedHz);
        }
        finally
        {
            _suppressSampleRateUpdate = false;
        }

        if (SelectedRxSampleRate is not null)
        {
            _core.SetDeviceOption("rx_sample_rate_hz", checked((int)SelectedRxSampleRate.Hz));
            SpectrumSpanHz = SelectedRxSampleRate.Hz;
        }
        else if (!IsRunning)
        {
            SpectrumSpanHz = 0.0;
        }
        OnPropertyChanged(nameof(CanSelectRxSampleRate));
    }

    private static SampleRateOption? SelectNearestSampleRate(IReadOnlyList<SampleRateOption> options, uint requestedHz)
    {
        if (options.Count == 0) return null;
        if (requestedHz == 0)
            return options.FirstOrDefault(o => o.Hz == 2_400_000u) ?? options[0];

        SampleRateOption best = options[0];
        ulong bestDelta = AbsDiff(best.Hz, requestedHz);
        for (int i = 1; i < options.Count; i++)
        {
            ulong delta = AbsDiff(options[i].Hz, requestedHz);
            if (delta < bestDelta)
            {
                best = options[i];
                bestDelta = delta;
            }
        }
        return best;
    }

    private static ulong AbsDiff(uint left, uint right) =>
        left >= right ? (ulong)(left - right) : (ulong)(right - left);

    private static string FormatSampleRateLabel(RadioDeviceKind kind, uint hz)
    {
        string label = $"{(hz / 1_000_000.0).ToString("0.###", CultureInfo.InvariantCulture)} MS/s";
        if (kind == RadioDeviceKind.HackRf && hz > HackRfStableMaxRateHz)
            label += " (experimental)";
        return label;
    }

    /// <summary>Switch the RX radio backend (only valid while stopped) and
    /// refresh the device badge / status.</summary>
    private void ApplyRxDevice(RadioDeviceKind kind)
    {
        if (IsRunning) return;
        _core.SetRxDevice(kind);
        RefreshSampleRateSelection(kind, GetSavedRxSampleRateHz(kind));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(TxDeviceName));
        OnPropertyChanged(nameof(DeviceStatus));
        OnPropertyChanged(nameof(HasRealRadio));
        OnPropertyChanged(nameof(DeviceBadge));
        OnPropertyChanged(nameof(CanTransmit));
        OnPropertyChanged(nameof(CanSelectRxSampleRate));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
        SendDeviceMetricsCommand.NotifyCanExecuteChanged();
        Status = $"Idle (RX {_core.DeviceName}, TX {_core.TxDeviceName})";
        Log(DeviceBadge);
        if (ShouldLogDeviceStatus(_core.DeviceStatus))
            Log(_core.DeviceStatus);
        SaveSettings();
    }

    /// <summary>Switch the TX radio backend (only valid while stopped) and
    /// refresh transmit affordances.</summary>
    private void ApplyTxDevice(RadioDeviceKind kind)
    {
        if (IsRunning) return;
        _core.SetTxDevice(kind);
        OnPropertyChanged(nameof(TxDeviceName));
        OnPropertyChanged(nameof(DeviceStatus));
        OnPropertyChanged(nameof(HasRealRadio));
        OnPropertyChanged(nameof(DeviceBadge));
        OnPropertyChanged(nameof(CanTransmit));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
        SendDeviceMetricsCommand.NotifyCanExecuteChanged();
        Status = $"Idle (RX {_core.DeviceName}, TX {_core.TxDeviceName})";
        Log(DeviceBadge);
        if (!_core.CanTransmit)
            Log("TX device cannot transmit; choose HackRF for transmit.");
        if (ShouldLogDeviceStatus(_core.DeviceStatus))
            Log(_core.DeviceStatus);
        SaveSettings();
    }

    /// <summary>Syncs OverrideSf/BwKhz/Cr to the firmware defaults for
    /// <paramref name="preset"/> without triggering retune or save.</summary>
    private void ApplyPresetToLoraParams(LoraPreset preset)
    {
        var p = MeshRF.LoraParamsHelper.FromPreset(preset);
        // Suppress change handlers so we don't retune three times.
        _suppressLoraParamSync = true;
        try
        {
            OverrideSf    = p.Sf;
            OverrideBwKhz = p.BwKhz;
            OverrideCr    = p.Cr;
        }
        finally
        {
            _suppressLoraParamSync = false;
        }
        OnPropertyChanged(nameof(IsCustomLoraParams));
    }

    /// <summary>Restart the receiver with the current parameters if it's running.</summary>
    private void RetuneIfRunning()
    {
        if (!IsRunning) return;
        if (SelectedDevice?.Kind == RadioDeviceKind.Null)
        {
            Stop();
            Status = "RX stopped (no RX device selected).";
            Log(Status);
            return;
        }
        try
        {
            _core.Stop();
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            StartRxWithCurrentParams(hz);
            Status = BuildRxStatus();
            Log($"retuned \u2192 {Status}");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Status = $"Error: {ex.Message}";
            Log(Status);
        }
    }

    /// <summary>Starts RX using explicit SF/BW/CR when they differ from the preset,
    /// or the faster preset path otherwise.</summary>
    private void StartRxWithCurrentParams(ulong centerFreqHz)
    {
        if (IsCustomLoraParams)
        {
            var bwHz = (uint)Math.Round(OverrideBwKhz * 1000.0);
            _core.StartRxParams(OverrideSf, bwHz, OverrideCr, centerFreqHz);
        }
        else
        {
            _core.StartRx(SelectedPreset, centerFreqHz);
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
        if (SelectedDevice?.Kind == RadioDeviceKind.Null)
        {
            Status = "Select an RX device before starting receive.";
            Log(Status);
            return;
        }
        try
        {
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            StartRxWithCurrentParams(hz);
            IsRunning = true;
            _lastRxPlayUtc = DateTime.UtcNow;
            Status = BuildRxStatus();
            Log(Status);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Status = $"Error: {ex.Message}";
            Log(Status);
        }
    }

    private string BuildRxStatus()
    {
        string modem = IsCustomLoraParams
            ? $"SF{OverrideSf} BW{OverrideBwKhz:G}kHz CR4/{OverrideCr}"
            : SelectedPreset.ToString();
        string rate = SelectedRxSampleRate is null
            ? string.Empty
            : $" / {FormatSampleRateLabel(SelectedDevice?.Kind ?? RadioDeviceKind.Null, SelectedRxSampleRate.Hz)}";
        return $"RX @ {CenterFreqMHz:F3} MHz / {modem}{rate}";
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

    /// <summary>True when the selected TX radio backend can transmit (HackRF).</summary>
    public bool CanTransmit => _core.CanTransmit;

    /// <summary>Text typed into the per-channel compose box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _composeText = string.Empty;

    [ObservableProperty]
    private uint _pendingReplyPacketId;

    [ObservableProperty]
    private string _pendingReplyContext = string.Empty;

    public bool HasPendingReply => PendingReplyPacketId != 0;

    partial void OnPendingReplyPacketIdChanged(uint value) =>
        OnPropertyChanged(nameof(HasPendingReply));

    /// <summary>HackRF TX VGA gain in dB (0..47). Default to max for range.</summary>
    [ObservableProperty]
    private byte _txGainDb = 47;

    /// <summary>Enable the HackRF RF amplifier during TX.</summary>
    [ObservableProperty]
    private bool _txAmpEnable;

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

    // Outstanding traceroute requests, keyed by the request packet id, so an
    // inbound TRACEROUTE_APP reply referencing that id can be matched to the
    // node we traced. Entries are best-effort and never expire-swept (the reply
    // either arrives or the user can trace again after the cooldown).
    private readonly Dictionary<uint, uint> _pendingTraceroutes = new();

    /// <summary>Minimum spacing between traceroute requests (Meshtastic-style
    /// client throttle to avoid flooding the mesh with discovery traffic).</summary>
    private static readonly TimeSpan TracerouteCooldown = TimeSpan.FromSeconds(30);

    /// <summary>When the last traceroute request was transmitted (for the cooldown).</summary>
    private DateTime _lastTracerouteUtc = DateTime.MinValue;

    /// <summary>Minimum spacing between position requests (client throttle).</summary>
    private static readonly TimeSpan PositionRequestCooldown = TimeSpan.FromSeconds(30);

    /// <summary>When the last position request was transmitted (for the cooldown).</summary>
    private DateTime _lastPositionRequestUtc = DateTime.MinValue;

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

    [RelayCommand]
    private void ReplyToMessage(ChannelMessage? target)
    {
        if (target is null || target.PacketId == 0) return;
        PendingReplyPacketId = target.PacketId;
        PendingReplyContext = BuildReplyContextText(target);
        Status = "Reply target selected for the next message.";
        Log(Status);
    }

    [RelayCommand]
    private void ClearPendingReply()
    {
        if (!HasPendingReply) return;
        ClearPendingReplyState();
        Status = "Reply target cleared.";
        Log(Status);
    }

    [RelayCommand]
    private void InsertComposeEmoji()
    {
        string? emoji = EmojiPickerWindow.PickEmoji(
            Application.Current?.MainWindow,
            EmojiPickerWindow.EmojiPickerMode.Reaction);
        if (string.IsNullOrWhiteSpace(emoji)) return;
        ComposeText += emoji;
    }

    /// <summary>
    /// Encode the composed text as a Meshtastic TEXT_MESSAGE_APP frame on the
    /// selected channel and transmit it on the current preset/frequency. The
    /// sent line is echoed into the channel's message list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        var ch = SelectedChannel;
        if (ch is null) return;
        var text = (ComposeText ?? string.Empty).Trim();
        if (text.Length == 0) return;
        uint replyId = PendingReplyPacketId;

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
                ch.Config, _myNodeNum, packetId, text, hopLimit: (byte)HopLimit,
                replyId: replyId,
                okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
            if (ok)
            {
                ch.Messages.Add(new ChannelMessage
                {
                    FromId = NodeDisplayName(_myNodeNum),
                    SenderNodeNum = _myNodeNum,
                    Text = replyId != 0
                        ? BuildOutgoingReplyDisplayText(text, replyId)
                        : text,
                    PacketId = packetId,
                    IsOutgoing = true,
                    IsReplyLinked = replyId != 0,
                    ReplyTargetFound = replyId != 0 && PendingReplyContext.Length > 0,
                    ReplyToPacketId = replyId,
                });
                if (ch.Messages.Count > 1000) ch.Messages.RemoveAt(0);
                PersistOutgoingText(0xFFFFFFFFu, packetId, text, ch.Config.Name,
                                    MessageDelivery.None, replyId);
                ComposeText = string.Empty;
                if (replyId != 0) ClearPendingReplyState();
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
    private async Task SendDirectMessageAsync(ConversationViewModel? convo)
    {
        if (convo is null) return;
        var text = (convo.ComposeText ?? string.Empty).Trim();
        if (text.Length == 0) return;
        uint replyId = PendingReplyPacketId;

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
                // Proactively pull the peer's key using the same NODEINFO_APP
                // request style as official Meshtastic apps.
                var requestChannel = ResolveRequestChannel();
                if (requestChannel is not null)
                    SendNodeInfoExchangeRequest(convo.NodeNum, requestChannel);
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
                      myPriv, peerPub, hopLimit: (byte)HopLimit, wantAck: true,
                    replyId: replyId,
                      okToMqtt: OkToMqtt)
                : MeshEncoder.EncodeTextMessage(
                      ch!.Config, _myNodeNum, packetId, text,
                      to: convo.NodeNum, hopLimit: (byte)HopLimit, wantAck: true,
                    replyId: replyId,
                      okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
            if (ok)
            {
                var sent = new ChannelMessage
                {
                    FromId = NodeDisplayName(_myNodeNum),
                    SenderNodeNum = _myNodeNum,
                    Text = replyId != 0
                        ? BuildOutgoingReplyDisplayText(text, replyId)
                        : text,
                    PacketId = packetId,
                    IsOutgoing = true,
                    IsReplyLinked = replyId != 0,
                    ReplyTargetFound = replyId != 0 && PendingReplyContext.Length > 0,
                    ReplyToPacketId = replyId,
                    Delivery = MessageDelivery.Sent,
                };
                convo.Add(sent);
                TrackPendingAck(sent);
                PersistOutgoingText(convo.NodeNum, packetId, text,
                                    usePkc ? "PKC" : (ch?.Config.Name ?? string.Empty),
                                    MessageDelivery.Sent, replyId);
                convo.ComposeText = string.Empty;
                if (replyId != 0) ClearPendingReplyState();
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

    /// <summary>
    /// Send a Meshtastic per-message emoji reaction (Data.reply_id + Data.emoji)
    /// targeting the selected message in the active chat tab.
    /// </summary>
    [RelayCommand]
    private void InsertConversationEmoji(ConversationViewModel? convo)
    {
        if (convo is null) return;
        string? emoji = EmojiPickerWindow.PickEmoji(
            Application.Current?.MainWindow,
            EmojiPickerWindow.EmojiPickerMode.Reaction);
        if (string.IsNullOrWhiteSpace(emoji)) return;
        convo.ComposeText += emoji;
    }

    [RelayCommand]
    private async Task SendReactionAsync(ChannelMessage? target)
    {
        if (target is null || target.PacketId == 0) return;
        if (!CanTransmit) return;

        if (_myNodeNum == 0)
        {
            Status = "Set your node ID (Identity) before sending reactions.";
            Log(Status);
            return;
        }

        string? emoji = EmojiPickerWindow.PickEmoji(
            Application.Current?.MainWindow,
            EmojiPickerWindow.EmojiPickerMode.Reaction);
        if (string.IsNullOrWhiteSpace(emoji)) return;
        uint? emojiCodePoint = EmojiToCodePoint(emoji);
        if (emojiCodePoint is null || emojiCodePoint.Value == 0)
        {
            Status = "Selected emoji is not valid.";
            Log(Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            bool ok;
            uint to;
            string channelTag;

            if (SelectedTab is ConversationViewModel convo)
            {
                to = convo.NodeNum;
                var myPriv = TryParseKeyBase64(MyPrivateKey);
                var peerPub = TryParseHex(_nodeStore.Get(convo.NodeNum)?.PublicKey);
                bool usePkc = myPriv.Length == 32 && peerPub.Length == 32;

                var primary = Channels.FirstOrDefault();
                if (!usePkc && primary is null)
                {
                    Status = "No channel configured to carry this reaction.";
                    Log(Status);
                    return;
                }

                byte[] frame = usePkc
                    ? MeshEncoder.EncodePkcTextMessage(
                        _myNodeNum, convo.NodeNum, packetId, emoji,
                        myPriv, peerPub, hopLimit: (byte)HopLimit, wantAck: true,
                        replyId: target.PacketId, emoji: 1,
                        okToMqtt: OkToMqtt)
                    : MeshEncoder.EncodeTextMessage(
                        primary!.Config, _myNodeNum, packetId, emoji,
                        to: convo.NodeNum, hopLimit: (byte)HopLimit, wantAck: true,
                        replyId: target.PacketId, emoji: 1,
                        okToMqtt: OkToMqtt);

                ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
                channelTag = usePkc ? "PKC" : primary?.Config.Name ?? string.Empty;
            }
            else if (SelectedTab is ChannelViewModel ch)
            {
                to = 0xFFFFFFFFu;
                var frame = MeshEncoder.EncodeTextMessage(
                    ch.Config, _myNodeNum, packetId, emoji,
                    hopLimit: (byte)HopLimit,
                    replyId: target.PacketId, emoji: 1,
                    okToMqtt: OkToMqtt);
                ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
                channelTag = ch.Config.Name;
            }
            else
            {
                return;
            }

            if (!ok)
            {
                Status = "Transmit failed (device cannot transmit).";
                Log(Status);
                return;
            }

            target.AddReaction(emoji, NodeDisplayName(_myNodeNum));
            PersistOutgoingReaction(to, packetId, target.PacketId, emoji, channelTag);
            Status = $"Reaction {emoji} sent.";
            Log(Status);
        }
        catch (Exception ex)
        {
            Status = $"Reaction send error: {ex.Message}";
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
    private async Task SendNodeInfoAsync()
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
                hwModel: (uint)HardwareModels.Id(MyHwModel), role: role, publicKey: pubKey, hopLimit: (byte)HopLimit,
                okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
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

    private bool CanSendTelemetry() => CanTransmit && _myNodeNum != 0;

    /// <summary>
    /// Broadcast our location (POSITION_APP) on the primary channel, fuzzed to
    /// that channel's position precision (firmware behaviour). Uses the home
    /// latitude/longitude configured in settings.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendPosition))]
    private async Task SendPositionAsync()
    {
        if (_myNodeNum == 0)
        {
            Status = "Set your node ID (Identity) before sending position.";
            Log(Status);
            return;
        }

        if (HomeLatitude is not double lat || HomeLongitude is not double lon)
        {
            Status = "Set your location latitude/longitude (Identity) before sending position.";
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

        if (primary.Config.PositionPrecision == 0)
        {
            Status = $"Location sharing is off for {primary.DisplayName} " +
                     "(choose a precision in the channel's settings first).";
            Log(Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodePosition(
                primary.Config, _myNodeNum, packetId,
                lat, lon, altitudeM: HomeAltitude,
                precisionBits: primary.Config.PositionPrecision,
                hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
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
    /// Broadcast a TELEMETRY_APP DeviceMetrics payload on the primary channel.
    /// Manual trigger only (no periodic broadcast scheduler).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendTelemetry))]
    private async Task SendDeviceMetricsAsync()
    {
        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null)
        {
            Status = "No primary channel to send device metrics on.";
            Log(Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var self = _nodeStore.Get(_myNodeNum) ?? Nodes.FirstOrDefault(n => n.NodeNum == _myNodeNum);
            uint uptime = _lastRxPlayUtc is DateTime startUtc
                ? (uint)Math.Clamp((DateTime.UtcNow - startUtc).TotalSeconds, 0, uint.MaxValue)
                : (self?.UptimeSeconds ?? 0u);

            ComputeLocalAirtimeUtilization(out float channelUtil, out float airUtilTx);
            TryGetWindowsPowerTelemetry(out bool acOnline, out var winBatteryPct, out var winVoltageV);

            // Meshtastic treats battery_level > 100 as externally powered.
            byte batteryPct = acOnline
                ? (byte)101
                : (winBatteryPct ?? self?.BatteryPct ?? 0);
            float? voltageV = winVoltageV;
            if (voltageV is null && self?.VoltageV is float priorV && priorV > 0f)
                voltageV = priorV;

            var frame = MeshEncoder.EncodeTelemetryDeviceMetrics(
                primary.Config, _myNodeNum, packetId,
                batteryLevel: batteryPct,
                voltage: voltageV,
                channelUtilization: channelUtil,
                airUtilTx: airUtilTx,
                uptimeSeconds: uptime,
                hopLimit: (byte)HopLimit,
                okToMqtt: OkToMqtt);

            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
            Status = ok
                ? $"Sent device metrics ({frame.Length} B) on {primary.DisplayName}"
                : "Transmit failed (device cannot transmit).";
            Log(Status);
        }
        catch (Exception ex)
        {
            Status = $"Device metrics error: {ex.Message}";
            Log(Status);
        }
    }

    /// <summary>
    /// Send a waypoint to the mesh and persist it locally so it remains on the
    /// map until explicitly deleted.
    /// </summary>
    public async Task SendWaypointFromMapAsync(double lat, double lon,
                                               ChannelConfig? channel = null)
    {
        if (!CanTransmit || _myNodeNum == 0)
        {
            Status = "Set your node ID and a TX-capable device before sending waypoints.";
            Log(Status);
            return;
        }

        var selectedChannel = ResolveRequestChannel(channel);
        if (selectedChannel is null)
        {
            Status = "No enabled channel to send waypoint on.";
            Log(Status);
            return;
        }

        var selectedChannelVm = Channels.FirstOrDefault(c => c.Config.Index == selectedChannel.Index);
        var selectedChannelName = selectedChannelVm?.DisplayName ?? selectedChannel.Name;

        try
        {
            uint packetId = NextPacketId();
            uint waypointId = packetId;
            string name = string.IsNullOrWhiteSpace(WaypointNameInput)
                ? $"Waypoint {DateTime.Now:HHmmss}"
                : WaypointNameInput.Trim();
            string description = WaypointDescriptionInput?.Trim() ?? string.Empty;
            uint? icon = EmojiToCodePoint(SelectedWaypointEmoji);
            uint expireEpoch = BuildWaypointExpiryEpoch();
            var frame = MeshEncoder.EncodeWaypoint(
                selectedChannel,
                _myNodeNum,
                packetId,
                waypointId,
                lat,
                lon,
                name: name,
                description: description,
                expireEpoch: expireEpoch,
                icon: icon,
                hopLimit: (byte)HopLimit,
                okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
            if (!ok)
            {
                Status = "Transmit failed (device cannot transmit).";
                Log(Status);
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _waypointStore.Upsert(new WaypointRecord
            {
                FromNode = _myNodeNum,
                WaypointId = waypointId,
                PacketId = packetId,
                Channel = selectedChannel.Name,
                Name = name,
                Description = description,
                Icon = icon,
                Latitude = lat,
                Longitude = lon,
                ExpireEpoch = expireEpoch,
                RxEpoch = now,
            });
            ReloadWaypoints();

            Status = $"Sent waypoint ({frame.Length} B) on {selectedChannelName}";
            Log(Status);
        }
        catch (Exception ex)
        {
            Status = $"Waypoint error: {ex.Message}";
            Log(Status);
        }
    }

    private static uint? EmojiToCodePoint(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return null;
        try
        {
            int cp = char.ConvertToUtf32(emoji.Trim(), 0);
            return cp > 0 ? (uint)cp : null;
        }
        catch { return null; }
    }

    private uint BuildWaypointExpiryEpoch()
    {
        if (!UseWaypointExpiry || WaypointExpiryDate is not DateTime date)
            return 0;

        if (!int.TryParse(WaypointExpiryHour12, NumberStyles.None, CultureInfo.InvariantCulture, out int hour12) ||
            hour12 is < 1 or > 12)
            return 0;
        if (!int.TryParse(WaypointExpiryMinute, NumberStyles.None, CultureInfo.InvariantCulture, out int minute) ||
            minute is < 0 or > 59)
            return 0;
        if (!int.TryParse(WaypointExpirySecond, NumberStyles.None, CultureInfo.InvariantCulture, out int second) ||
            second is < 0 or > 59)
            return 0;

        bool isPm;
        if (string.Equals(WaypointExpiryMeridiem, "PM", StringComparison.OrdinalIgnoreCase))
            isPm = true;
        else if (string.Equals(WaypointExpiryMeridiem, "AM", StringComparison.OrdinalIgnoreCase))
            isPm = false;
        else
            return 0;

        int hour24 = hour12 % 12;
        if (isPm) hour24 += 12;

        var local = new DateTime(
            date.Year,
            date.Month,
            date.Day,
            hour24,
            minute,
            second,
            DateTimeKind.Local);
        return (uint)new DateTimeOffset(local).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Send a request-only NodeInfo packet to <paramref name="to"/>: empty
    /// NODEINFO payload + <c>want_response</c> set, prompting that node to
    /// reply with its own NodeInfo without us advertising ours in the request.
    /// No-op when we can't transmit. When
    /// <paramref name="packetId"/> is 0 a fresh id is allocated; callers that
    /// need to reference the sent packet (e.g. to log a conversation note) can
    /// pass one in.
    /// </summary>
    private ChannelConfig? ResolveRequestChannel(ChannelConfig? preferred = null)
    {
        if (preferred is not null) return preferred;
        return Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary)?.Config
            ?? Channels.FirstOrDefault(c => c.Config.Role != ChannelRole.Disabled)?.Config;
    }

    private ChannelConfig? FindChannelByName(string? channelName)
    {
        if (!string.IsNullOrWhiteSpace(channelName))
        {
            var channel = Channels.FirstOrDefault(c =>
                string.Equals(c.Config.Name, channelName, StringComparison.Ordinal));
            if (channel is not null) return channel.Config;
        }
        return ResolveRequestChannel();
    }

    private static bool IsDeviceTelemetryRequest(byte[] payload)
    {
        var rdr = new ProtoReader(payload);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            if (field == 2 && wt == ProtoReader.WireType.Len)
                return true;
            rdr.SkipField(wt);
        }
        return false;
    }

    private void SendNodeInfoRequestOnly(uint to, ChannelConfig channel, uint packetId = 0)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;

        try
        {
            if (packetId == 0) packetId = NextPacketId();
            var frame = MeshEncoder.EncodeNodeInfoRequest(
                channel, _myNodeNum, to, packetId,
                hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            var preset = SelectedPreset; var gain = TxGainDb; var amp = TxAmpEnable;
            TransmitBackground(preset, hz, frame, gain, amp);
        }
        catch (Exception ex)
        {
            Log($"  NodeInfo request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Send our NodeInfo directed at <paramref name="to"/> with
    /// <c>want_response</c> set, prompting that node to reply with its own
    /// NodeInfo. This is the exchange flow.
    /// </summary>
    private void SendNodeInfoExchangeRequest(uint to, ChannelConfig channel, uint packetId = 0)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;

        try
        {
            if (packetId == 0) packetId = NextPacketId();
            uint role = RoleEnumValue(MyRole);
            byte[] pubKey = TryParseKeyBase64(MyPublicKey);
            var frame = MeshEncoder.EncodeNodeInfo(
                channel, _myNodeNum, packetId,
                MyLongName ?? string.Empty, MyShortName ?? string.Empty,
                hwModel: (uint)HardwareModels.Id(MyHwModel), role: role, publicKey: pubKey,
                to: to, hopLimit: (byte)HopLimit, wantResponse: true);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            var preset = SelectedPreset; var gain = TxGainDb; var amp = TxAmpEnable;
            TransmitBackground(preset, hz, frame, gain, amp);
        }
        catch (Exception ex)
        {
            Log($"  NodeInfo exchange failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reply to a directed NodeInfo request with our NodeInfo (no want_response,
    /// to avoid a request/response loop) so the requester learns our public key.
    /// </summary>
    private void RequestNodeInfoReply(uint to, ChannelConfig? channel = null)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;
        var replyChannel = ResolveRequestChannel(channel);
        if (replyChannel is null) return;

        try
        {
            uint packetId = NextPacketId();
            uint role = RoleEnumValue(MyRole);
            byte[] pubKey = TryParseKeyBase64(MyPublicKey);
            var frame = MeshEncoder.EncodeNodeInfo(
                replyChannel, _myNodeNum, packetId,
                MyLongName ?? string.Empty, MyShortName ?? string.Empty,
                hwModel: (uint)HardwareModels.Id(MyHwModel), role: role, publicKey: pubKey,
                to: to, hopLimit: (byte)HopLimit, wantResponse: false);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
        }
        catch (Exception ex)
        {
            Log($"  NodeInfo reply failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reply to a directed position request with our location (POSITION_APP),
    /// fuzzed to the primary channel's precision, addressed back to the
    /// requester. No-op when we can't transmit, have no home location set, or
    /// the primary channel has location sharing disabled (precision 0).
    /// </summary>
    private byte[] BuildDeviceTelemetryFrame(ChannelConfig channel,
                                             uint packetId,
                                             uint to = 0xFFFFFFFFu,
                                             uint requestId = 0)
    {
        var self = _nodeStore.Get(_myNodeNum) ?? Nodes.FirstOrDefault(n => n.NodeNum == _myNodeNum);
        uint uptime = _lastRxPlayUtc is DateTime startUtc
            ? (uint)Math.Clamp((DateTime.UtcNow - startUtc).TotalSeconds, 0, uint.MaxValue)
            : (self?.UptimeSeconds ?? 0u);

        ComputeLocalAirtimeUtilization(out float channelUtil, out float airUtilTx);
        TryGetWindowsPowerTelemetry(out bool acOnline, out var winBatteryPct, out var winVoltageV);

        byte batteryPct = acOnline
            ? (byte)101
            : (winBatteryPct ?? self?.BatteryPct ?? 0);
        float? voltageV = winVoltageV;
        if (voltageV is null && self?.VoltageV is float priorV && priorV > 0f)
            voltageV = priorV;

        return MeshEncoder.EncodeTelemetryDeviceMetrics(
            channel, _myNodeNum, packetId,
            batteryLevel: batteryPct,
            voltage: voltageV,
            channelUtilization: channelUtil,
            airUtilTx: airUtilTx,
            uptimeSeconds: uptime,
            to: to,
            hopLimit: (byte)HopLimit,
            okToMqtt: OkToMqtt,
            requestId: requestId);
    }

    private void ReplyWithPosition(uint to, uint requestId = 0, ChannelConfig? channel = null)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;
        if (HomeLatitude is not double lat || HomeLongitude is not double lon) return;
        var replyChannel = ResolveRequestChannel(channel);
        if (replyChannel is null || replyChannel.PositionPrecision == 0) return;

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodePosition(
                replyChannel, _myNodeNum, packetId,
                lat, lon, altitudeM: HomeAltitude,
                precisionBits: replyChannel.PositionPrecision,
                to: to, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt,
                requestId: requestId);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
        }
        catch (Exception ex)
        {
            Log($"  position reply failed: {ex.Message}");
        }
    }

    private void ReplyWithTelemetry(uint to, uint requestId = 0, ChannelConfig? channel = null)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;
        var replyChannel = ResolveRequestChannel(channel);
        if (replyChannel is null) return;

        try
        {
            uint packetId = NextPacketId();
            var frame = BuildDeviceTelemetryFrame(replyChannel, packetId, to, requestId);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
        }
        catch (Exception ex)
        {
            Log($"  telemetry reply failed: {ex.Message}");
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
    /// our own sends are never heard back as decodable, so store them here). The
    /// <paramref name="delivery"/> state is stored verbatim so it reloads the
    /// same way the live UI showed it: channel broadcasts are never ACKed, so
    /// they carry <see cref="MessageDelivery.None"/> and show no status, while
    /// direct messages start as <see cref="MessageDelivery.Sent"/> and are later
    /// updated to delivered/failed.</summary>
    private void PersistOutgoingText(uint to, uint packetId, string text, string channel,
                                    MessageDelivery delivery = MessageDelivery.Sent,
                                    uint replyId = 0)
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
                ReplyId = replyId,
                Decrypted = true,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Delivery = (int)delivery,
            });
        }
        catch (Exception ex) { Log($"message store failed: {ex.Message}"); }
    }

    private void PersistOutgoingReaction(uint to, uint packetId, uint replyId,
                                         string emojiText, string channel)
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
                Text = emojiText ?? string.Empty,
                ReplyId = replyId,
                Emoji = 1,
                IsReaction = true,
                Decrypted = true,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Delivery = (int)MessageDelivery.None,
            });
        }
        catch (Exception ex) { Log($"reaction store failed: {ex.Message}"); }
    }

    /// <summary>Persist an app-generated conversation note (a traceroute result
    /// or position-request echo shown in a DM tab) so it survives a refresh /
    /// restart. Stored under <see cref="MessageStore.ConversationNotePort"/>
    /// with the display tag in the channel column; reloaded by
    /// <see cref="LoadConversationHistory"/> via <see cref="BuildHistoryMessage"/>.</summary>
    private void PersistConversationNote(uint peer, bool outgoing, uint packetId,
                                         string tag, string text,
                                         float? rssi = null, float? snr = null)
    {
        if (_myNodeNum == 0 || peer == 0 || peer == 0xFFFFFFFFu) return;
        try
        {
            _messageStore.Add(new MessageRecord
            {
                PacketId = packetId,
                FromNode = outgoing ? _myNodeNum : peer,
                ToNode = outgoing ? peer : _myNodeNum,
                Channel = tag ?? string.Empty,
                PortNum = MessageStore.ConversationNotePort,
                Text = text,
                Decrypted = true,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                RssiDbfs = rssi,
                SnrDb = snr,
                Delivery = (int)MessageDelivery.None,
            });
        }
        catch (Exception ex) { Log($"note store failed: {ex.Message}"); }
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
        bool ack = result.RoutingError == 0;
        bool addressedToUs = _myNodeNum != 0 && header.To == _myNodeNum;

        if (addressedToUs && result.RequestId != 0 &&
            _pendingAcks.Remove(result.RequestId, out var pending))
        {
            pending.Message.Delivery = ack ? MessageDelivery.Delivered : MessageDelivery.Failed;
            PersistDelivery(pending.Message);
            Log(ack
                ? $"  ACK from {NodeDisplayName(header.From)} for id {result.RequestId:x8}"
                : $"  NAK ({result.RoutingError}) from {NodeDisplayName(header.From)} for id {result.RequestId:x8}");
            return;
        }

        var target = header.IsBroadcast
            ? "^all"
            : addressedToUs ? "us" : NodeDisplayName(header.To);
        Log(ack
            ? $"  routing ACK {NodeDisplayName(header.From)} -> {target}"
                + (result.RequestId != 0 ? $" for id {result.RequestId:x8}" : string.Empty)
            : $"  routing NAK ({result.RoutingError}) {NodeDisplayName(header.From)} -> {target}"
                + (result.RequestId != 0 ? $" for id {result.RequestId:x8}" : string.Empty));
    }

    /// <summary>Handle an inbound TRACEROUTE_APP packet: a reply to a request we
    /// sent (render the path), a request directed at us (reply with the route so
    /// the tracer sees us), or overheard traceroute traffic (just logged).</summary>
    private void HandleTraceroute(MeshHeader header, MeshDecodeResult result, float? snrDb)
    {
        bool addressedToUs = _myNodeNum != 0 && header.To == _myNodeNum && !header.IsBroadcast;

        // A reply to a request we sent: request_id matches and addressed to us.
        if (result.RequestId != 0 && addressedToUs)
        {
            _pendingTraceroutes.Remove(result.RequestId);
            var name = NodeDisplayName(header.From);
            string path = FormatTraceroute(_myNodeNum, header.From, result.RouteDiscovery);
            Log($"  traceroute reply from {name}: {path}");
            var convo = OpenConversation(header.From, name, focus: false);
            float? rssi = float.IsNegativeInfinity(RssiDbfs) ? null : RssiDbfs;
            var noteText = $"Route to {name}: {path}";
            convo.Add(new ChannelMessage
            {
                FromId = "traceroute",
                Text = noteText,
                RssiDbm = rssi,
                SnrDb = snrDb,
                PacketId = header.PacketId,
            });
            PersistConversationNote(header.From, outgoing: false, header.PacketId,
                                    "traceroute", noteText, rssi, snrDb);
            return;
        }

        // A request directed at us: reply so the originator sees us in the path.
        if (result.WantResponse && addressedToUs)
        {
            Log($"  traceroute request from {NodeDisplayName(header.From)} — replying");
            SendTracerouteReply(header, result, snrDb);
            return;
        }

        // Otherwise it's traceroute traffic we merely overheard.
        Log($"  traceroute {header.FromId} \u2192 !{header.To:x8}");
    }

    /// <summary>Reply to a traceroute request directed at us, echoing the
    /// forward route with the SNR of the hop that reached us appended (mirrors
    /// firmware <c>TraceRouteModule</c>), referencing the original packet id.</summary>
    private void SendTracerouteReply(MeshHeader origHeader, MeshDecodeResult result, float? snrDb)
    {
        if (!CanTransmit || _myNodeNum == 0) return;
        var ch = Channels.FirstOrDefault(c => c.Config.Name == result.ChannelName)
                 ?? Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary)
                 ?? Channels.FirstOrDefault();
        if (ch is null) return;

        try
        {
            var rd = result.RouteDiscovery;
            var route = rd?.Route?.ToList() ?? new List<uint>();
            var snrTowards = rd?.SnrTowards?.ToList() ?? new List<int>();
            // SNR of the hop that reached us, scaled by 4 (-128 = unknown).
            // Cast through sbyte to match the firmware's (int8_t)(snr * 4) — the
            // nanopb-generated struct stores snr_towards as int8_t and rejects
            // values outside [-128, 127] with "integer too large".
            snrTowards.Add(snrDb is float s ? (int)(sbyte)(int)Math.Round(s * 4) : -128);

            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodeTracerouteReply(
                ch.Config, _myNodeNum, origHeader.From, packetId, origHeader.PacketId,
                route, snrTowards, hopLimit: (byte)HopLimit);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
        }
        catch (Exception ex)
        {
            Log($"  traceroute reply failed: {ex.Message}");
        }
    }

    /// <summary>Handle an inbound NEIGHBORINFO_APP packet: log the sender's
    /// neighbor list with per-neighbor SNR.</summary>
    private void HandleNeighborInfo(MeshHeader header, MeshRF.Mesh.MeshNeighborInfo ni)
    {
        var senderName = NodeDisplayName(header.From);
        if (ni.Neighbors.Count == 0)
        {
            Log($"  neighborinfo {header.FromId} ({senderName}): no neighbors reported");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"  neighborinfo {header.FromId} ({senderName}): {ni.Neighbors.Count} neighbor");
        if (ni.Neighbors.Count != 1) sb.Append('s');
        sb.Append(" — ");
        for (int i = 0; i < ni.Neighbors.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var n = ni.Neighbors[i];
            sb.Append($"!{n.NodeId:x8}");
            var name = NodeDisplayName(n.NodeId);
            if (!string.IsNullOrEmpty(name) && name != $"!{n.NodeId:x8}")
                sb.Append($" ({name})");
            sb.Append($" {n.Snr:+0.0;-0.0}dB");
        }
        Log(sb.ToString());
    }

    private void LogStoreForward(MeshHeader header, MeshRF.Mesh.MeshStoreForward sf)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"  storeforward {header.FromId}: {sf.Type}");

        if (sf.Heartbeat is { } hb)
        {
            sb.Append($" — period={hb.PeriodSeconds}s");
            if (hb.IsSecondary) sb.Append(" (secondary)");
        }
        if (sf.Stats is { } st)
        {
            var uptime = TimeSpan.FromSeconds(st.UpTimeSeconds);
            sb.Append($" — msgs={st.MessagesSaved}/{st.MessagesMax}, req={st.Requests}, hist={st.RequestsHistory}, up={uptime:d\\.hh\\:mm\\:ss}");
        }
        if (sf.HistoryMessages is { } hm)
        {
            sb.Append($" — historyMsgs={hm}");
            if (sf.HistoryWindow is { } hw) sb.Append($", window={hw}min");
        }
        if (!string.IsNullOrEmpty(sf.Text))
        {
            sb.Append($" — \"{sf.Text}\"");
        }

        Log(sb.ToString());
    }

    // Render a traceroute RouteDiscovery as "us → hop (snr) → … → dest". SNR
    // entries are stored scaled by 4; a sentinel of -128 (unknown hop) shows "?".
    private string FormatTraceroute(uint origin, uint dest, MeshRouteDiscovery? rd)
    {
        var nodes = new List<uint> { origin };
        if (rd?.Route is { Count: > 0 } hops) nodes.AddRange(hops);
        nodes.Add(dest);
        var snr = rd?.SnrTowards ?? (IReadOnlyList<int>)Array.Empty<int>();

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("  \u2192  ");
                int idx = i - 1;
                if (idx < snr.Count)
                {
                    int raw = snr[idx];
                    sb.Append(raw <= -128
                        ? "(?) "
                        : $"({(raw / 4.0).ToString("0.#", CultureInfo.InvariantCulture)} dB) ");
                }
            }
            sb.Append(TracerouteNodeLabel(nodes[i]));
        }
        int hopCount = nodes.Count - 1;
        sb.Append(hopCount <= 1 ? "  [direct]" : $"  [{hopCount} hops]");
        return sb.ToString();
    }

    private string TracerouteNodeLabel(uint nodeNum)
        => (nodeNum == 0 || nodeNum == 0xFFFFFFFFu) ? "unknown" : NodeDisplayName(nodeNum);

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
            var ackTarget = NodeDisplayName(origHeader.From);
            var ackId = origHeader.PacketId;
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable);
            Log($"  sent ACK to {ackTarget} for id {ackId:x8}");
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
        "RouterLate"   => 11,
        "ClientBase"   => 12,
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
        if (_myPrivateKeyBytes.Length != 32) return null;

        var senderPub = GetSenderPublicKeyBytes(header.From);
        if (senderPub.Length != 32) return null;

        return MeshDecoder.DecodePkc(frame, _myPrivateKeyBytes, senderPub);
    }

    private byte[] GetSenderPublicKeyBytes(uint nodeNum)
    {
        if (_pkcSenderPublicKeyBytes.TryGetValue(nodeNum, out var cached))
            return cached;

        var sender = _nodesByNum.GetValueOrDefault(nodeNum)
            ?? _nodeStore.Get(nodeNum);
        var parsed = TryParsePublicKeyHexFast(sender?.PublicKey);
        var value = parsed.Length == 32 ? parsed : Array.Empty<byte>();
        _pkcSenderPublicKeyBytes[nodeNum] = value;
        return value;
    }

    private static byte[] TryParsePublicKeyHexFast(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        var s = hex.Trim();
        if (s.Length != 64) return Array.Empty<byte>();
        try
        {
            var bytes = Convert.FromHexString(s);
            return bytes.Length == 32 ? bytes : Array.Empty<byte>();
        }
        catch
        {
            return Array.Empty<byte>();
        }
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
    /// Toggle recording of decoded LoRa payloads to a JSON (.json) file.
    /// Prompts for a path when starting; each successfully demodulated payload
    /// is appended as one object inside a single JSON array document. Closes
    /// and finalizes the JSON document when stopping.
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
            Title = "Record decoded payloads (.json)",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"payloads_{DateTime.Now:yyyyMMdd_HHmmss}.json",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _payloadWriter = new StreamWriter(dlg.FileName, append: false) { AutoFlush = true };
            _payloadCount = 0;
            _payloadJsonHasEntries = false;
            _payloadWriter.Write('[');
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
        try
        {
            if (_payloadJsonHasEntries)
                _payloadWriter.WriteLine();
            _payloadWriter.WriteLine("]");
            _payloadWriter.Flush();
            _payloadWriter.Dispose();
        }
        catch { /* ignore */ }
        _payloadWriter = null;
        _payloadJsonHasEntries = false;
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

    private static readonly JsonSerializerOptions PayloadRecordJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Pulls the peak-above-noise figure out of a preamble line, e.g.
    //   "preamble: SF9 BW250k cfo=+101.6k peak=28.3dB"
    // We use this as the per-packet SNR estimate for the message/node tables.
    private static readonly Regex PreamblePeakRegex = new(
        @"peak=(?<peak>-?\d+(?:\.\d+)?)dB", RegexOptions.Compiled);

    // Captures native airtime event lines such as:
    //   "Packet RX: 262ms"
    //   "Packet TX: 170ms"
    private static readonly Regex PacketDurationRegex = new(
        @"Packet\s+(?<dir>RX|TX):\s+(?<ms>\d+)ms", RegexOptions.Compiled);

    /// <summary>Peak-above-noise (dB) from the most recent preamble, applied as
    /// the SNR of the next decoded packet. NaN until a preamble is seen.</summary>
    private float _lastPreamblePeakDb = float.NaN;

    private readonly Queue<(DateTime Utc, int Ms, bool IsTx)> _airtimeSamples = new();
    private readonly object _airtimeSamplesLock = new();

    private void RecordAirtimeSample(int ms, bool isTx)
    {
        if (ms <= 0) return;
        var now = DateTime.UtcNow;
        lock (_airtimeSamplesLock)
        {
            _airtimeSamples.Enqueue((now, ms, isTx));
            TrimAirtimeSamplesLocked(now);
        }
    }

    private static int EstimatePacketAirtimeMs(LoraPreset preset, int payloadBytes)
    {
        if (payloadBytes <= 0)
            return 0;

        var p = LoraParamsHelper.FromPreset(preset);
        double sf = p.Sf;
        double bwHz = p.BwKhz * 1000.0;
        double cr = p.Cr - 4.0; // 5..8 -> 1..4
        if (bwHz <= 0.0 || cr < 1.0)
            return 0;

        double tSym = Math.Pow(2.0, sf) / bwHz;
        int de = tSym >= 0.016 ? 1 : 0; // LDRO when symbol time >= 16 ms
        const int ih = 0; // explicit header
        const int crc = 1;

        double payloadNumerator = (8.0 * payloadBytes) - (4.0 * sf) + 28.0 + (16.0 * crc) - (20.0 * ih);
        double payloadDenominator = 4.0 * (sf - (2.0 * de));
        double payloadSym = 8.0;
        if (payloadDenominator > 0)
            payloadSym += Math.Max(Math.Ceiling(payloadNumerator / payloadDenominator) * (cr + 4.0), 0.0);

        const double preambleSym = 8.0 + 4.25;
        double toaSeconds = (preambleSym + payloadSym) * tSym;
        int ms = (int)Math.Round(toaSeconds * 1000.0, MidpointRounding.AwayFromZero);
        return Math.Max(ms, 1);
    }

    private void TrackAirtimeFromEvent(string ev)
    {
        // Legacy format from older native builds.
        var m = PacketDurationRegex.Match(ev);
        if (m.Success)
        {
            if (int.TryParse(m.Groups["ms"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var legacyMs) && legacyMs > 0)
            {
                bool isTx = string.Equals(m.Groups["dir"].Value, "TX", StringComparison.Ordinal);
                RecordAirtimeSample(legacyMs, isTx);
            }
            return;
        }

        // Current format includes payload length; infer RX airtime from length.
        var payload = PayloadLineRegex.Match(ev);
        if (!payload.Success) return;
        if (!int.TryParse(payload.Groups["len"].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var payloadLen) || payloadLen <= 0)
            return;

        RecordAirtimeSample(EstimatePacketAirtimeMs(SelectedPreset, payloadLen), isTx: false);
    }

    private void TrimAirtimeSamples(DateTime nowUtc)
    {
        lock (_airtimeSamplesLock)
            TrimAirtimeSamplesLocked(nowUtc);
    }

    private void TrimAirtimeSamplesLocked(DateTime nowUtc)
    {
        var maxAge = TimeSpan.FromHours(1);
        while (_airtimeSamples.Count > 0 &&
               nowUtc - _airtimeSamples.Peek().Utc > maxAge)
            _airtimeSamples.Dequeue();
    }

    // Meshtastic-style approximations from local counters:
    // - channel_utilization: RX+TX airtime over the last minute.
    // - air_util_tx: TX airtime over the last hour.
    private void ComputeLocalAirtimeUtilization(out float channelUtilPct, out float airUtilTxPct)
    {
        var now = DateTime.UtcNow;

        const double minuteMs = 60_000.0;
        const double hourMs = 3_600_000.0;

        double chanUsedMsMinute = 0;
        double txUsedMsHour = 0;

        lock (_airtimeSamplesLock)
        {
            TrimAirtimeSamplesLocked(now);
            foreach (var sample in _airtimeSamples)
            {
                var age = now - sample.Utc;
                if (age <= TimeSpan.FromMinutes(1))
                    chanUsedMsMinute += sample.Ms;
                if (sample.IsTx && age <= TimeSpan.FromHours(1))
                    txUsedMsHour += sample.Ms;
            }
        }

        channelUtilPct = (float)Math.Clamp((chanUsedMsMinute / minuteMs) * 100.0, 0.0, 100.0);
        airUtilTxPct = (float)Math.Clamp((txUsedMsHour / hourMs) * 100.0, 0.0, 100.0);
    }

    private void RefreshLiveUtilizationMetrics()
    {
        ComputeLocalAirtimeUtilization(out var channelUtilPct, out var airUtilTxPct);
        LiveChannelUtilizationPct = channelUtilPct;
        LiveAirUtilTxPct = airUtilTxPct;
    }

    private static void TryGetWindowsPowerTelemetry(out bool acOnline, out byte? batteryPct, out float? voltageV)
    {
        acOnline = false;
        batteryPct = null;
        voltageV = null;

        try
        {
            if (GetSystemPowerStatus(out var s))
            {
                acOnline = s.ACLineStatus == 1;
                // API returns 0..100 or 255 (unknown).
                if (s.BatteryLifePercent <= 100)
                    batteryPct = s.BatteryLifePercent;
            }
        }
        catch
        {
            // Best effort only.
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI", "SELECT Voltage FROM BatteryStatus");
            foreach (ManagementObject b in searcher.Get().OfType<ManagementObject>())
            {
                var raw = b["Voltage"];
                if (raw is null) continue;
                if (!uint.TryParse(raw.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var mv))
                    continue;
                if (mv < 1000 || mv > 20000) // sanity: 1.0V..20.0V
                    continue;
                voltageV = mv / 1000f;
                break;
            }
        }
        catch
        {
            // Some systems/users disable WMI battery classes.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    /// <summary>If payload recording is active and <paramref name="ev"/> is a
    /// decoded-payload event, append a structured JSON object in the recording array.</summary>
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

        MeshDecodeResult? decoded = null;
        if (crcOk)
        {
            var frame = HexToBytes(hex);
            if (frame.Length >= MeshHeader.Size && MeshHeader.TryParse(frame, out var header))
            {
                var channels = Channels.Select(c => c.Config).ToList();
                decoded = MeshDecoder.Decode(frame, channels);
                if (decoded is null && _myNodeNum != 0 &&
                    header.To == _myNodeNum && !header.IsBroadcast &&
                    header.ChannelHash == 0x00)
                {
                    decoded = TryDecodePkc(frame, header);
                }
            }
        }

        int.TryParse(len, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLen);
        var payloadRecord = new
        {
            time = ts,
            freq_mhz = CenterFreqMHz,
            preset = SelectedPreset.ToString(),
            status,
            crc_ok = crcOk,
            len = parsedLen,
            crc_rx = string.IsNullOrEmpty(rx) ? null : rx,
            crc_calc = string.IsNullOrEmpty(calc) ? null : calc,
            hex,
            decoded = BuildDecodedPayloadForRecord(decoded),
        };

        var json = JsonSerializer.Serialize(payloadRecord, PayloadRecordJsonOptions);

        try
        {
            if (_payloadJsonHasEntries)
                _payloadWriter.Write(',');
            _payloadWriter.WriteLine();
            _payloadWriter.Write("  ");
            _payloadWriter.Write(json);
            _payloadJsonHasEntries = true;
            _payloadCount++;
        }
        catch (Exception ex)
        {
            Log($"payload record write failed: {ex.Message}");
            StopPayloadRecording();
        }
    }

    private static object? BuildDecodedPayloadForRecord(MeshDecodeResult? decoded)
    {
        if (decoded is null) return null;

        var h = decoded.Header;
        return new
        {
            header = new
            {
                to = h.To,
                from = h.From,
                packet_id = h.PacketId,
                flags = h.Flags,
                channel_hash = h.ChannelHash,
                next_hop = h.NextHop,
                relay_node = h.RelayNode,
                hop_limit = h.HopLimit,
                want_ack = h.WantAck,
                via_mqtt = h.ViaMqtt,
                hop_start = h.HopStart,
                is_broadcast = h.IsBroadcast,
                from_id = h.FromId,
                to_id = h.ToId,
            },
            channel = decoded.ChannelName,
            port = decoded.Port.ToString(),
            text = decoded.Text,
            want_response = decoded.WantResponse,
            request_id = decoded.RequestId,
            reply_id = decoded.ReplyId,
            emoji = decoded.Emoji,
            ok_to_mqtt = decoded.OkToMqtt,
            routing_error = decoded.RoutingError,
            user = decoded.User,
            position = decoded.Position,
            waypoint = decoded.Waypoint,
            telemetry = decoded.Telemetry,
            route_discovery = decoded.RouteDiscovery,
            neighbor_info = decoded.NeighborInfo,
            app_payload_hex = BytesToHex(decoded.AppPayload),
        };
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
            // Decode our own frame to surface the ok_to_mqtt bitfield, so the
            // user can confirm the flag is actually present on the wire.
            var ownChannels = Channels.Select(c => c.Config).ToList();
            var own = MeshDecoder.Decode(frame, ownChannels);
            string mqttNote = own is not null
                ? $", ok_to_mqtt={(own.OkToMqtt ? "yes" : "no")}"
                : string.Empty;
            Log($"  tx confirmed (heard own packet id {header.PacketId:x8}{mqttNote})");
            return;
        }

        var rxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // SNR estimate captured from this frame's preamble (peak above noise).
        float? snrDb = float.IsNaN(_lastPreamblePeakDb) ? null : _lastPreamblePeakDb;
        _lastPreamblePeakDb = float.NaN; // consume it so it can't bleed to the next

        byte hopsAway = (byte)(header.HopStart >= header.HopLimit
            ? header.HopStart - header.HopLimit
            : 0);
        float? packetRssiDbm = float.IsNegativeInfinity(RssiDbfs) ? null : RssiDbfs;

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
            if (TryQueuePkcDecode(frame, header, rxEpoch, snrDb, packetRssiDbm, hopsAway))
                return;

            result = TryDecodePkc(frame, header);
        }

        ApplyDecodedPayloadResult(frame, header, result, rxEpoch, snrDb, packetRssiDbm, hopsAway);
    }

    private bool TryQueuePkcDecode(
        byte[] frame,
        MeshHeader header,
        long rxEpoch,
        float? snrDb,
        float? packetRssiDbm,
        byte hopsAway)
    {
        if (_pkcDecodeCts.IsCancellationRequested) return false;
        if (_myPrivateKeyBytes.Length != 32) return false;

        var senderPub = GetSenderPublicKeyBytes(header.From);
        if (senderPub.Length != 32) return false;

        var myPrivCopy = _myPrivateKeyBytes.ToArray();
        var senderPubCopy = senderPub.ToArray();

        return _pkcDecodeQueue.Writer.TryWrite(new PkcDecodeWorkItem(
            frame,
            header,
            rxEpoch,
            snrDb,
            packetRssiDbm,
            hopsAway,
            myPrivCopy,
            senderPubCopy));
    }

    private async Task RunPkcDecodeWorkerAsync()
    {
        try
        {
            var reader = _pkcDecodeQueue.Reader;
            while (await reader.WaitToReadAsync(_pkcDecodeCts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    MeshDecodeResult? result = null;
                    try
                    {
                        result = MeshDecoder.DecodePkc(item.Frame, item.MyPrivateKey, item.SenderPublicKey);
                    }
                    catch
                    {
                        result = null;
                    }

                    if (_pkcDecodeCts.IsCancellationRequested)
                        return;

                    await _uiDispatcher.InvokeAsync(
                        () => ApplyDecodedPayloadResult(
                            item.Frame,
                            item.Header,
                            result,
                            item.RxEpoch,
                            item.SnrDb,
                            item.PacketRssiDbm,
                            item.HopsAway),
                        DispatcherPriority.Background);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private void EnqueueDbWrite(Action<NodeStore, WaypointStore> write)
    {
        if (_dbWriteCts.IsCancellationRequested) return;
        if (_dbWriteQueue.Writer.TryWrite(write))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _dbWriteQueue.Writer.WriteAsync(write, _dbWriteCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown path.
            }
        });
    }

    private async Task RunDbWriteWorkerAsync()
    {
        try
        {
            var reader = _dbWriteQueue.Reader;
            while (await reader.WaitToReadAsync(_dbWriteCts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var write))
                {
                    try
                    {
                        write(_dbWriteNodeStore, _dbWriteWaypointStore);
                    }
                    catch
                    {
                        // Best-effort: write failures should not block RX/UI.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void ApplyDecodedPayloadResult(
        byte[] frame,
        MeshHeader header,
        MeshDecodeResult? result,
        long rxEpoch,
        float? snrDb,
        float? packetRssiDbm,
        byte hopsAway)
    {
        Log($"  from={header.FromId}  to=!{header.To:x8}  id={header.PacketId:x8}  chanHash=0x{header.ChannelHash:X2}  hopLimit={header.HopLimit}  hopStart={header.HopStart}");

        bool nodeInfoRecord = result is { Port: PortNum.NodeInfo, User: not null } && result.AppPayload.Length != 0;

        // Always record the sender sighting (RSSI/last-heard), decoded or not.
        // NodeInfo records fold these fields into their own upsert below so a
        // key-bearing NodeInfo doesn't do two SQLite writes on the UI tick.
        if (!nodeInfoRecord)
        {
            EnqueueDbWrite((nodes, _) =>
                nodes.RecordSighting(header.From,
                    rssiDbm: packetRssiDbm,
                    snrDb: snrDb,
                    hopsAway: hopsAway,
                    seenViaMqtt: header.ViaMqtt));
        }

        uint normalizedReplyId = 0;
        if (result is not null && result.Port == PortNum.TextMessage)
            normalizedReplyId = ResolveReactionTargetId(result);

        bool isReactionRecord = false;
        if (result is not null && result.Port == PortNum.TextMessage)
            isReactionRecord = normalizedReplyId != 0
                && result.Emoji != 0;

        if (result is null)
        {
            if (!RememberUndecodedPacket(header))
            {
                CancelPendingRelay(header.From, header.PacketId);
                Log($"  (dup) rx undecoded from {header.FromId} pkt {header.PacketId:x8} (chan hash {header.ChannelHash:X2})");
                MarkNodeDirty(header.From);
                return;
            }

            RelayIfEligible(frame, header, result);
            Log($"  rx undecoded from {header.FromId} (chan hash {header.ChannelHash:X2})");
            MarkNodeDirty(header.From);
            return;
        }

        var record = new MessageRecord
        {
            PacketId = header.PacketId,
            FromNode = header.From,
            ToNode = header.To,
            PortNum = (int)result.Port,
            Channel = result.ChannelName,
            ReplyId = normalizedReplyId,
            Emoji = result.Emoji,
            IsReaction = isReactionRecord,
            Decrypted = true,
            ViaMqtt = header.ViaMqtt,
            RxEpoch = rxEpoch,
            RssiDbfs = float.IsNegativeInfinity(RssiDbfs) ? null : RssiDbfs,
            SnrDb = snrDb,
        };

        record.PayloadHex = BytesToHex(result.AppPayload);
        if (result.Port == PortNum.TextMessage)
            record.Text = result.Text ?? string.Empty;

        // Dedup: Meshtastic floods packets, so the same message arrives several
        // times (different relays). MessageStore.Add returns false for a packet
        // we've already stored — skip ALL UI updates for repeats so each unique
        // message shows exactly once, like the Meshtastic app.
        bool isNew;
        try { isNew = _messageStore.Add(record); }
        catch (Exception ex) { Log($"message store failed: {ex.Message}"); isNew = false; }

        if (!isNew)
        {
            CancelPendingRelay(header.From, header.PacketId);
            // Still refresh the sighting timestamp (done above), but don't echo.
            MarkNodeDirty(header.From);
            Log($"  (dup) {header.FromId} pkt {header.PacketId:x8}");
            return;
        }

        RelayIfEligible(frame, header, result);

        Messages.Insert(0, record);

        bool nodeChanged = false;
        var senderName = NodeDisplayName(header.From);
        bool senderIgnored = IsNodeIgnored(header.From);
        switch (result.Port)
            {
                case PortNum.TextMessage:
                    uint reactionTargetId = ResolveReactionTargetId(result);
                    bool isReaction = reactionTargetId != 0
                        && result.Emoji != 0;
                    bool isReplyLinkedNonReaction = reactionTargetId != 0 && !isReaction;
                    bool showInChat = ChatMessagePassesIgnoredFilter(header.From);
                    if (!showInChat)
                    {
                        if (_myNodeNum != 0 && !header.IsBroadcast && header.To == _myNodeNum)
                        {
                            Log(isReaction
                                ? $"  DM reaction from {senderName}: {ResolveReactionGlyph(result.Text, result.Emoji)}"
                                : isReplyLinkedNonReaction
                                    ? $"  DM reply from {senderName}: {record.Text}"
                                    : $"  DM from {senderName}: {record.Text}");
                            if (header.WantAck) SendAck(header, result);
                        }
                        else
                        {
                            Log(isReaction
                                ? $"  [{result.ChannelName}] {senderName} reacted {ResolveReactionGlyph(result.Text, result.Emoji)}"
                                : isReplyLinkedNonReaction
                                    ? $"  [{result.ChannelName}] {senderName} replied: {record.Text}"
                                    : $"  [{result.ChannelName}] {senderName}: {record.Text}");
                        }
                        break;
                    }

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
                        if (isReaction)
                        {
                            bool applied = TryApplyReaction(convo.Messages, reactionTargetId, result.Text, result.Emoji, header.From);
                            if (applied)
                            {
                                MarkTabNeedsAttention(convo);
                            }
                            else
                            {
                                convo.Add(BuildStandaloneReactionMessage(record));
                                MarkTabNeedsAttention(convo);
                            }
                            Log(applied
                                ? $"  DM reaction from {senderName}: {ResolveReactionGlyph(result.Text, result.Emoji)}"
                                : $"  DM reaction from {senderName}: target id {reactionTargetId:x8} not found");
                        }
                        else if (isReplyLinkedNonReaction)
                        {
                            if (existed)
                                convo.Add(BuildReplyLinkedMessage(record, convo.Messages));
                            MarkTabNeedsAttention(convo);
                            Log($"  DM reply from {senderName}: {record.Text}");
                        }
                        else if (existed)
                            convo.Add(new ChannelMessage
                            {
                                FromId = senderName,
                                SenderNodeNum = header.From,
                                Text = record.Text,
                                RssiDbm = record.RssiDbfs,
                                SnrDb = record.SnrDb,
                                PacketId = header.PacketId,
                                IsIgnoredSender = senderIgnored,
                            });
                        if (!isReaction && !isReplyLinkedNonReaction)
                        {
                            MarkTabNeedsAttention(convo);
                            Log($"  DM from {senderName}: {record.Text}");
                        }
                        // Acknowledge if the sender asked for one (firmware does
                        // this for any unicast packet addressed to it).
                        if (header.WantAck) SendAck(header, result);
                        if (!senderIgnored && !IsNodeRtttlMuted(header.From))
                        {
                            var rtttl = RingtoneRtttl; var mode = ParseRingtoneMode(RingtoneMode); var vol = RingtoneVolume / 100.0;
                            Task.Run(() => _ringtone.Play(rtttl, mode, vol));
                        }
                    }
                    else
                    {
                        // Broadcast text → populate the owning channel tab like a chat room.
                        var chanVm = ResolveChannelTab(result.ChannelName);
                        bool shouldRing = false;
                        if (isReaction)
                        {
                            bool applied = chanVm is not null
                                && TryApplyReaction(chanVm.Messages, reactionTargetId, result.Text, result.Emoji, header.From);
                            if (applied)
                            {
                                MarkTabNeedsAttention(chanVm);
                            }
                            else if (chanVm is not null)
                            {
                                chanVm.Messages.Add(BuildStandaloneReactionMessage(record));
                                if (chanVm.Messages.Count > 1000)
                                    chanVm.Messages.RemoveAt(0);
                                MarkTabNeedsAttention(chanVm);
                            }
                            Log(applied
                                ? $"  [{result.ChannelName}] {senderName} reacted {ResolveReactionGlyph(result.Text, result.Emoji)}"
                                : $"  [{result.ChannelName}] {senderName} reaction target {reactionTargetId:x8} not found");
                            shouldRing = chanVm?.MuteRtttl != true;
                        }
                        else if (isReplyLinkedNonReaction)
                        {
                            if (chanVm is not null)
                            {
                                chanVm.Messages.Add(BuildReplyLinkedMessage(record, chanVm.Messages));
                                if (chanVm.Messages.Count > 1000)
                                    chanVm.Messages.RemoveAt(0);
                                MarkTabNeedsAttention(chanVm);
                            }
                            Log($"  [{result.ChannelName}] {senderName} replied: {record.Text}");
                            shouldRing = chanVm?.MuteRtttl != true;
                        }
                        else
                        {
                            chanVm?.Messages.Add(new ChannelMessage
                            {
                                FromId = senderName,
                                SenderNodeNum = header.From,
                                Text = record.Text,
                                RssiDbm = record.RssiDbfs,
                                SnrDb = record.SnrDb,
                                PacketId = header.PacketId,
                                IsIgnoredSender = senderIgnored,
                            });
                            MarkTabNeedsAttention(chanVm);
                            if (chanVm is not null && chanVm.Messages.Count > 1000)
                                chanVm.Messages.RemoveAt(0);
                            Log($"  [{result.ChannelName}] {senderName}: {record.Text}");
                            shouldRing = !senderIgnored && chanVm?.MuteRtttl != true;
                        }

                        if (shouldRing && !senderIgnored && !IsNodeRtttlMuted(header.From))
                        {
                            var rtttl = RingtoneRtttl; var mode = ParseRingtoneMode(RingtoneMode); var vol = RingtoneVolume / 100.0;
                            Task.Run(() => _ringtone.Play(rtttl, mode, vol));
                        }
                    }
                    break;
                case PortNum.Routing:
                    // ACK / NAK for one of our sent messages.
                    HandleRouting(header, result);
                    break;
                case PortNum.NodeInfo when result.User is not null:
                    // An empty NodeInfo payload with want_response is a pure
                    // *request* (no User content), not an advertisement — reply
                    // with ours instead of overwriting the node with blanks.
                    if (result.AppPayload.Length == 0)
                    {
                        if (result.WantResponse && _myNodeNum != 0 &&
                            header.To == _myNodeNum && !header.IsBroadcast)
                        {
                            Log($"  NodeInfo requested by {senderName} — replying");
                            RequestNodeInfoReply(header.From, FindChannelByName(result.ChannelName));
                        }
                        break;
                    }
                    nodeChanged = true;
                    {
                        string newKeyHex = result.User.PublicKey.Length == 32
                            ? Convert.ToHexString(result.User.PublicKey)
                            : string.Empty;
                        var existingNode = _nodesByNum.GetValueOrDefault(header.From)
                            ?? _nodeStore.Get(header.From);
                        // A mismatch is a NEW non-empty key that differs from a
                        // key we already trust. We keep the old key (don't
                        // silently accept a substitution) and flag it red until
                        // the user explicitly requests new keys.
                        bool keyMismatch = newKeyHex.Length > 0
                            && !string.IsNullOrEmpty(existingNode?.PublicKey)
                            && !string.Equals(existingNode!.PublicKey, newKeyHex,
                                               StringComparison.OrdinalIgnoreCase);

                        var nodeInfoUpsert = new NodeRecord
                        {
                            NodeNum = header.From,
                            UserId = string.IsNullOrEmpty(result.User.Id) ? header.FromId : result.User.Id,
                            LongName = result.User.LongName,
                            ShortName = result.User.ShortName,
                            HwModel = HardwareModels.Name(result.User.HwModel),
                            // If field 7 was absent from the wire, proto3 default = CLIENT.
                            // Store "Client" so we can distinguish "received NodeInfo but
                            // role is Client" from "never received NodeInfo at all" (blank).
                            Role = string.IsNullOrEmpty(result.User.Role) ? "Client" : result.User.Role,
                            // Empty on mismatch keeps the previously trusted key.
                            PublicKey = keyMismatch ? string.Empty : newKeyHex,
                            // Only touch the flag when this NodeInfo carried a key.
                            KeyMismatch = newKeyHex.Length > 0 ? keyMismatch : (bool?)null,
                            LastHeardEpoch = rxEpoch,
                            SeenViaMqtt = header.ViaMqtt,
                            RssiDbm = packetRssiDbm,
                            SnrDb = snrDb,
                            HopsAway = hopsAway,
                        };
                        EnqueueDbWrite((nodes, _) => nodes.Upsert(nodeInfoUpsert));

                        _pkcSenderPublicKeyBytes.Remove(header.From);

                        if (keyMismatch)
                            Log($"  nodeinfo {header.FromId}: KEY MISMATCH — public key changed; "
                                + "keeping the old key. Right-click the node → Request new keys to accept it.");
                        else
                            Log($"  nodeinfo {header.FromId}: {result.User.LongName} ({result.User.ShortName})"
                                + (string.IsNullOrEmpty(result.User.Role) ? string.Empty : $" role={result.User.Role}")
                                + (newKeyHex.Length > 0 ? " [PKC key]" : string.Empty));
                    }
                    // If they directed a NodeInfo request at us (want_response),
                    // reply with ours so they learn our public key. Firmware does
                    // the same — this is how PKC key exchange bootstraps.
                    if (result.WantResponse && _myNodeNum != 0 &&
                        header.To == _myNodeNum && !header.IsBroadcast)
                    {
                        Log($"  NodeInfo requested by {senderName} — replying");
                        RequestNodeInfoReply(header.From, FindChannelByName(result.ChannelName));
                    }
                    break;
                case PortNum.Position when result.Position is not null:
                    bool directedPositionResponseRequest = result.WantResponse
                        && _myNodeNum != 0
                        && header.To == _myNodeNum
                        && !header.IsBroadcast;
                    // A position *request* carries want_response=true and has no
                    // real coordinates (lat/lon both 0 — the payload only contains
                    // a timestamp field). Distinguish it from a real position report
                    // so we don't store a bogus 0,0 fix. A genuine report at exactly
                    // 0,0 (Gulf of Guinea) would not have want_response set.
                    if (result.WantResponse &&
                        result.Position.Latitude == 0 && result.Position.Longitude == 0)
                    {
                        if (directedPositionResponseRequest)
                        {
                            Log($"  position requested by {senderName} — replying");
                            ReplyWithPosition(header.From,
                                requestId: result.RequestId != 0 ? result.RequestId : header.PacketId,
                                channel: FindChannelByName(result.ChannelName));
                        }
                        else
                        {
                            string target = header.IsBroadcast ? "broadcast" : $"for {header.ToId}";
                            Log($"  position request from {senderName} ({target})");
                        }
                        break;
                    }
                    nodeChanged = true;
                    var existingPositionNode = _nodesByNum.GetValueOrDefault(header.From)
                        ?? _nodeStore.Get(header.From);
                    bool positionChanged = existingPositionNode?.Latitude is not double oldLat
                        || existingPositionNode.Longitude is not double oldLon
                        || Math.Abs(oldLat - result.Position.Latitude) > 1e-7
                        || Math.Abs(oldLon - result.Position.Longitude) > 1e-7
                        || existingPositionNode.AltitudeM != result.Position.AltitudeM;
                    if (positionChanged)
                    {
                        // Sighting telemetry (last-heard/RSSI/SNR) was already
                        // recorded earlier for this packet; only persist
                        // coordinates when they actually changed.
                        var positionUpsert = new NodeRecord
                        {
                            NodeNum = header.From,
                            Latitude = result.Position.Latitude,
                            Longitude = result.Position.Longitude,
                            AltitudeM = result.Position.AltitudeM,
                        };
                        var positionTimestamp = DateTimeOffset.FromUnixTimeSeconds(rxEpoch).UtcDateTime;
                        EnqueueDbWrite((nodes, _) =>
                        {
                            nodes.Upsert(positionUpsert);
                            nodes.AddLocationHistory(
                                header.From,
                                positionTimestamp,
                                result.Position.Latitude,
                                result.Position.Longitude,
                                result.Position.AltitudeM);
                        });
                    }
                    if (positionChanged)
                        _nodeLocationHistoryCounts[header.From] = _nodeLocationHistoryCounts.TryGetValue(header.From, out int count)
                            ? count + 1
                            : 1;
                    if (positionChanged)
                        Log($"  position {header.FromId}: {result.Position.Latitude:F5}, {result.Position.Longitude:F5}");
                    else
                        Log($"  position {header.FromId}: unchanged ({result.Position.Latitude:F5}, {result.Position.Longitude:F5})");
                    // Android's "exchange position" can include coordinates while
                    // still setting want_response on a directed packet.
                    if (directedPositionResponseRequest)
                    {
                        Log($"  position exchange requested by {senderName} — replying");
                        ReplyWithPosition(header.From,
                            requestId: result.RequestId != 0 ? result.RequestId : header.PacketId,
                            channel: FindChannelByName(result.ChannelName));
                    }
                    break;
                case PortNum.Waypoint when result.Waypoint is not null:
                    {
                        var wp = result.Waypoint;
                        // Some senders omit waypoint id (0). Use packet id as
                        // a stable fallback key per sender.
                        uint waypointId = wp.Id != 0 ? wp.Id : header.PacketId;
                        var waypointRecord = new WaypointRecord
                        {
                            FromNode = header.From,
                            WaypointId = waypointId,
                            PacketId = header.PacketId,
                            Channel = result.ChannelName,
                            Name = wp.Name,
                            Description = wp.Description,
                            Icon = wp.Icon,
                            Latitude = wp.Latitude,
                            Longitude = wp.Longitude,
                            ExpireEpoch = wp.ExpireEpoch,
                            LockedTo = wp.LockedTo,
                            RxEpoch = rxEpoch,
                        };
                        EnqueueDbWrite((_, waypoints) => waypoints.Upsert(waypointRecord));
                        _waypointsDirty = true;
                        Log($"  waypoint {header.FromId}: {wp.Latitude:F5}, {wp.Longitude:F5}  {wp.Name}");
                    }
                    break;
                case PortNum.Telemetry when result.Telemetry is not null:
                    if (result.WantResponse && _myNodeNum != 0 &&
                        header.To == _myNodeNum && !header.IsBroadcast &&
                        IsDeviceTelemetryRequest(result.AppPayload))
                    {
                        Log($"  telemetry requested by {senderName} — replying");
                        ReplyWithTelemetry(header.From,
                            requestId: result.RequestId != 0 ? result.RequestId : header.PacketId,
                            channel: FindChannelByName(result.ChannelName));
                        break;
                    }
                    nodeChanged = true;
                    var t = result.Telemetry;
                    var telemetryUpsert = new NodeRecord
                    {
                        NodeNum = header.From,
                        LastHeardEpoch = rxEpoch,
                        SeenViaMqtt = header.ViaMqtt,
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
                    };
                    EnqueueDbWrite((nodes, _) => nodes.Upsert(telemetryUpsert));
                    PersistTelemetryHistory(header.From, rxEpoch, t);
                    if (t.HasEnvironmentMetrics)
                    {
                        var temp = t.TemperatureC is float tempC ? FormatTemperature(tempC) : "n/a";
                        var pressure = t.BarometricPressureHpa is float pressureHpa ? FormatPressure(pressureHpa) : "n/a";
                        Log($"  telemetry {header.FromId}: {temp} {t.RelativeHumidityPct:F0}% {pressure}");
                    }
                    else
                        Log($"  telemetry {header.FromId}: batt {t.BatteryLevel}% {t.Voltage:F2}V");
                    break;
                case PortNum.Traceroute:
                    HandleTraceroute(header, result, snrDb);
                    break;
                case PortNum.NeighborInfo when result.NeighborInfo is not null:
                    HandleNeighborInfo(header, result.NeighborInfo);
                    break;
                case PortNum.StoreForward when result.StoreForward is not null:
                    LogStoreForward(header, result.StoreForward);
                    break;
                default:
                    Log($"  [{result.ChannelName}] {header.FromId} {result.Port} ({result.AppPayload.Length} B)");
                    break;
            }

        MarkNodeDirty(header.From);
        if (nodeChanged) { /* names refreshed on the next dirty-node apply */ }
    }

    private void PersistTelemetryHistory(uint nodeNum, long rxEpoch, MeshTelemetry telemetry)
    {
        if (!telemetry.HasDeviceMetrics && !telemetry.HasEnvironmentMetrics)
            return;

        string kind = telemetry switch
        {
            { HasDeviceMetrics: true, HasEnvironmentMetrics: true } => "DE",
            { HasDeviceMetrics: true } => "D",
            { HasEnvironmentMetrics: true } => "E",
            _ => string.Empty,
        };
        string signature = BuildTelemetryHistorySignature(telemetry);

        DateTime timestampUtc = rxEpoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds(rxEpoch).UtcDateTime
            : DateTime.UtcNow;

        var record = new NodeTelemetryHistoryRecord(
            0,
            nodeNum,
            timestampUtc,
            telemetry.HasDeviceMetrics ? telemetry.BatteryLevel : null,
            telemetry.HasDeviceMetrics ? telemetry.Voltage : null,
            telemetry.HasDeviceMetrics ? telemetry.ChannelUtilization : null,
            telemetry.HasDeviceMetrics ? telemetry.AirUtilTx : null,
            telemetry.HasDeviceMetrics ? telemetry.UptimeSeconds : null,
            telemetry.HasEnvironmentMetrics ? telemetry.TemperatureC : null,
            telemetry.HasEnvironmentMetrics ? telemetry.RelativeHumidityPct : null,
            telemetry.HasEnvironmentMetrics ? telemetry.BarometricPressureHpa : null,
            telemetry.HasEnvironmentMetrics ? telemetry.GasResistanceMohm : null,
            telemetry.HasEnvironmentMetrics ? telemetry.Iaq : null,
            signature);

        EnqueueDbWrite((nodes, waypoints) =>
        {
            var lastSignature = nodes.LatestTelemetrySignature(nodeNum, kind);
            if (string.Equals(lastSignature, signature, StringComparison.Ordinal))
                return;

            long id = nodes.AddTelemetryHistory(record);
            var withId = record with { Id = id };
            _ = _uiDispatcher.InvokeAsync(() =>
            {
                foreach (var convo in Tabs.OfType<ConversationViewModel>())
                    if (convo.NodeNum == nodeNum)
                        convo.AppendTelemetryHistoryRecord(withId);
            }, DispatcherPriority.Background);
        });
    }

    private static string BuildTelemetryHistorySignature(MeshTelemetry telemetry)
    {
        string kind = telemetry switch
        {
            { HasDeviceMetrics: true, HasEnvironmentMetrics: true } => "DE",
            { HasDeviceMetrics: true } => "D",
            { HasEnvironmentMetrics: true } => "E",
            _ => string.Empty,
        };

        return string.Join("|",
            kind,
            telemetry.HasDeviceMetrics ? FormatTelemetrySignatureValue(telemetry.BatteryLevel) : string.Empty,
            telemetry.HasDeviceMetrics ? FormatTelemetrySignatureValue(telemetry.Voltage) : string.Empty,
            telemetry.HasDeviceMetrics ? FormatTelemetrySignatureValue(telemetry.ChannelUtilization) : string.Empty,
            telemetry.HasDeviceMetrics ? FormatTelemetrySignatureValue(telemetry.AirUtilTx) : string.Empty,
            telemetry.HasDeviceMetrics ? FormatTelemetrySignatureValue(telemetry.UptimeSeconds) : string.Empty,
            telemetry.HasEnvironmentMetrics ? FormatTelemetrySignatureValue(telemetry.TemperatureC) : string.Empty,
            telemetry.HasEnvironmentMetrics ? FormatTelemetrySignatureValue(telemetry.RelativeHumidityPct) : string.Empty,
            telemetry.HasEnvironmentMetrics ? FormatTelemetrySignatureValue(telemetry.BarometricPressureHpa) : string.Empty,
            telemetry.HasEnvironmentMetrics ? FormatTelemetrySignatureValue(telemetry.GasResistanceMohm) : string.Empty,
            telemetry.HasEnvironmentMetrics ? FormatTelemetrySignatureValue(telemetry.Iaq) : string.Empty);
    }

    private static bool SameTelemetryHistoryKind(string? left, string right)
    {
        static string Kind(string? signature)
        {
            if (string.IsNullOrWhiteSpace(signature)) return string.Empty;
            int separator = signature.IndexOf('|');
            return separator < 0 ? signature : signature[..separator];
        }

        return string.Equals(Kind(left), Kind(right), StringComparison.Ordinal);
    }

    private static string FormatTelemetrySignatureValue<T>(T? value)
        where T : struct, IFormattable =>
        value.HasValue ? value.Value.ToString(null, CultureInfo.InvariantCulture) : string.Empty;

    private void RelayIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result)
    {
        if (!RoutingRelayEnabled) return;
        if (!CanTransmit || _myNodeNum == 0) return;
        if (header.From == _myNodeNum) return;
        if (IsNodeIgnored(header.From)) return;
        if (header.To == _myNodeNum) return;
        if (header.PacketId == 0) return;
        if (header.HopLimit == 0) return;
        if (!IsRoutingRoleEnabled(MyRole)) return;

        byte myRelayByte = (byte)(_myNodeNum & 0xFF);
        if (header.NextHop != 0 && header.NextHop != myRelayByte) return;
        if (!PassesRebroadcastPolicy(header, result)) return;

        bool decrement = ShouldDecrementHopLimit(header);
        byte nextHopLimit = decrement
            ? (byte)Math.Max(0, header.HopLimit - 1)
            : header.HopLimit;
        var relayFrame = (byte[])frame.Clone();
        relayFrame[12] = (byte)((relayFrame[12] & 0xF8) | (nextHopLimit & 0x07));
        relayFrame[14] = 0x00;
        relayFrame[15] = myRelayByte;

        var relayDelayMs = ComputeRelayDelayMs(header);
        ScheduleDelayedRelay(header, relayFrame, nextHopLimit, relayDelayMs);
    }

    /// <summary>
    /// Firmware-compatible hop decrement logic: ROUTER/ROUTER_LATE/CLIENT_BASE
    /// roles preserve hop_limit when the previous relay was a favorited router.
    /// First hop always decrements to prevent retry issues.
    /// </summary>
    private bool ShouldDecrementHopLimit(MeshHeader header)
    {
        // First hop must always decrement to prevent retry loops
        int hopsAway = header.HopStart >= header.HopLimit
            ? header.HopStart - header.HopLimit
            : 0;
        if (hopsAway == 0)
            return true;

        // Only router roles can preserve hops
        string role = (MyRole ?? string.Empty).Trim().ToUpperInvariant();
        if (role is not ("ROUTER" or "ROUTERLATE" or "CLIENTBASE"))
            return true;

        // Check if the previous relay is a favorited router node
        byte relayByte = header.RelayNode;
        if (relayByte == 0)
            return true;

        foreach (var node in _nodeStore.All())
        {
            if (!node.Favorite)
                continue;

            // Check if node's low byte matches the relay byte
            if ((node.NodeNum & 0xFF) == relayByte)
                return false;
        }

        return true;
    }

    private int ComputeRelayDelayMs(MeshHeader header)
    {
        string role = (MyRole ?? string.Empty).Trim().ToUpperInvariant();
        int minBase, maxBase;

        if (role == "ROUTER")
        {
            minBase = 80;
            maxBase = 160;
        }
        else if (role is "ROUTERLATE" or "CLIENTBASE")
        {
            minBase = 150;
            maxBase = 280;
        }
        else
        {
            minBase = 220;
            maxBase = 420;
        }

        int hopsAway = header.HopStart >= header.HopLimit
            ? header.HopStart - header.HopLimit
            : 0;
        int hopPenalty = Math.Min(200, hopsAway * 25);
        return Random.Shared.Next(minBase, maxBase + 1) + hopPenalty;
    }

    private static ulong RelayKey(uint from, uint packetId) => ((ulong)from << 32) | packetId;

    private void CancelPendingRelay(uint from, uint packetId)
    {
        if (packetId == 0) return;
        CancellationTokenSource? cts = null;
        lock (_relayScheduleLock)
        {
            var key = RelayKey(from, packetId);
            if (_pendingRelayCancels.TryGetValue(key, out cts))
                _pendingRelayCancels.Remove(key);
        }

        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch { }
            cts.Dispose();
            Log($"  relay canceled for duplicate packet {packetId:x8}");
        }
    }

    private void ScheduleDelayedRelay(MeshHeader header, byte[] relayFrame,
                                      byte nextHopLimit, int delayMs)
    {
        var key = RelayKey(header.From, header.PacketId);
        CancellationTokenSource cts;

        lock (_relayScheduleLock)
        {
            if (_pendingRelayCancels.ContainsKey(key))
                return;
            cts = new CancellationTokenSource();
            _pendingRelayCancels[key] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;

                var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
                TransmitBackground(SelectedPreset, hz, relayFrame, TxGainDb, TxAmpEnable);
                Log($"  relayed packet {header.PacketId:x8} ({header.HopLimit}->{nextHopLimit}) after {delayMs} ms mode={RebroadcastMode}");
            }
            catch (TaskCanceledException)
            {
                // canceled due to receiving a duplicate first
            }
            finally
            {
                lock (_relayScheduleLock)
                    _pendingRelayCancels.Remove(key);
                cts.Dispose();
            }
        });
    }

    private bool PassesRebroadcastPolicy(MeshHeader header, MeshDecodeResult? result)
    {
        string mode = EffectiveRebroadcastMode();
        return mode switch
        {
            "NONE" => false,
            "ALL" => true,
            "ALL_SKIP_DECODING" => true,
            // Firmware semantics: local mesh packets only (ignore foreign/undecryptable).
            "LOCAL_ONLY" => result is not null,
            // Firmware semantics: local mesh + sender must be known in node DB.
            "KNOWN_ONLY" => result is not null && _nodeStore.Get(header.From) is not null,
            "CORE_PORTNUMS_ONLY" => result is not null && IsCorePort(result.Port),
            _ => true,
        };
    }

    private string EffectiveRebroadcastMode()
    {
        string role = (MyRole ?? string.Empty).Trim().ToUpperInvariant();
        string mode = (RebroadcastMode ?? "ALL").Trim().ToUpperInvariant();

        // Firmware admin module coerces NONE for ROUTER/ROUTER_LATE to ALL.
        if (mode == "NONE" && (role == "ROUTER" || role == "ROUTERLATE"))
            return "ALL";

        // Firmware docs: ALL_SKIP_DECODING is repeater-only; other roles behave as ALL.
        if (mode == "ALL_SKIP_DECODING" && role != "REPEATER")
            return "ALL";

        return mode;
    }

    private static bool IsRoutingRoleEnabled(string? role)
    {
        string r = (role ?? string.Empty).Trim();
        // Firmware isRebroadcaster(): role != CLIENT_MUTE and rebroadcast_mode != NONE.
        return !r.Equals("ClientMute", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCorePort(PortNum port) => port switch
    {
        PortNum.TextMessage => true,
        PortNum.TextMessageCompressed => true,
        PortNum.Position => true,
        PortNum.NodeInfo => true,
        PortNum.Routing => true,
        PortNum.Telemetry => true,
        PortNum.Admin => true,
        PortNum.Alert => true,
        PortNum.KeyVerification => true,
        PortNum.StoreForward => true,
        PortNum.StoreForwardPlusPlus => true,
        PortNum.Traceroute => true,
        PortNum.Waypoint => true,
        _ => false,
    };

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
        KickAutoReportTick();
        RefreshLiveUtilizationMetrics();

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
        long rxDrainStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < MaxRxEventsPerTick; i++)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - rxDrainStart) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= MaxRxDrainMsPerTick)
                break;

            var ev = _core.PullEvent();
            if (ev is null) break;
            var nowUtc = DateTime.UtcNow;
            if (!IsHighRateDemodEvent(ev))
                Log(CompactDemodEventForUi(ev));
            TrackAirtimeFromEvent(ev);
            // A "preamble: ..." line marks the start of a received frame; grab
            // its peak-above-noise as the SNR for the payload that follows.
            if (ev.StartsWith("preamble", StringComparison.Ordinal))
            {
                MarkRxBusy(nowUtc, RxBusyDefaultHold);
                var pm = PreamblePeakRegex.Match(ev);
                if (pm.Success &&
                    float.TryParse(pm.Groups["peak"].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var pk))
                    _lastPreamblePeakDb = pk;
            }
            else if (ev.StartsWith("payload", StringComparison.Ordinal))
            {
                MarkRxFrameComplete(nowUtc);
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

        // Flush any node-list changes accumulated during the event drain in one
        // pass rather than once per packet (avoids Nodes.Clear + full DataGrid
        // rebind stutter on every received frame).
        if (_nodesDirty && !_suspendNodeReload)
        {
            // If node dirtiness was flagged without concrete ids, clear the
            // latch and wait for the next concrete update rather than forcing
            // a full reload (which can visibly flicker the node grid/map).
            if (_dirtyNodeNums.Count == 0)
                _nodesDirty = false;
            else
                _nodesDirty = ApplyDirtyNodeUpdates();
        }
        if (_waypointsDirty)
        {
            ReloadWaypoints();
            _waypointsDirty = false;
        }
    }

    private void KickAutoReportTick()
    {
        if (Interlocked.Exchange(ref _autoReportTickInFlight, 1) != 0)
            return;

        _ = RunAutoReportTickAsync();
    }

    private async Task RunAutoReportTickAsync()
    {
        try
        {
            var now = DateTime.UtcNow;

            if (AutoReportNodeInfoEnabled &&
                CanSendNodeInfo() &&
                now >= _nextAutoNodeInfoUtc)
            {
                _nextAutoNodeInfoUtc = now.AddSeconds(Math.Max(5, AutoReportNodeInfoSeconds));
                await SendNodeInfoAsync();
                if (Status.StartsWith("Sent node info", StringComparison.OrdinalIgnoreCase))
                {
                    _lastAutoNodeInfoUtc = now;
                    UpdateAutoReportLastSentSummary();
                }
            }

            now = DateTime.UtcNow;
            if (AutoReportPositionEnabled &&
                CanSendPosition() &&
                now >= _nextAutoPositionUtc)
            {
                _nextAutoPositionUtc = now.AddSeconds(Math.Max(5, AutoReportPositionSeconds));
                await SendPositionAsync();
                if (Status.StartsWith("Sent position", StringComparison.OrdinalIgnoreCase))
                {
                    _lastAutoPositionUtc = now;
                    UpdateAutoReportLastSentSummary();
                }
            }

            now = DateTime.UtcNow;
            if (AutoReportDeviceMetricsEnabled &&
                CanSendTelemetry() &&
                now >= _nextAutoDeviceMetricsUtc)
            {
                _nextAutoDeviceMetricsUtc = now.AddSeconds(Math.Max(5, AutoReportDeviceMetricsSeconds));
                await SendDeviceMetricsAsync();
                if (Status.StartsWith("Sent device metrics", StringComparison.OrdinalIgnoreCase))
                {
                    _lastAutoDeviceMetricsUtc = now;
                    UpdateAutoReportLastSentSummary();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _autoReportTickInFlight, 0);
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

    // Large payload hex strings are expensive to render in the live log and
    // can stall the UI at end-of-frame. Keep full payload capture in
    // RecordPayloadIfActive(ev) but compact what we display.
    private static string CompactDemodEventForUi(string ev)
    {
        if (!ev.StartsWith("payload", StringComparison.Ordinal))
            return ev;

        var m = PayloadLineRegex.Match(ev);
        if (!m.Success)
            return ev;

        string status = m.Groups["status"].Success ? m.Groups["status"].Value : "?";
        string hex = m.Groups["hex"].Success ? m.Groups["hex"].Value : string.Empty;
        int byteCount = hex.Length / 2;

        string preview = hex.Length <= 24
            ? hex
            : $"{hex.AsSpan(0, 12).ToString()}..{hex.AsSpan(hex.Length - 8).ToString()}";

        return $"payload {status} ({byteCount} B) {preview}";
    }

    // Preamble/payload lines arrive at high cadence and include large payload
    // strings; pushing each one through ObservableCollection->ListBox causes
    // measurable UI jank during bursts. Keep decode/recording paths intact and
    // suppress only these raw demod lines from the live log view.
    private static bool IsHighRateDemodEvent(string ev) =>
        ev.StartsWith("preamble", StringComparison.Ordinal) ||
        ev.StartsWith("payload", StringComparison.Ordinal);

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

    // ========== Filter Optimization: Parallel evaluation off UI thread ==========

    /// <summary>Cached filter criteria struct to avoid re-parsing on every node evaluation.</summary>
    private struct FilterCriteria
    {
        public string SearchText { get; set; }
        public int MaxHops { get; set; }
        public string KeyStatus { get; set; }
        public string LocationStatus { get; set; }
        public bool HideInvalidLocations { get; set; }
        public string IgnoredStatus { get; set; }
        public string MqttStatus { get; set; }
        public string TemperatureStatus { get; set; }
        public string HumidityStatus { get; set; }
        public string PressureStatus { get; set; }
        public double MaxDistanceKm { get; set; }
        public double HomeLatitude { get; set; }
        public double HomeLongitude { get; set; }
        public int MaxAgeMinutes { get; set; }

        public FilterCriteria()
        {
            SearchText = string.Empty;
            MaxHops = -1;
            KeyStatus = "Any";
            LocationStatus = "Any";
            HideInvalidLocations = false;
            IgnoredStatus = "Show all";
            MqttStatus = "Any";
            TemperatureStatus = "Any";
            HumidityStatus = "Any";
            PressureStatus = "Any";
            MaxDistanceKm = -1;
            HomeLatitude = 0;
            HomeLongitude = 0;
            MaxAgeMinutes = -1;
        }

        /// <summary>Precompute all filter values to avoid repeated parsing on each node evaluation.</summary>
        public static FilterCriteria Create(MainViewModel vm, Dictionary<uint, int> locationCounts)
        {
            var criteria = new FilterCriteria
            {
                SearchText = vm.NodeSearchText.Trim(),
                KeyStatus = vm.NodeKeyFilter,
                LocationStatus = vm.NodeLocationFilter,
                HideInvalidLocations = vm.HideInvalidNodeLocations,
                IgnoredStatus = vm.NodeIgnoredFilter,
                MqttStatus = vm.NodeMqttFilter,
                TemperatureStatus = vm.NodeTemperatureFilter,
                HumidityStatus = vm.NodeHumidityFilter,
                PressureStatus = vm.NodePressureFilter,
            };

            // Parse hops filter
            criteria.MaxHops = vm.NodeHopsFilter switch
            {
                "Direct" => 0,
                "≤1 hop" => 1,
                "≤2 hops" => 2,
                "≤3 hops" => 3,
                "≤4 hops" => 4,
                _ => -1,
            };

            // Parse distance filter
            if (!string.IsNullOrWhiteSpace(vm.NodeDistanceKmText)
                && double.TryParse(vm.NodeDistanceKmText, NumberStyles.Float,
                                   CultureInfo.CurrentCulture, out double maxDist)
                && maxDist > 0
                && vm.HomeLatitude is double hlat && vm.HomeLongitude is double hlon)
            {
                criteria.MaxDistanceKm = DisplayUnits.ConvertDistanceInputToKm(maxDist, vm.CurrentUnitSystem);
                criteria.HomeLatitude = hlat;
                criteria.HomeLongitude = hlon;
            }

            // Parse max age filter
            if (!string.IsNullOrWhiteSpace(vm.NodeMaxAgeMinutesText)
                && int.TryParse(vm.NodeMaxAgeMinutesText, out int maxAge) && maxAge > 0)
            {
                criteria.MaxAgeMinutes = maxAge;
            }

            return criteria;
        }

        public static FilterCriteria CreateEmpty() => new();
    }

    /// <summary>Called on debounce timer tick to apply batched filter changes.</summary>
    private void OnFilterChangeDebounceTimerTick(object? sender, EventArgs e)
    {
        _filterChangeDebounceTimer.Stop();
        
        // Compute new filter on background thread and then update UI on main thread
        _ = ComputeFilterCriteriaAsync();
    }

    /// <summary>Asynchronously computes which nodes pass the current filter criteria.</summary>
    private async Task ComputeFilterCriteriaAsync()
    {
        // Cancel any previous pending filter computation
        _filterComputeCts?.Cancel();
        _filterComputeCts = new CancellationTokenSource();
        var cts = _filterComputeCts;

        try
        {
            // Create filter criteria on UI thread
            var newCriteria = FilterCriteria.Create(this, _nodeLocationHistoryCounts);
            var nodesToTest = _nodesByNum.Values.ToList();

            // Compute filtered set on thread pool using Parallel.ForEach for parallelism
            var filteredSet = new HashSet<uint>();
            var lockObj = new object();

            await Task.Run(() =>
            {
                Parallel.ForEach(nodesToTest, new ParallelOptions
                {
                    CancellationToken = cts.Token,
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                }, node =>
                {
                    if (cts.Token.IsCancellationRequested)
                        return;

                    if (NodePassesFilterWithCriteria(node, newCriteria))
                    {
                        lock (lockObj)
                        {
                            filteredSet.Add(node.NodeNum);
                        }
                    }
                });
            }, cts.Token).ConfigureAwait(true);

            if (cts.Token.IsCancellationRequested)
                return;

            // Update cache on UI thread
            lock (_filterCriteriaSyncLock)
            {
                _currentFilterCriteria = newCriteria;
                _nodeFilterCache.Clear();
                foreach (var nodeNum in filteredSet)
                    _nodeFilterCache.Add(nodeNum);
            }

            // Refresh UI elements
            var uiDispatcher = Application.Current?.Dispatcher;
            if (uiDispatcher != null)
            {
                uiDispatcher.Invoke(() =>
                {
                    if (cts.Token.IsCancellationRequested)
                        return;

                    NodesView?.Refresh();
                    RebuildNodeMapStateSignaturesOptimized();
                    MapDataChanged?.Invoke(this, EventArgs.Empty);
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
            // Filter computation was canceled, which is fine
        }
    }

    /// <summary>Debounced version of RefreshNodesFilter that batches multiple filter changes.</summary>
    private void RefreshNodesFilter()
    {
        if (!_filterChangeDebounceTimer.IsEnabled)
            _filterChangeDebounceTimer.Start();
    }

    /// <summary>Optimized filter evaluation using cached criteria to avoid repeated parsing.</summary>
    private bool NodePassesFilterWithCriteria(NodeRecord n, FilterCriteria criteria)
    {
        // Text search across long name, short name, user ID.
        if (!string.IsNullOrWhiteSpace(criteria.SearchText))
        {
            if (!n.LongName.Contains(criteria.SearchText, StringComparison.OrdinalIgnoreCase)
             && !n.ShortName.Contains(criteria.SearchText, StringComparison.OrdinalIgnoreCase)
             && !n.DisplayId.Contains(criteria.SearchText, StringComparison.OrdinalIgnoreCase)
             && !n.UserId.Contains(criteria.SearchText, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Hops filter.
        if (criteria.MaxHops >= 0)
        {
            if (n.HopsAway is not byte h || h > criteria.MaxHops)
                return false;
        }

        // Key status filter.
        switch (criteria.KeyStatus)
        {
            case "Good key": if (!n.HasPublicKey || n.HasKeyMismatch) return false; break;
            case "Mismatch": if (!n.HasKeyMismatch) return false; break;
            case "No key": if (n.HasPublicKey) return false; break;
        }

        // Location filter.
        bool hasPos = n.HasLocation;
        bool hasPosHistory = _nodeLocationHistoryCounts.TryGetValue(n.NodeNum, out int historyCount)
            && historyCount > 1;
        switch (criteria.LocationStatus)
        {
            case "Has position": if (!hasPos) return false; break;
            case "Has position history (>1)": if (!hasPosHistory) return false; break;
            case "No position": if (hasPos) return false; break;
        }

        if (criteria.HideInvalidLocations && n.HasInvalidLocation)
            return false;

        // Ignored filter.
        switch (criteria.IgnoredStatus)
        {
            case "Hide ignored": if (n.Ignored) return false; break;
            case "Only ignored": if (!n.Ignored) return false; break;
        }

        // MQTT filter.
        switch (criteria.MqttStatus)
        {
            case "Hide via MQTT": if (n.SeenViaMqtt) return false; break;
            case "Only via MQTT": if (!n.SeenViaMqtt) return false; break;
        }

        // Telemetry presence filters.
        switch (criteria.TemperatureStatus)
        {
            case "Has value": if (n.TemperatureC is null) return false; break;
            case "No value": if (n.TemperatureC is not null) return false; break;
        }
        switch (criteria.HumidityStatus)
        {
            case "Has value": if (n.RelativeHumidityPct is null) return false; break;
            case "No value": if (n.RelativeHumidityPct is not null) return false; break;
        }
        switch (criteria.PressureStatus)
        {
            case "Has value": if (n.BarometricPressureHpa is null) return false; break;
            case "No value": if (n.BarometricPressureHpa is not null) return false; break;
        }

        // Distance from home
        if (criteria.MaxDistanceKm > 0)
        {
            // Only apply the distance gate when this node has coordinates.
            // Nodes with unknown position should remain visible.
            if (n.Latitude is double nlat && n.Longitude is double nlon)
            {
                if (HaversineKm(criteria.HomeLatitude, criteria.HomeLongitude, nlat, nlon) > criteria.MaxDistanceKm)
                    return false;
            }
        }

        // Max age (minutes since last heard)
        if (criteria.MaxAgeMinutes > 0)
        {
            if (n.LastHeardEpoch == 0) return false;
            double ageMin = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - n.LastHeardEpoch) / 60.0;
            if (ageMin > criteria.MaxAgeMinutes) return false;
        }

        return true;
    }

    /// <summary>Optimized RebuildNodeMapStateSignatures that only updates changed nodes.</summary>
    private void RebuildNodeMapStateSignaturesOptimized()
    {
        lock (_filterCriteriaSyncLock)
        {
            foreach (var n in _nodesByNum.Values)
                UpdateNodeMapStateSignature(n.NodeNum, n);
        }
    }

    /// <summary>
    /// Filter predicate used by the node grid/map. Prefer the precomputed cache,
    /// but fall back to a live criteria check on cache misses so newly
    /// discovered/updated nodes can appear immediately even if the async cache
    /// recompute has not completed yet.
    /// </summary>
    private bool NodePassesFilter(NodeRecord n)
    {
        lock (_filterCriteriaSyncLock)
        {
            if (_nodeFilterCache.Contains(n.NodeNum))
                return true;

            return NodePassesFilterWithCriteria(n, _currentFilterCriteria);
        }
    }

    public void Dispose()
    {
        StopPayloadRecording();
        _dbWriteQueue.Writer.TryComplete();
        _dbWriteCts.Cancel();
        try { _dbWriteWorkerTask.Wait(200); } catch { }
        _dbWriteCts.Dispose();
        _pkcDecodeQueue.Writer.TryComplete();
        _pkcDecodeCts.Cancel();
        _pkcDecodeCts.Dispose();
        _filterComputeCts?.Cancel();
        _filterChangeDebounceTimer?.Stop();
        _gpsService.Stop();
        _gpsService.StatusChanged -= HandleGpsStatusChanged;
        _gpsService.FixReceived -= HandleGpsFixReceived;
        _gpsService.Dispose();
        _core.Dispose();
        _dbWriteNodeStore.Dispose();
        _dbWriteWaypointStore.Dispose();
        _nodeStore.Dispose();
        _waypointStore.Dispose();
        _channelStore.Dispose();
        _messageStore.Dispose();
        _ringtone.Dispose();
    }
}
