// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.App.Audio;
using MeshRF.App.Location;
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
    private bool _settingsLoaded;
    private double? _manualHomeLatitude;
    private double? _manualHomeLongitude;
    private int?    _manualHomeAltitude;

    // Serializes all transmit calls so concurrent sends (user + auto-reply)
    // don't race on the shared native Core handle.
    private readonly SemaphoreSlim _txSemaphore = new(1, 1);

    // Set when a received packet updates node state; consumed by the 20 Hz
    // timer tick so ReloadNodes() runs at most once per tick rather than once
    // per received packet (avoids stutter from Nodes.Clear + full rebind).
    private bool _nodesDirty;
    private bool _waypointsDirty;
    private bool _suspendNodeReload;
    private readonly HashSet<uint> _dirtyNodeNums = new();
    private readonly Dictionary<uint, NodeRecord> _nodesByNum = new();
    private readonly Dictionary<uint, int> _nodeMapStateSignatures = new();
    private readonly Dictionary<uint, int> _nodeTooltipSignatures = new();
    private readonly Dictionary<uint, string> _nodeTooltipCache = new();
    private DateTime _lastNodesViewRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan NodesViewRefreshInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxDirtyNodeUpdatesPerTick = 64;
    private const int MaxRxEventsPerTick = 8;
    private bool _nodesViewRefreshPending;
    private readonly DispatcherTimer _nodesViewRefreshTimer;

    private const string ManualLocationSourceValue = "Manual";
    private const string UsbSerialLocationSourceValue = "UsbSerial";
    private const double HomeMapUpdateThresholdKm = 0.02; // 20 m

    // Plays the RTTTL ringtone when a text message arrives.
    private readonly RtttlPlayer _ringtone = new();
    // Payload recording: open StreamWriter when active. Each decoded payload is
    // appended as one JSON object (JSONL). Null when not recording.
    private StreamWriter? _payloadWriter;
    private int _payloadCount;

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
    private bool _useFahrenheit;

    [ObservableProperty]
    private bool _useMiles;

    public IReadOnlyList<string> Themes { get; } = new[] { "System", "Light", "Dark" };

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

    /// <summary>An entry in the home-location source selector.</summary>
    public sealed record LocationSourceOption(string Value, string Label);

    /// <summary>Selectable RX radio backends (HackRF / RTL-SDR / None).
    /// Populated at construction with an availability annotation.</summary>
    public IReadOnlyList<DeviceOption> DeviceOptions { get; private set; } =
        Array.Empty<DeviceOption>();

    public IReadOnlyList<DeviceOption> TxDeviceOptions { get; private set; } =
        Array.Empty<DeviceOption>();

    [ObservableProperty]
    private DeviceOption? _selectedDevice;

    [ObservableProperty]
    private DeviceOption? _selectedTxDevice;

    private bool _suppressDeviceUpdate;

    /// <summary>True when the selected RX backend is an RTL-SDR (drives which
    /// receiver controls the toolbar shows).</summary>
    public bool IsRtlSdr => SelectedDevice?.Kind == RadioDeviceKind.RtlSdr;

    /// <summary>True for everything that isn't an RTL-SDR; those use the
    /// HackRF-style LNA/VGA/AMP gain model.</summary>
    public bool IsHackRf => !IsRtlSdr;

    /// <summary>The device selectors are only editable while RX is stopped.</summary>
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
    [ObservableProperty] private string _nodeIgnoredFilter     = "Show all";
    [ObservableProperty] private string _nodeTemperatureFilter = "Any";
    [ObservableProperty] private string _nodeHumidityFilter    = "Any";
    [ObservableProperty] private string _nodePressureFilter    = "Any";
    [ObservableProperty] private string _mapNodeLabelMode      = "Short name";
    [ObservableProperty] private string _nodeDistanceKmText    = string.Empty;
    [ObservableProperty] private string _nodeMaxAgeMinutesText = string.Empty;

    public IReadOnlyList<string> NodeHopsFilterOptions     { get; } = ["Any", "Direct", "≤1 hop", "≤2 hops", "≤3 hops", "≤4 hops"];
    public IReadOnlyList<string> NodeKeyFilterOptions      { get; } = ["Any", "Good key", "Mismatch", "No key"];
    public IReadOnlyList<string> NodeLocationFilterOptions { get; } = ["Any", "Has position", "No position", "Invalid"];
    public IReadOnlyList<string> NodeIgnoredFilterOptions  { get; } = ["Show all", "Hide ignored", "Only ignored"];
    public IReadOnlyList<string> TelemetryHasFilterOptions { get; } = ["Any", "Has value", "No value"];
    public IReadOnlyList<string> MapNodeLabelModeOptions   { get; } =
        ["Node Number", "Long Name", "Short Name", "Temperature", "Humidity", "Pressure"];

    /// <summary>True when a home location is set (enables the distance filter).</summary>
    public bool HasHomeLocation => HomeLatitude.HasValue && HomeLongitude.HasValue;

    public string DistanceUnitShort => UseMiles ? "mi" : "km";
    public string DistanceUnitLong => UseMiles ? "miles" : "km";
    public string MaxDistanceFilterToolTip =>
        UseMiles
            ? "Max distance from location in miles (blank = any; requires location to be set)"
            : "Max distance from location in km (blank = any; requires location to be set)";

    private void RefreshNodesFilter()
    {
        NodesView?.Refresh();
        RebuildNodeMapStateSignatures();
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnNodeSearchTextChanged(string value)          => RefreshNodesFilter();
    partial void OnNodeHopsFilterChanged(string value)          => RefreshNodesFilter();
    partial void OnNodeKeyFilterChanged(string value)           => RefreshNodesFilter();
    partial void OnNodeLocationFilterChanged(string value)      => RefreshNodesFilter();
    partial void OnNodeIgnoredFilterChanged(string value)       => RefreshNodesFilter();
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
        NodeIgnoredFilter     = "Show all";
        NodeTemperatureFilter = "Any";
        NodeHumidityFilter    = "Any";
        NodePressureFilter    = "Any";
        NodeDistanceKmText    = string.Empty;
        NodeMaxAgeMinutesText = string.Empty;
    }

    private bool NodePassesFilter(NodeRecord n)
    {
        // Text search across long name, short name, user ID.
        if (!string.IsNullOrWhiteSpace(NodeSearchText))
        {
            var t = NodeSearchText.Trim();
            if (!n.LongName.Contains(t, StringComparison.OrdinalIgnoreCase)
             && !n.ShortName.Contains(t, StringComparison.OrdinalIgnoreCase)
             && !n.DisplayId.Contains(t, StringComparison.OrdinalIgnoreCase)
             && !n.UserId.Contains(t, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Hops filter.
        if (NodeHopsFilter != "Any")
        {
            int maxH = NodeHopsFilter switch
            {
                "Direct"  => 0,
                "≤1 hop"  => 1,
                "≤2 hops" => 2,
                "≤3 hops" => 3,
                "≤4 hops" => 4,
                _          => -1,
            };
            if (maxH >= 0 && (n.HopsAway is not byte h || h > maxH)) return false;
        }

        // Key status filter.
        switch (NodeKeyFilter)
        {
            case "Good key": if (!n.HasPublicKey || n.HasKeyMismatch) return false; break;
            case "Mismatch": if (!n.HasKeyMismatch) return false; break;
            case "No key":   if (n.HasPublicKey) return false; break;
        }

        // Location filter.
        bool hasPos = n.HasLocation;
        switch (NodeLocationFilter)
        {
            case "Has position": if (!hasPos) return false; break;
            case "No position":  if (hasPos)  return false; break;
            case "Invalid":      if (!n.HasInvalidLocation) return false; break;
        }

        // Ignored filter.
        switch (NodeIgnoredFilter)
        {
            case "Hide ignored": if (n.Ignored) return false; break;
            case "Only ignored": if (!n.Ignored) return false; break;
        }

        // Telemetry presence filters.
        switch (NodeTemperatureFilter)
        {
            case "Has value": if (n.TemperatureC is null) return false; break;
            case "No value":  if (n.TemperatureC is not null) return false; break;
        }
        switch (NodeHumidityFilter)
        {
            case "Has value": if (n.RelativeHumidityPct is null) return false; break;
            case "No value":  if (n.RelativeHumidityPct is not null) return false; break;
        }
        switch (NodePressureFilter)
        {
            case "Has value": if (n.BarometricPressureHpa is null) return false; break;
            case "No value":  if (n.BarometricPressureHpa is not null) return false; break;
        }

        // Distance from home (entered in km or miles based on user preference).
        if (!string.IsNullOrWhiteSpace(NodeDistanceKmText)
            && double.TryParse(NodeDistanceKmText, NumberStyles.Float, CultureInfo.CurrentCulture, out double maxDistance)
            && maxDistance > 0
            && HomeLatitude is double hlat && HomeLongitude is double hlon)
        {
            double maxKm = UseMiles ? maxDistance * 1.609344 : maxDistance;
            if (n.Latitude is not double nlat || n.Longitude is not double nlon) return false;
            if (HaversineKm(hlat, hlon, nlat, nlon) > maxKm) return false;
        }

        // Max age (minutes since last heard).
        if (!string.IsNullOrWhiteSpace(NodeMaxAgeMinutesText)
            && int.TryParse(NodeMaxAgeMinutesText, out int maxMin) && maxMin > 0)
        {
            if (n.LastHeardEpoch == 0) return false;
            double ageMin = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - n.LastHeardEpoch) / 60.0;
            if (ageMin > maxMin) return false;
        }

        return true;
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

    /// <summary>The selected tab when it is a channel (null for DM tabs).</summary>
    public ChannelViewModel? SelectedChannel => SelectedTab as ChannelViewModel;

    partial void OnSelectedTabChanged(object? value)
    {
        if (value is ITabItem tab)
            tab.TabNeedsAttention = false;

        OnPropertyChanged(nameof(SelectedChannel));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
    }

    private void MarkTabNeedsAttention(ITabItem? tab)
    {
        if (tab is null) return;
        if (ReferenceEquals(SelectedTab, tab)) return;
        tab.TabNeedsAttention = true;
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

    /// <summary>Default hop limit for transmitted packets (1..7). Mirrors the
    /// firmware LoRa config; broadcasts and DMs are sent with this many hops.</summary>
    [ObservableProperty] private int _hopLimit = 3;

    /// <summary>When set, transmitted packets flag <c>ok_to_mqtt</c> so gateways
    /// may uplink them to the public MQTT broker.</summary>
    [ObservableProperty] private bool _okToMqtt;

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
    [ObservableProperty] private string _waypointExpiryTimeText = "12:00:00";
    [ObservableProperty] private string _waypointNameInput = string.Empty;
    [ObservableProperty] private string _waypointDescriptionInput = string.Empty;

    public IReadOnlyList<string> WaypointEmojiOptions { get; } = new[]
    {
        "📍", "📌", "🏠", "⛺", "⚠", "🚧", "🛰", "🏴",
    };

    [RelayCommand]
    private void PickWaypointEmoji()
    {
        string? picked = EmojiPickerWindow.PickEmoji(Application.Current?.MainWindow);
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

    /// <summary>Location history paths requested by open conversation tabs.</summary>
    public IReadOnlyList<MapPolyline> GetMapPolylines()
    {
        var list = new List<MapPolyline>();

        foreach (var convo in Tabs.OfType<ConversationViewModel>())
        {
            if (!convo.ShowLocationHistoryOnMap || convo.LocationHistory.Count < 2)
                continue;

            var points = convo.LocationHistory
                .Select(p => (p.Latitude, p.Longitude))
                .ToList();
            if (points.Count < 2)
                continue;

            list.Add(new MapPolyline(
                string.IsNullOrWhiteSpace(convo.PeerName) ? convo.PeerId : convo.PeerName,
                points));
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
    public void RequestKeys(IEnumerable<MeshRF.Nodes.NodeRecord> nodes)
    {
        var targets = nodes?.Where(n => n is not null).ToList();
        if (targets is null || targets.Count == 0) return;

        foreach (var n in targets)
        {
            if (_myNodeNum != 0 && n.NodeNum == _myNodeNum) continue;
            _nodeStore.ClearPublicKey(n.NodeNum);
            uint packetId = NextPacketId();
            SendNodeInfoExchangeRequest(n.NodeNum, packetId);
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
    public void RequestNodeInfoOnly(IEnumerable<MeshRF.Nodes.NodeRecord> nodes)
    {
        var targets = nodes?.Where(n => n is not null).ToList();
        if (targets is null || targets.Count == 0) return;

        foreach (var n in targets)
        {
            if (_myNodeNum != 0 && n.NodeNum == _myNodeNum) continue;
            uint packetId = NextPacketId();
            SendNodeInfoRequestOnly(n.NodeNum, packetId);
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
    public void ExchangeNodeInfo(IEnumerable<MeshRF.Nodes.NodeRecord> nodes)
    {
        var targets = nodes?.Where(n => n is not null).ToList();
        if (targets is null || targets.Count == 0) return;

        foreach (var n in targets)
        {
            if (_myNodeNum != 0 && n.NodeNum == _myNodeNum) continue;
            uint packetId = NextPacketId();
            SendNodeInfoExchangeRequest(n.NodeNum, packetId);
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

            if (await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable))
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
    public async Task RequestPositionAsync(MeshRF.Nodes.NodeRecord? node)
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

        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null)
        {
            Status = "Position request needs a primary channel.";
            Log("  " + Status);
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodePositionRequest(
                primary.Config, _myNodeNum, node.NodeNum, packetId,
                hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            if (await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable))
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
    /// Exchange location with <paramref name="node"/> by requesting its
    /// position and also sending our current position directly to it.
    /// </summary>
    public async Task ExchangeLocationAsync(MeshRF.Nodes.NodeRecord? node)
    {
        if (node is null) return;
        await RequestPositionAsync(node);
        ReplyWithPosition(node.NodeNum);
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
        if (n.TemperatureC is float t) env.Add(FormatTemperature(t));
        if (n.RelativeHumidityPct is float h) env.Add($"{h:F0}% RH");
        if (n.BarometricPressureHpa is float p) env.Add($"{p:F0} hPa");
        if (env.Count > 0) sb.Append('\n').Append(string.Join("  ", env));

        // Last heard (relative).
        sb.Append("\nHeard ").Append(FormatAge(n.LastHeardEpoch));
        return sb.ToString();
    }

    private static string BuildWaypointTooltip(WaypointRecord wp)
    {
        var sb = new System.Text.StringBuilder();
                sb.Append(string.IsNullOrWhiteSpace(wp.IconText) ? wp.DisplayName : $"{wp.IconText} {wp.DisplayName}")
          .Append("\nFrom ").Append(wp.FromId)
          .Append("\n").Append(wp.Latitude.ToString("F5", CultureInfo.InvariantCulture))
          .Append(", ").Append(wp.Longitude.ToString("F5", CultureInfo.InvariantCulture));
        if (wp.AltitudeM is int alt) sb.Append("  ").Append(alt).Append(" m");
        if (!string.IsNullOrWhiteSpace(wp.Description))
            sb.Append("\n").Append(wp.Description);
        if (wp.LockedTo != 0)
            sb.Append("\nLocked to !").Append(wp.LockedTo.ToString("x8", CultureInfo.InvariantCulture));
        if (wp.ExpireEpoch != 0)
            sb.Append("\nExpires ").Append(DateTimeOffset.FromUnixTimeSeconds(wp.ExpireEpoch).LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
              .Append(wp.IsExpired ? "  [EXPIRED]" : string.Empty);
        sb.Append("\nReceived ").Append(FormatAge(wp.RxEpoch));
        return sb.ToString();
    }

    private static float CelsiusToFahrenheit(float celsius) => (celsius * 9f / 5f) + 32f;

    private string FormatTemperature(float temperatureC) =>
        UseFahrenheit
            ? $"{CelsiusToFahrenheit(temperatureC):F1} °F"
            : $"{temperatureC:F1} °C";

    private string GetMapNodeLabel(NodeRecord n) => MapNodeLabelMode switch
    {
        "Node Number" => n.DisplayId,
        "Long Name" => !string.IsNullOrWhiteSpace(n.LongName) ? n.LongName : n.DisplayId,
        "Short Name" => !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName : n.DisplayId,
        "Temperature" => n.TemperatureC is float t ? FormatTemperature(t) : n.DisplayId,
        "Humidity" => n.RelativeHumidityPct is float h ? $"{h:F0}%" : n.DisplayId,
        "Pressure" => n.BarometricPressureHpa is float p ? $"{p:F0} hPa" : n.DisplayId,
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
        _nodesViewRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = NodesViewRefreshInterval,
        };
        _nodesViewRefreshTimer.Tick += OnNodesViewRefreshTimerTick;

        _settings = AppSettings.Load();
        var soon = DateTime.Now.AddHours(1);
        WaypointExpiryTimeText = soon.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
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
        AgcEnable = _settings.AgcEnable;
        AgcTargetDbfs = _settings.AgcTargetDbfs;
        RtlGainDb = _settings.RtlGainDb;
        BiasTee = _settings.BiasTee;
        DcBlockEnable = _settings.DcBlockEnable;
        Theme = _settings.Theme;
        UseFahrenheit = _settings.UseFahrenheit;
        UseMiles = _settings.UseMiles;
        WaterfallColormap = _settings.WaterfallColormap;
        RingtoneMode = _settings.RingtoneMode;
        RingtoneVolume = _settings.RingtoneVolume;
        RingtoneRtttl = _settings.RingtoneRtttl;
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
        OkToMqtt = _settings.OkToMqtt;
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
        HomeAltitudeText  = _manualHomeAltitude?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _suppressHomeTextUpdate = false;
        HomeAltitude = _manualHomeAltitude;
        SelectedLocationSource = LocationSourceOptions.FirstOrDefault(o =>
            string.Equals(o.Value, _settings.HomeLocationSource, StringComparison.OrdinalIgnoreCase))
            ?? LocationSourceOptions[0];

        // Restore node list filter state.
        NodeSearchText        = _settings.NodeFilterSearch;
        NodeHopsFilter        = _settings.NodeFilterHops;
        NodeKeyFilter         = _settings.NodeFilterKey;
        NodeLocationFilter    = _settings.NodeFilterLocation;
        NodeIgnoredFilter     = _settings.NodeFilterIgnored;
        NodeTemperatureFilter = _settings.NodeFilterTemperature;
        NodeHumidityFilter    = _settings.NodeFilterHumidity;
        NodePressureFilter    = _settings.NodeFilterPressure;
        MapNodeLabelMode      = string.IsNullOrWhiteSpace(_settings.MapNodeLabelMode)
            ? "Short name"
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

    /// <summary>Apply database-backed updates for only the node ids that changed.</summary>
    private bool ApplyDirtyNodeUpdates()
    {
        if (_dirtyNodeNums.Count == 0)
        {
            ReloadNodes();
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

        foreach (var nodeNum in changedNodeNums)
        {
            var latest = _nodeStore.Get(nodeNum);
            var existing = Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);

            if (latest is null)
            {
                if (existing is not null)
                    Nodes.Remove(existing);
                _nodesByNum.Remove(nodeNum);
                if (UpdateNodeMapStateSignature(nodeNum, null))
                    mapChangedNodeNums.Add(nodeNum);
                _nodeTooltipSignatures.Remove(nodeNum);
                _nodeTooltipCache.Remove(nodeNum);
                RefreshConversationNode(nodeNum);
                continue;
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
                    Nodes.Remove(existing);
                    InsertNodeByDefaultOrder(latest);
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
        }

        foreach (var nodeNum in changedNodeNums)
            _dirtyNodeNums.Remove(nodeNum);
        RefreshNodesViewIfNeeded();
        if (mapChangedNodeNums.Count > 0)
            NodeMarkersChanged?.Invoke(mapChangedNodeNums);
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
        int insertAt = 0;
        while (insertAt < Nodes.Count)
        {
            var current = Nodes[insertAt];
            if (node.LastHeardEpoch > current.LastHeardEpoch) break;
            if (node.LastHeardEpoch == current.LastHeardEpoch && node.NodeNum < current.NodeNum) break;
            insertAt++;
        }
        Nodes.Insert(insertAt, node);
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
            !string.Equals(NodeIgnoredFilter, "Show all", StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(NodeDistanceKmText) ||
            !string.IsNullOrWhiteSpace(NodeMaxAgeMinutesText);

        bool hasSortOrGroup = view.SortDescriptions.Count > 0 || view.GroupDescriptions?.Count > 0;

        if (!hasActiveFilter && !hasSortOrGroup)
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
            {
                ReloadNodes();
                _nodesDirty = false;
            }
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
    /// Offloads a blocking <see cref="MeshtasticCore.Transmit"/> call to a
    /// thread-pool thread, serialized through <see cref="_txSemaphore"/> so
    /// concurrent sends never race on the shared native Core handle.
    /// Await this from async <c>[RelayCommand]</c> methods to keep the UI thread
    /// responsive during transmit (stop-RX + USB streaming + restart-RX).
    /// </summary>
    private async Task<bool> TransmitAsync(LoraPreset preset, ulong hz, byte[] frame,
                                           byte gain, bool amp)
    {
        await _txSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _core.Transmit(preset, hz, frame, gain, amp))
                             .ConfigureAwait(false);
        }
        finally
        {
            _txSemaphore.Release();
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
            try { _core.Transmit(preset, hz, frame, gain, amp); }
            catch { /* best-effort */ }
            finally { _txSemaphore.Release(); }
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
                continue;

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
                OpenConversation(peer, NodeDisplayName(peer), focus: false);
            }
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

        var pendingReactions = new List<MessageRecord>();
        foreach (var msg in _messageStore.Conversation(convo.NodeNum, _myNodeNum))
        {
            if (IsReactionRecord(msg))
            {
                pendingReactions.Add(msg);
                continue;
            }

            if (IsOrphanReactionRow(msg))
                continue;

            if (IsNonStandaloneReplyRecord(msg))
                continue;

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
            Text = msg.Text,
            RssiDbm = msg.RssiDbfs,
            SnrDb = msg.SnrDb,
            PacketId = msg.PacketId,
            IsOutgoing = outgoing,
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
            Text = $"reacted {glyph} (original message {targetText} not found)",
            RssiDbm = reaction.RssiDbfs,
            SnrDb = reaction.SnrDb,
            PacketId = reaction.PacketId,
            IsOutgoing = outgoing,
            Delivery = outgoing && !isBroadcast
                ? (MessageDelivery)reaction.Delivery
                : MessageDelivery.None,
        };
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
        foreach (var c in existing)
            Channels.Add(new ChannelViewModel(c, OnChannelSaved,
                IsChannelRtttlMuted(c.Index), OnChannelRtttlMuteChanged));
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
    }

    private bool IsNodeIgnored(uint nodeNum) =>
        Nodes.FirstOrDefault(n => n.NodeNum == nodeNum)?.Ignored == true;

    private void OnConversationMuteRtttlChanged(ConversationViewModel convo, bool muted)
    {
        var node = Nodes.FirstOrDefault(n => n.NodeNum == convo.NodeNum);
        if (node is not null)
            SetNodeRtttlMuted(node, muted);
    }

    private void OnConversationLocationHistoryChanged(ConversationViewModel convo)
    {
        if (!convo.ShowLocationHistoryOnMap)
            return;

        MapDataChanged?.Invoke(this, EventArgs.Empty);
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
            PositionPrecision = 0,
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
        LoadChatHistory();
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

    private static bool ShouldLogDeviceStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        return !status.StartsWith("HackRF open OK", StringComparison.OrdinalIgnoreCase);
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
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanSelectDevice));
    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        OnPropertyChanged(nameof(IsRtlSdr));
        OnPropertyChanged(nameof(IsHackRf));
        if (_suppressDeviceUpdate || value is null) return;
        ApplyRxDevice(value.Kind);
    }
    partial void OnSelectedTxDeviceChanged(DeviceOption? value)
    {
        if (_suppressDeviceUpdate || value is null) return;
        ApplyTxDevice(value.Kind);
    }
    partial void OnAgcEnableChanged(bool value) { SaveSettings(); }
    partial void OnAgcTargetDbfsChanged(double value) { SaveSettings(); }
    partial void OnRtlGainDbChanged(byte value) { PushGains(); SaveSettings(); }
    partial void OnBiasTeeChanged(bool value) { _core.SetDeviceOption("bias_tee", value ? 1 : 0); SaveSettings(); }
    partial void OnDcBlockEnableChanged(bool value) { _core.SetDcBlock(value); SaveSettings(); }

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
    partial void OnUseFahrenheitChanged(bool value)
    {
        SaveSettings();
        if (!_settingsLoaded) return;

        _nodeTooltipSignatures.Clear();
        _nodeTooltipCache.Clear();
        foreach (var convo in Tabs.OfType<ConversationViewModel>())
            convo.RefreshTelemetryFormatting();
        MapDataChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnUseMilesChanged(bool value)
    {
        SaveSettings();
        OnPropertyChanged(nameof(DistanceUnitShort));
        OnPropertyChanged(nameof(DistanceUnitLong));
        OnPropertyChanged(nameof(MaxDistanceFilterToolTip));
        if (!_settingsLoaded) return;
        RefreshNodesFilter();
    }
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
        _settings.OverrideSf    = OverrideSf;
        _settings.OverrideBwHz  = (uint)Math.Round(OverrideBwKhz * 1000.0);
        _settings.OverrideCr    = OverrideCr;
        _settings.LnaGainDb = LnaGainDb;
        _settings.VgaGainDb = VgaGainDb;
        _settings.AmpEnable = AmpEnable;
        _settings.DeviceKind = SelectedDevice?.Kind.ToString() ?? "Auto";
        _settings.RxDeviceKind = SelectedDevice?.Kind.ToString() ?? "Auto";
        _settings.TxDeviceKind = SelectedTxDevice?.Kind.ToString() ?? "HackRf";
        _settings.AgcEnable = AgcEnable;
        _settings.AgcTargetDbfs = AgcTargetDbfs;
        _settings.RtlGainDb = RtlGainDb;
        _settings.BiasTee = BiasTee;
        _settings.DcBlockEnable = DcBlockEnable;
        _settings.Theme = Theme;
        _settings.UseFahrenheit = UseFahrenheit;
        _settings.UseMiles = UseMiles;
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
        _settings.UserNodeNum = _myNodeNum;
        _settings.UserLongName = MyLongName ?? string.Empty;
        _settings.UserShortName = MyShortName ?? string.Empty;
        _settings.UserRole = MyRole ?? "Client";
        _settings.UserHwModel = MyHwModel ?? "UNSET";
        _settings.RebroadcastMode = RebroadcastMode ?? "ALL";
        _settings.HopLimit = Math.Clamp(HopLimit, 1, 7);
        _settings.OkToMqtt = OkToMqtt;
        _settings.UserPublicKey = MyPublicKey ?? string.Empty;
        _settings.UserPrivateKey = MyPrivateKey ?? string.Empty;
        _settings.HomeLocationSource = SelectedLocationSource?.Value ?? ManualLocationSourceValue;
        _settings.HomeLatitude  = _manualHomeLatitude;
        _settings.HomeLongitude = _manualHomeLongitude;
        _settings.HomeAltitude  = _manualHomeAltitude;
        _settings.GpsSerialPort = GpsPortName?.Trim() ?? string.Empty;
        _settings.GpsBaudRate = ParseGpsBaudRateOrNull() ?? 0;
        _settings.OpenConversations = Tabs.OfType<ConversationViewModel>()
                                          .Select(c => c.NodeNum)
                                          .ToList();
        _settings.Save();
    }

    // -- Identity change handlers -------------------------------------------

    partial void OnMyNodeIdTextChanged(string value)
    {
        _myNodeNum = ParseNodeId(value);
        OnPropertyChanged(nameof(MyMacAddress));
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
    partial void OnOkToMqttChanged(bool value) => SaveSettings();

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
            ? null : (int.TryParse(HomeAltitudeText.Trim(), NumberStyles.Integer,
                                   CultureInfo.InvariantCulture, out int altParsed)
                      ? altParsed : _manualHomeAltitude);
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
        var altStr = fix.AltitudeM is int a ? $"  alt {a} m" : string.Empty;
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
            FormatTemperature);
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
            bool affectedMap = convo.ShowLocationHistoryOnMap && convo.LocationHistory.Count > 1;
            int idx = Tabs.IndexOf(convo);
            Tabs.Remove(convo);
            if (ReferenceEquals(SelectedTab, convo))
                SelectedTab = Tabs.Count > 0
                    ? Tabs[Math.Min(idx, Tabs.Count - 1)]
                    : null;
            // Closing a DM tab means it should not reopen next launch.
            SaveSettings();
            if (affectedMap)
                MapDataChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Switch the RX radio backend (only valid while stopped) and
    /// refresh the device badge / status.</summary>
    private void ApplyRxDevice(RadioDeviceKind kind)
    {
        if (IsRunning) return;
        _core.SetRxDevice(kind);
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(TxDeviceName));
        OnPropertyChanged(nameof(DeviceStatus));
        OnPropertyChanged(nameof(HasRealRadio));
        OnPropertyChanged(nameof(DeviceBadge));
        OnPropertyChanged(nameof(CanTransmit));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendNodeInfoCommand.NotifyCanExecuteChanged();
        SendPositionCommand.NotifyCanExecuteChanged();
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
            Status = IsCustomLoraParams
                ? $"RX @ {CenterFreqMHz:F3} MHz / SF{OverrideSf} BW{OverrideBwKhz:G}kHz CR4/{OverrideCr}"
                : $"RX @ {CenterFreqMHz:F3} MHz / {SelectedPreset}";
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
            Status = IsCustomLoraParams
                ? $"RX @ {CenterFreqMHz:F3} MHz / SF{OverrideSf} BW{OverrideBwKhz:G}kHz CR4/{OverrideCr}"
                : $"RX @ {CenterFreqMHz:F3} MHz / {SelectedPreset}";
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

    /// <summary>True when the selected TX radio backend can transmit (HackRF).</summary>
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
                okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
            if (ok)
            {
                ch.Messages.Add(new ChannelMessage
                {
                    FromId = NodeDisplayName(_myNodeNum),
                    Text = text,
                    PacketId = packetId,
                    IsOutgoing = true,
                });
                if (ch.Messages.Count > 1000) ch.Messages.RemoveAt(0);
                PersistOutgoingText(0xFFFFFFFFu, packetId, text, ch.Config.Name,
                                    MessageDelivery.None);
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
    private async Task SendDirectMessageAsync(ConversationViewModel? convo)
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
                                // Proactively pull the peer's key using the same NODEINFO_APP
                                // request style as official Meshtastic apps.
                                SendNodeInfoExchangeRequest(convo.NodeNum);
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
                      okToMqtt: OkToMqtt)
                : MeshEncoder.EncodeTextMessage(
                      ch!.Config, _myNodeNum, packetId, text,
                      to: convo.NodeNum, hopLimit: (byte)HopLimit, wantAck: true,
                      okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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

    /// <summary>
    /// Send a Meshtastic per-message emoji reaction (Data.reply_id + Data.emoji)
    /// targeting the selected message in the active chat tab.
    /// </summary>
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

        string? emoji = EmojiPickerWindow.PickEmoji(Application.Current?.MainWindow);
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

                ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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
                ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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
    /// Send a waypoint to the mesh and persist it locally so it remains on the
    /// map until explicitly deleted.
    /// </summary>
    public async Task SendWaypointFromMapAsync(double lat, double lon)
    {
        if (!CanTransmit || _myNodeNum == 0)
        {
            Status = "Set your node ID and a TX-capable device before sending waypoints.";
            Log(Status);
            return;
        }

        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null)
        {
            Status = "No primary channel to send waypoint on.";
            Log(Status);
            return;
        }

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
                primary.Config,
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

            bool ok = await TransmitAsync(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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
                Channel = primary.Config.Name,
                Name = name,
                Description = description,
                Icon = icon,
                Latitude = lat,
                Longitude = lon,
                ExpireEpoch = expireEpoch,
                RxEpoch = now,
            });
            ReloadWaypoints();

            Status = $"Sent waypoint ({frame.Length} B) on {primary.DisplayName}";
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

        if (!TimeSpan.TryParseExact(
                WaypointExpiryTimeText?.Trim() ?? string.Empty,
                new[] { "hh\\:mm\\:ss", "hh\\:mm" },
                CultureInfo.InvariantCulture,
                out var tod))
            return 0;

        var local = new DateTime(
            date.Year,
            date.Month,
            date.Day,
            tod.Hours,
            tod.Minutes,
            tod.Seconds,
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
    private void SendNodeInfoRequestOnly(uint to, uint packetId = 0)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;
        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null) return;

        try
        {
            if (packetId == 0) packetId = NextPacketId();
            var frame = MeshEncoder.EncodeNodeInfoRequest(
                primary.Config, _myNodeNum, to, packetId,
                hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            var preset = SelectedPreset; var gain = TxGainDb; var amp = AmpEnable;
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
    private void SendNodeInfoExchangeRequest(uint to, uint packetId = 0)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;
        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null) return;

        try
        {
            if (packetId == 0) packetId = NextPacketId();
            uint role = RoleEnumValue(MyRole);
            byte[] pubKey = TryParseKeyBase64(MyPublicKey);
            var frame = MeshEncoder.EncodeNodeInfo(
                primary.Config, _myNodeNum, packetId,
                MyLongName ?? string.Empty, MyShortName ?? string.Empty,
                hwModel: (uint)HardwareModels.Id(MyHwModel), role: role, publicKey: pubKey,
                to: to, hopLimit: (byte)HopLimit, wantResponse: true);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            var preset = SelectedPreset; var gain = TxGainDb; var amp = AmpEnable;
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
                hwModel: (uint)HardwareModels.Id(MyHwModel), role: role, publicKey: pubKey,
                to: to, hopLimit: (byte)HopLimit, wantResponse: false);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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
    private void ReplyWithPosition(uint to, uint requestId = 0)
    {
        if (!CanTransmit || _myNodeNum == 0 || to == 0 || to == 0xFFFFFFFFu) return;
        if (HomeLatitude is not double lat || HomeLongitude is not double lon) return;
        var primary = Channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary);
        if (primary is null || primary.Config.PositionPrecision == 0) return;

        try
        {
            uint packetId = NextPacketId();
            var frame = MeshEncoder.EncodePosition(
                primary.Config, _myNodeNum, packetId,
                lat, lon, altitudeM: HomeAltitude,
                precisionBits: primary.Config.PositionPrecision,
                to: to, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt,
                requestId: requestId);
            var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
        }
        catch (Exception ex)
        {
            Log($"  position reply failed: {ex.Message}");
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
                                    MessageDelivery delivery = MessageDelivery.Sent)
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
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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
            TransmitBackground(SelectedPreset, hz, frame, TxGainDb, AmpEnable);
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

        if (IsNodeIgnored(header.From)) return;

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

        uint normalizedReplyId = 0;
        if (result is not null && result.Port == PortNum.TextMessage)
            normalizedReplyId = ResolveReactionTargetId(result);

        bool isReactionRecord = false;
        if (result is not null && result.Port == PortNum.TextMessage)
            isReactionRecord = normalizedReplyId != 0
                && result.Emoji != 0;

        var record = new MessageRecord
        {
            PacketId = header.PacketId,
            FromNode = header.From,
            ToNode = header.To,
            PortNum = (int)(result?.Port ?? PortNum.Unknown),
            Channel = result?.ChannelName ?? string.Empty,
            ReplyId = normalizedReplyId,
            Emoji = result?.Emoji ?? 0,
            IsReaction = isReactionRecord,
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
            MarkNodeDirty(header.From);
            return;
        }

        Messages.Insert(0, record);

        bool nodeChanged = false;
        if (result is null)
        {
            Log($"  rx undecoded from {header.FromId} (chan hash {header.ChannelHash:X2})");
        }
        else
        {
            var senderName = NodeDisplayName(header.From);
            switch (result.Port)
            {
                case PortNum.TextMessage:
                    uint reactionTargetId = ResolveReactionTargetId(result);
                    bool isReaction = reactionTargetId != 0
                        && result.Emoji != 0;
                    bool isReplyLinkedNonReaction = reactionTargetId != 0 && !isReaction;
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
                            Log($"  DM reply-linked packet from {senderName} ignored (target {reactionTargetId:x8})");
                        }
                        else if (existed)
                            convo.Add(new ChannelMessage
                            {
                                FromId = senderName,
                                Text = record.Text,
                                RssiDbm = record.RssiDbfs,
                                SnrDb = record.SnrDb,
                                PacketId = header.PacketId,
                            });
                        if (!isReaction && !isReplyLinkedNonReaction)
                        {
                            MarkTabNeedsAttention(convo);
                            Log($"  DM from {senderName}: {record.Text}");
                        }
                        // Acknowledge if the sender asked for one (firmware does
                        // this for any unicast packet addressed to it).
                        if (header.WantAck) SendAck(header, result);
                        if (!isReplyLinkedNonReaction && !IsNodeRtttlMuted(header.From))
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
                            Log($"  [{result.ChannelName}] {senderName} reply-linked packet ignored (target {reactionTargetId:x8})");
                        }
                        else
                        {
                            chanVm?.Messages.Add(new ChannelMessage
                            {
                                FromId = senderName,
                                Text = record.Text,
                                RssiDbm = record.RssiDbfs,
                                SnrDb = record.SnrDb,
                                PacketId = header.PacketId,
                            });
                            MarkTabNeedsAttention(chanVm);
                            if (chanVm is not null && chanVm.Messages.Count > 1000)
                                chanVm.Messages.RemoveAt(0);
                            Log($"  [{result.ChannelName}] {senderName}: {record.Text}");
                            shouldRing = chanVm?.MuteRtttl != true;
                        }

                        if (shouldRing && !IsNodeRtttlMuted(header.From))
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
                            RequestNodeInfoReply(header.From);
                        }
                        break;
                    }
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
                        });

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
                        RequestNodeInfoReply(header.From);
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
                                requestId: result.RequestId != 0 ? result.RequestId : header.PacketId);
                        }
                        break;
                    }
                    nodeChanged = true;
                    var existingPositionNode = _nodeStore.Get(header.From);
                    bool positionChanged = existingPositionNode?.Latitude is not double oldLat
                        || existingPositionNode.Longitude is not double oldLon
                        || Math.Abs(oldLat - result.Position.Latitude) > 1e-7
                        || Math.Abs(oldLon - result.Position.Longitude) > 1e-7
                        || existingPositionNode.AltitudeM != result.Position.AltitudeM;
                    _nodeStore.Upsert(new NodeRecord
                    {
                        NodeNum = header.From,
                        Latitude = result.Position.Latitude,
                        Longitude = result.Position.Longitude,
                        AltitudeM = result.Position.AltitudeM,
                        LastHeardEpoch = rxEpoch,
                    });
                    if (positionChanged)
                        Log($"  position {header.FromId}: {result.Position.Latitude:F5}, {result.Position.Longitude:F5}");
                    // Android's "exchange position" can include coordinates while
                    // still setting want_response on a directed packet.
                    if (directedPositionResponseRequest)
                    {
                        Log($"  position exchange requested by {senderName} — replying");
                        ReplyWithPosition(header.From,
                            requestId: result.RequestId != 0 ? result.RequestId : header.PacketId);
                    }
                    break;
                case PortNum.Waypoint when result.Waypoint is not null:
                    {
                        var wp = result.Waypoint;
                        // Some senders omit waypoint id (0). Use packet id as
                        // a stable fallback key per sender.
                        uint waypointId = wp.Id != 0 ? wp.Id : header.PacketId;
                        _waypointStore.Upsert(new WaypointRecord
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
                        });
                        _waypointsDirty = true;
                        Log($"  waypoint {header.FromId}: {wp.Latitude:F5}, {wp.Longitude:F5}  {wp.Name}");
                    }
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
                    {
                        var temp = t.TemperatureC is float tempC ? FormatTemperature(tempC) : "n/a";
                        Log($"  telemetry {header.FromId}: {temp} {t.RelativeHumidityPct:F0}% {t.BarometricPressureHpa:F0}hPa");
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
                default:
                    Log($"  [{result.ChannelName}] {header.FromId} {result.Port} ({result.AppPayload.Length} B)");
                    break;
            }
        }

        MarkNodeDirty(header.From);
        if (nodeChanged) { /* names refreshed on the next dirty-node apply */ }
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
        for (int i = 0; i < MaxRxEventsPerTick; i++)
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

        // Flush any node-list changes accumulated during the event drain in one
        // pass rather than once per packet (avoids Nodes.Clear + full DataGrid
        // rebind stutter on every received frame).
        if (_nodesDirty && !_suspendNodeReload)
        {
            // Fallback: if we somehow flagged node dirtiness without tracking
            // specific node ids, do a full sync so the grid can't get stale.
            if (_dirtyNodeNums.Count == 0)
            {
                ReloadNodes();
                _nodesDirty = false;
            }
            else
                _nodesDirty = ApplyDirtyNodeUpdates();
        }
        if (_waypointsDirty)
        {
            ReloadWaypoints();
            _waypointsDirty = false;
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
        _gpsService.Stop();
        _gpsService.StatusChanged -= HandleGpsStatusChanged;
        _gpsService.FixReceived -= HandleGpsFixReceived;
        _gpsService.Dispose();
        _core.Dispose();
        _nodeStore.Dispose();
        _waypointStore.Dispose();
        _channelStore.Dispose();
        _messageStore.Dispose();
        _ringtone.Dispose();
    }
}
