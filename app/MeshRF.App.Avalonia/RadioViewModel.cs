// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

/// <summary>One selectable RX sample rate. ToString() drives the ComboBox's
/// default (no-ItemTemplate) display text.</summary>
public sealed record SampleRateOption(uint Hz, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Radio control surface: device select / start-stop RX / signal stats,
/// plus a real (not mocked) message/node list — received frames are fed
/// through MeshRxRouter (MeshRF.Core) via AvaloniaMeshRxHost. Also owns
/// node/waypoint/message context-menu actions (traceroute,
/// telemetry/position/nodeinfo requests, reply/react, etc.).
/// </summary>
public partial class RadioViewModel : ObservableObject, IDisposable
{
    // Mirrors MainViewModel.PayloadLineRegex; matches lines like
    // "  payload[OK] len=31 crc=E511/E511 FFFFFFFF594FA54F...".
    private static readonly Regex PayloadLineRegex = new(
        @"payload(?:\[(?<status>OK|BAD)\])?\s+len=(?<len>\d+)(?:\s+crc=(?<rx>[0-9A-Fa-f]+)/(?<calc>[0-9A-Fa-f]+))?\s+(?<hex>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    private static readonly TimeSpan TracerouteCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PositionRequestCooldown = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly MeshtasticCore? _core;
    private readonly DispatcherTimer _pollTimer;
    private readonly NodeStore _nodeStore = new();
    private readonly MessageStore _messageStore = new();
    private readonly ChannelStore _channelStore = new();
    private readonly WaypointStore _waypointStore = new();
    private readonly AvaloniaMeshRxHost _rxHost;
    private readonly MeshRxRouter _rxRouter;
    private DateTime _lastTracerouteUtc = DateTime.MinValue;
    private DateTime _lastPositionRequestUtc = DateTime.MinValue;
    private ITabItem? _previousTab;

    /// <summary>False until the constructor has finished loading every setting
    /// from disk. Gates <see cref="SaveSettings"/> so mid-construction property
    /// cascades can't persist not-yet-loaded defaults over saved values.</summary>
    private bool _settingsLoaded;

    public ObservableCollection<ITabItem> Tabs => _rxHost.Tabs;
    public ObservableCollection<NodeRecord> Nodes => _rxHost.Nodes;
    public ObservableCollection<WaypointRecord> Waypoints => _rxHost.Waypoints;
    public ObservableCollection<string> LogLines => _rxHost.LogLines;

    [ObservableProperty]
    private ITabItem? _selectedTab;

    [ObservableProperty]
    private RadioDeviceKind _selectedDevice = RadioDeviceKind.Null;

    [ObservableProperty]
    private RadioDeviceKind _selectedTxDevice = RadioDeviceKind.HackRf;

    /// <summary>TX can't run on RTL-SDR (receive-only hardware). The SX1262
    /// stick is transmit-only and so appears here but never in
    /// <see cref="AvailableDevices"/>.</summary>
    public RadioDeviceKind[] AvailableTxDevices { get; } =
        { RadioDeviceKind.Null, RadioDeviceKind.HackRf, RadioDeviceKind.Sx1262 };

    /// <summary>The CH341+SX126x sticks MeshRF knows how to drive. Unspecified
    /// is offered so the picker can start on it: nothing transmits until a real
    /// board is chosen, because the two cannot be told apart at runtime.</summary>
    public Sx1262Board[] AvailableSx1262Boards { get; } =
        { Sx1262Board.Unspecified, Sx1262Board.MeshStick, Sx1262Board.MeshToad };

    private static readonly uint[] HackRfSampleRatesHz =
        [2_000_000, 2_400_000, 4_000_000, 8_000_000, 10_000_000, 12_500_000, 16_000_000, 20_000_000];
    private static readonly uint[] RtlSdrSampleRatesHz =
        [960_000, 1_024_000, 1_200_000, 1_440_000, 1_600_000, 1_800_000, 1_920_000,
         2_048_000, 2_400_000, 2_560_000, 2_880_000, 3_200_000];
    private const uint HackRfMaxSelectableRateHz = 20_000_000;
    private const uint RtlSdrDecodeSafeMaxRateHz = 2_560_000;
    private bool _suppressSampleRateUpdate;

    [ObservableProperty]
    private IReadOnlyList<SampleRateOption> _sampleRateOptions = Array.Empty<SampleRateOption>();

    [ObservableProperty]
    private SampleRateOption? _selectedRxSampleRate;

    public bool CanSelectRxSampleRate => !IsRunning && SelectedDevice != RadioDeviceKind.Null && SampleRateOptions.Count > 0;

    public bool IsHackRf => SelectedDevice == RadioDeviceKind.HackRf;
    public bool IsRtlSdr => SelectedDevice == RadioDeviceKind.RtlSdr;

    /// <summary>Receiving through the hardware modem. There is no IQ on this
    /// path, so everything downstream of the SDR pipeline is unavailable.</summary>
    public bool IsRxSx1262 => SelectedDevice == RadioDeviceKind.Sx1262;

    /// <summary>The spectrum, waterfall, packet spectrogram and IQ capture all
    /// need IQ that a hardware modem cannot produce.</summary>
    public bool HasSpectrum => !IsRxSx1262;

    /// <summary>Shown over the spectrum area in place of the display.</summary>
    public string NoSpectrumMessage =>
        "No spectrum — the SX1262 is a hardware LoRa modem, not an SDR.\n" +
        "It reports decoded packets with real RSSI and SNR, but produces no IQ, " +
        "so the waterfall, packet snapshot and IQ capture are unavailable.\n" +
        "Select a HackRF or RTL-SDR as the receiver to get them back.";

    /// <summary>Serials of the attached SX1262 sticks. Only meaningful when
    /// more than one is plugged in, which is the only case where MeshRF has to
    /// be told which to use.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _sx1262Serials = Array.Empty<string>();

    [ObservableProperty]
    private string _selectedSx1262Serial = string.Empty;

    /// <summary>Only worth showing when there is an actual choice to make.</summary>
    public bool ShowSx1262SerialPicker =>
        (IsRxSx1262 || IsTxSx1262) && Sx1262Serials.Count > 1;
    public bool IsTxHackRf => SelectedTxDevice == RadioDeviceKind.HackRf;
    public bool IsTxSx1262 => SelectedTxDevice == RadioDeviceKind.Sx1262;

    /// <summary>True once a real board is chosen. The power control is
    /// meaningless before that, and nothing can transmit.</summary>
    public bool IsSx1262BoardChosen => SelectedSx1262Board != Sx1262Board.Unspecified;

    /// <summary>Prompt shown while the SX1262 is selected but no board is.
    /// This is the only thing standing between a MeshToad owner and
    /// transmitting 8 dB hotter than the UI says.</summary>
    public bool ShowSx1262BoardPrompt => IsTxSx1262 && !IsSx1262BoardChosen;

    /// <summary>Shown only for the MeshToad, whose PA can pull ~900 mA on
    /// transmit — more than a USB 2.0 port is obliged to supply.</summary>
    public bool ShowSx1262PowerWarning =>
        IsTxSx1262 && SelectedSx1262Board == Sx1262Board.MeshToad && Sx1262TxPowerDbm > 22;

    /// <summary>The band we were last transmitting in, so a region change can
    /// be measured against it. Null until the constructor finishes loading —
    /// restoring a saved region is not a band change.</summary>
    private Region? _acknowledgedBandRegion;

    /// <summary>Set when the region moves to a band that does not overlap the
    /// one we were operating in, and a stick is the transmitter. Blocks
    /// transmit until acknowledged, because the app cannot know which band the
    /// attached stick was built for and the accidental case — a mis-clicked
    /// region dropdown — is indistinguishable from the deliberate one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSx1262BandWarning))]
    private bool _bandChangeNeedsAck;

    public bool ShowSx1262BandWarning => IsTxSx1262 && BandChangeNeedsAck;

    /// <summary>Text of the band warning, naming both bands — the number is the
    /// whole point, so it belongs in the message rather than a tooltip.</summary>
    public string Sx1262BandWarningText
    {
        get
        {
            var r = ChannelPlan.Range(SelectedRegion);
            return $"⚠ {SelectedRegion} is {r.FreqStartMHz:0.###}–{r.FreqEndMHz:0.###} MHz — " +
                   "confirm your stick is built for this band";
        }
    }

    /// <summary>Accepts the band change. One click for someone who really does
    /// own a stick for the new band; the same click is what an accidental
    /// region change has to survive.</summary>
    [RelayCommand]
    private void AcknowledgeBandChange()
    {
        _acknowledgedBandRegion = SelectedRegion;
        BandChangeNeedsAck = false;
    }

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

    [ObservableProperty]
    private bool _biasTee;

    [ObservableProperty]
    private byte _txGainDb = 47;

    [ObservableProperty]
    private bool _txAmpEnable;

    [ObservableProperty]
    private Sx1262Board _selectedSx1262Board = Sx1262Board.Unspecified;

    /// <summary>Antenna-port transmit power in dBm for the SX1262 stick. Unlike
    /// <see cref="TxGainDb"/> (a HackRF VGA setting) this is real radiated
    /// power, so it is bounded by the selected board rather than a fixed
    /// range.</summary>
    [ObservableProperty]
    private int _sx1262TxPowerDbm = 22;

    [ObservableProperty]
    private int _sx1262MinPowerDbm = -9;

    [ObservableProperty]
    private int _sx1262MaxPowerDbm = 22;

    [ObservableProperty]
    private bool _dcBlockEnable = true;

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

    /// <summary>Keep the newest message in view. Persisted alongside the rest
    /// of the chat state, same as MeshRF.App's AutoScroll.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>Same, for the log panel.</summary>
    [ObservableProperty]
    private bool _logAutoScroll = true;

    // ----- Ringtone -----

    private readonly IRingtonePlayer _ringtone = new AvaloniaRingtonePlayer();

    public string[] RingtoneModes { get; } = { "Off", "Play once", "5 seconds", "10 seconds", "30 seconds" };

    [ObservableProperty]
    private string _ringtoneMode = "Play once";

    [ObservableProperty]
    private double _ringtoneVolume = 70;

    [ObservableProperty]
    private string _ringtoneRtttl = IRingtonePlayer.MeshtasticDefault;

    /// <summary>Map the display name to the enum, matching MeshRF.App's labels.
    /// Fully qualified because the RingtoneMode property above shadows the
    /// enum type name inside this class.</summary>
    private static MeshRF.RingtoneMode ParseRingtoneMode(string? name) => name switch
    {
        "Off" => MeshRF.RingtoneMode.Off,
        "5 seconds" => MeshRF.RingtoneMode.Seconds5,
        "10 seconds" => MeshRF.RingtoneMode.Seconds10,
        "30 seconds" => MeshRF.RingtoneMode.Seconds30,
        _ => MeshRF.RingtoneMode.PlayOnce,
    };

    [RelayCommand]
    private void TestRingtone() =>
        _ringtone.Play(RingtoneRtttl, ParseRingtoneMode(RingtoneMode), RingtoneVolume / 100.0);

    /// <summary>Play the incoming-message alert, unless it's muted.</summary>
    public void PlayIncomingRingtone() =>
        _ringtone.Play(RingtoneRtttl, ParseRingtoneMode(RingtoneMode), RingtoneVolume / 100.0);

    partial void OnRingtoneModeChanged(string value) => SaveSettings();
    partial void OnRingtoneVolumeChanged(double value) => SaveSettings();
    partial void OnRingtoneRtttlChanged(string value) => SaveSettings();

    [ObservableProperty]
    private uint _pendingReplyPacketId;

    [ObservableProperty]
    private string _pendingReplyContext = string.Empty;

    public bool HasPendingReply => PendingReplyPacketId != 0;

    /// <summary>Selectable RX backends. Deliberately not Enum.GetValues: the
    /// enum still carries Auto because its value is part of the C ABI, but
    /// "pick a device for me" is not offered in the UI — you choose the radio
    /// you actually have. Ordered to match <see cref="AvailableTxDevices"/>.</summary>
    public RadioDeviceKind[] AvailableDevices { get; } =
        { RadioDeviceKind.Null, RadioDeviceKind.HackRf, RadioDeviceKind.RtlSdr,
          RadioDeviceKind.Sx1262 };

    public string ToggleButtonText => IsRunning ? "Stop RX" : "Start RX";

    /// <summary>Raised when a CRC-valid packet decodes, so the view can freeze
    /// a spectrogram of it (MeshRF.App's PacketDecoded).</summary>
    public event Action? PacketDecoded;

    /// <summary>Exposed so MainWindow's code-behind can drive the
    /// spectrum/waterfall pull loop — mirrors how MeshRF.App's
    /// MainWindow.xaml.cs owns that render loop rather than MainViewModel.</summary>
    public MeshtasticCore? Core => _core;

    [ObservableProperty]
    private double _spectrumCenterHz;

    [ObservableProperty]
    private double _spectrumSpanHz;

    [ObservableProperty]
    private double _waterfallFloorDb = -100.0;

    [ObservableProperty]
    private double _waterfallCeilDb = 0.0;

    [ObservableProperty]
    private bool _waterfallAutoLevels = true;

    [ObservableProperty]
    private WaterfallColormap _waterfallColormap = WaterfallColormap.Turbo;

    public WaterfallColormap[] AvailableColormaps { get; } = Enum.GetValues<WaterfallColormap>();

    [ObservableProperty]
    private double _waterfallRowsPerSecond = 60.0;

    // ----- My Node identity (Configure dialog) -----

    public string[] NodeRoleOptions { get; } =
    {
        "Client", "ClientMute", "ClientHidden", "Router", "RouterClient",
        "Repeater", "Tracker", "Sensor", "TAK", "TakTracker", "LostAndFound",
        "RouterLate", "ClientBase",
    };

    public IReadOnlyList<string> HwModelOptions { get; } = HardwareModels.AllNames;

    [ObservableProperty]
    private string _myNodeIdText = string.Empty;

    [ObservableProperty]
    private string _myLongName = "MeshRF";

    [ObservableProperty]
    private string _myShortName = "MRF";

    [ObservableProperty]
    private string _myRole = "Client";

    /// <summary>Firmware <c>User.is_licensed</c>: amateur-radio operation. The
    /// long name doubles as the call sign, exactly as firmware stores it.</summary>
    [ObservableProperty]
    private bool _myIsLicensed;

    /// <summary>Firmware <c>User.is_unmessagable</c>. Advertised only — it asks
    /// peers not to open a conversation, and nothing here enforces it.</summary>
    [ObservableProperty]
    private bool _myIsUnmessagable;

    [ObservableProperty]
    private string _myHwModel = "UNSET";

    [ObservableProperty]
    private string _myPublicKey = string.Empty;

    [ObservableProperty]
    private string _myPrivateKey = string.Empty;

    [ObservableProperty]
    private string _myNodeStatus = string.Empty;

    [ObservableProperty]
    private int _hopLimit = 3;

    /// <summary>Firmware ignore_mqtt: never relay MQTT-derived traffic.</summary>
    [ObservableProperty]
    private bool _ignoreMqtt;

    [ObservableProperty]
    private bool _okToMqtt;

    [ObservableProperty]
    private string _homeLatitudeText = string.Empty;

    [ObservableProperty]
    private string _homeLongitudeText = string.Empty;

    [ObservableProperty]
    private string _homeAltitudeText = string.Empty;

    [ObservableProperty]
    private string _myFirmwareVersion = "2.8.0";

    [ObservableProperty]
    private string _myFirmwareEdition = "VANILLA";

    public string[] FirmwareEditionOptions { get; } = { "VANILLA", "PREMIUM" };

    [ObservableProperty]
    private string _rebroadcastMode = "All";

    public string[] RebroadcastModeOptions { get; } =
        { "All", "AllSkipDecoding", "LocalOnly", "KnownOnly", "None", "CorePortnumsOnly" };

    [ObservableProperty]
    private bool _routingRelayEnabled;

    [ObservableProperty]
    private string _homeLocationSource = "Manual";

    public string[] HomeLocationSourceOptions { get; } = { "Manual", "UsbSerialGps" };

    public bool IsUsbSerialLocationSource => HomeLocationSource == "UsbSerialGps";
    public bool IsManualLocationSource => !IsUsbSerialLocationSource;

    [ObservableProperty]
    private string _gpsSerialPort = string.Empty;

    [ObservableProperty]
    private string _gpsBaudRateText = string.Empty;

    // ----- Display units -----

    public string[] UnitSystems { get; } = { "Metric", "Imperial" };

    [ObservableProperty]
    private string _unitSystemName = "Metric";

    public UnitSystem CurrentUnitSystem =>
        string.Equals(UnitSystemName, "Imperial", StringComparison.OrdinalIgnoreCase)
            ? UnitSystem.Imperial : UnitSystem.Metric;

    // The unit system alone drives temperature and distance display (as in
    // MeshRF.App); the legacy per-quantity settings are kept in sync with it.
    public bool UseImperial => CurrentUnitSystem == UnitSystem.Imperial;

    public bool UseFahrenheit => UseImperial;

    public bool UseMiles => UseImperial;

    /// <summary>Altitude field label, unit-aware like MeshRF.App's.</summary>
    public string HomeAltitudeLabel => CurrentUnitSystem == UnitSystem.Imperial ? "Alt (ft)" : "Alt (m)";

    /// <summary>Placeholder for the node list's max-distance filter box.</summary>
    public string NodeDistanceUnitShort => DisplayUnits.DistanceUnitShort(CurrentUnitSystem);

    /// <summary>Index of the last selected channel tab, persisted so the same
    /// tab is reselected next launch (MeshRF.App's LastSelectedChannelIndex).</summary>
    private int _lastSelectedChannelIndex;

    public RadioViewModel()
    {
        NodesView = new DataGridCollectionView(FilteredNodes);
        _settings = AppSettings.Load();

        // Snapshot everything we need from _settings into locals up front.
        // OnSelectedPresetChanged/OnSelectedRegionChanged etc. below call
        // SaveSettings(), which mutates these same _settings fields to match
        // the view model's current (partially-applied) state — reading
        // _settings again later in this constructor would see those
        // in-progress values instead of what was actually on disk, silently
        // clobbering the saved slot/frequency with a preset's default.
        var savedRxDeviceKind = _settings.RxDeviceKind;
        var savedTxDeviceKind = _settings.TxDeviceKind;
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
        var savedBiasTee = _settings.BiasTee;
        var savedTxGainDb = _settings.TxGainDb;
        var savedTxAmpEnable = _settings.TxAmpEnable;
        var savedSx1262Board = _settings.Sx1262Board;
        var savedSx1262Serial = _settings.Sx1262Serial;
        var savedSx1262TxPowerDbm = _settings.Sx1262TxPowerDbm;
        var savedDcBlockEnable = _settings.DcBlockEnable;
        var savedWaterfallFloorDb = _settings.WaterfallFloorDb;
        var savedWaterfallCeilDb = _settings.WaterfallCeilDb;
        var savedWaterfallAutoLevels = _settings.WaterfallAutoLevels;
        var savedWaterfallRowsPerSecond = _settings.WaterfallRowsPerSecond;
        var savedWaterfallColormap = _settings.WaterfallColormap;
        var savedMapNodeLabelMode = _settings.MapNodeLabelMode;
        var savedOpenConversations = _settings.OpenConversations?.ToList() ?? new List<uint>();
        // Identity fields (My Node dialog) — also snapshotted here, not read
        // directly further down, for the same reason as everything above:
        // the property assignments below trigger OnXxxChanged -> SaveSettings()
        // cascades that would otherwise write these still-at-their-compile-time-
        // default fields back into _settings before we get a chance to load
        // the real saved values, permanently wiping identity settings on every
        // single startup.
        var savedUserLongName = _settings.UserLongName;
        var savedUserShortName = _settings.UserShortName;
        var savedUserRole = _settings.UserRole;
        var savedUserIsLicensed = _settings.UserIsLicensed;
        var savedUserIsUnmessagable = _settings.UserIsUnmessagable;
        var savedUserHwModel = _settings.UserHwModel;
        var savedUserPublicKey = _settings.UserPublicKey;
        var savedUserPrivateKey = _settings.UserPrivateKey;
        var savedUserNodeStatus = _settings.UserNodeStatus;
        var savedHopLimit = _settings.HopLimit;
        var savedOkToMqtt = _settings.OkToMqtt;
        var savedIgnoreMqtt = _settings.IgnoreMqtt;
        var savedHomeLatitude = _settings.HomeLatitude;
        var savedHomeLongitude = _settings.HomeLongitude;
        var savedHomeAltitude = _settings.HomeAltitude;
        var savedFirmwareVersion = _settings.UserFirmwareVersion;
        var savedFirmwareEdition = _settings.UserFirmwareEdition;
        var savedRebroadcastMode = _settings.RebroadcastMode;
        var savedRoutingRelayEnabled = _settings.RoutingRelayEnabled;
        var savedHomeLocationSource = _settings.HomeLocationSource;
        var savedGpsSerialPort = _settings.GpsSerialPort;
        var savedGpsBaudRate = _settings.GpsBaudRate;
        var savedUnitSystem = _settings.UnitSystem;
        var savedRingtoneMode = _settings.RingtoneMode;
        var savedRingtoneVolume = _settings.RingtoneVolume;
        var savedRingtoneRtttl = _settings.RingtoneRtttl;
        var savedLastSelectedChannelIndex = _settings.LastSelectedChannelIndex;
        var savedSelectedConversationNode = _settings.SelectedConversationNode;

        // First run: mint a full Meshtastic identity instead of leaving the app
        // with a bare random node number and no keys. Order matters — the
        // private key is the root, the public key derives from it, and the node
        // number derives from the public key (PkiNodeNumber, the same hash
        // firmware uses), so our node id and PKI identity agree. A random node
        // number with an unrelated key can never satisfy that check, which is
        // what HasDerivedNodeNumMatch reports on the node grid.
        //
        // Gated on there being no key AND no node number, so an existing
        // settings.json — including one written by MeshRF.App — is never
        // re-minted. The names follow firmware's default: the node id's last
        // four hex digits, as both "Meshtastic abcd" and the short name.
        if (string.IsNullOrEmpty(savedUserPrivateKey) && _settings.UserNodeNum == 0)
        {
            var privateKey = Curve25519.GeneratePrivateKey();
            var publicKey = Curve25519.GetPublicKey(privateKey);
            if (PkiNodeNumber.TryFromPublicKey(publicKey, out var derivedNodeNum) &&
                derivedNodeNum is not (0 or 0xFFFFFFFFu))
            {
                savedUserPrivateKey = Convert.ToBase64String(privateKey);
                savedUserPublicKey = Convert.ToBase64String(publicKey);
                _settings.UserNodeNum = derivedNodeNum;

                var suffix = $"{derivedNodeNum:x8}"[^4..];
                savedUserLongName = $"Meshtastic {suffix}";
                savedUserShortName = suffix;
            }
        }

        // Shared with MeshRF.App's UserNodeNum when set (same settings.json);
        // otherwise an ephemeral random identity for this session — see
        // AvaloniaMeshRxHost.MyNodeNum. Avoid 0 (unset) and the broadcast
        // address for the random fallback.
        var myNodeNum = _settings.UserNodeNum != 0
            ? _settings.UserNodeNum
            : (uint)Random.Shared.NextInt64(1, 0xFFFFFFFE);
        _rxHost = new AvaloniaMeshRxHost(_nodeStore, _channelStore, _waypointStore, _messageStore, myNodeNum, savedOpenConversations);
        _rxHost.OpenConversationsChanged += SaveOpenConversations;
        _rxHost.IncomingDirectMessage += PlayIncomingRingtone;
        _rxHost.IncomingChannelMessage += PlayIncomingRingtone;
        _rxHost.AutoReplyRequested += HandleAutoReplyRequest;
        _rxHost.TelemetryReplyRequested += HandleTelemetryReplyRequest;
        _rxHost.AckRequested += SendAck;
        _rxHost.RoutingReplyReceived += CancelAckRetransmit;
        _rxHost.DecodedPacketForFeed += AppendDecodedPacketJson;
        _rxHost.SelectedTabProvider = () => SelectedTab;
        // Relaying is opt-in via the Routing checkbox; the scheduler is only
        // consulted when RoutingRelayEnabled is on (see RelayContextProvider).
        _rxHost.RelayContextProvider = BuildRelayContext;
        _rxHost.RelayScheduler = new RelayScheduler
        {
            Transmit = frame => TransmitFrameAsync(frame),
            // The scheduler logs from a thread-pool continuation once its
            // contention delay elapses, and _rxHost.Log mutates LogLines, which
            // is bound to a ListBox — so it has to be marshalled.
            Log = LogFromAnyThread,
        };
        // Enables PKC decode in the shared router; without it every direct
        // message stays undecodable.
        _rxHost.MyPrivateKeyProvider = () => TryParseKeyBase64(MyPrivateKey);
        // Uplink is a parallel side-effect of receiving, alongside relaying —
        // the shared router calls this for every non-echo frame it handles.
        _rxHost.UplinkHandler = UplinkIfEligible;
        InitMqtt();
        _rxHost.FormatTemperature = FormatTemperature;
        _rxHost.FormatPressure = hpa => $"{hpa:0.0} hPa";
        // Restore per-channel ringtone mutes. The channel tabs exist by now
        // (the host loads them in its constructor), and MutedRingtoneChannels
        // is the same settings.json key MeshRF.App writes.
        foreach (var channelTab in Tabs.OfType<ChannelTabViewModel>())
            channelTab.MuteRtttl = _settings.MutedRingtoneChannels.Contains(channelTab.Config.Index);
        _rxRouter = new MeshRxRouter(_rxHost, _messageStore, new AvaloniaUiDispatcher());
        SelectedTab = Tabs.FirstOrDefault();
        // Contains() guards the same case the TX line below does, and one more:
        // settings written before Auto was dropped still say "Auto". Falling
        // through leaves the None default rather than selecting a value the
        // picker can't show, which would blank the ComboBox.
        if (Enum.TryParse<RadioDeviceKind>(savedRxDeviceKind, out var device) && AvailableDevices.Contains(device))
            SelectedDevice = device;
        // The board has to be restored before the TX device is: selecting
        // Sx1262 opens the stick against whichever profile is current, and
        // opening it as a MeshStick when it is really a MeshToad would put the
        // power model 8 dB out until the user touched the picker.
        SelectedSx1262Serial = savedSx1262Serial ?? string.Empty;
        if (Enum.TryParse<Sx1262Board>(savedSx1262Board, out var sxBoard) &&
            AvailableSx1262Boards.Contains(sxBoard))
            SelectedSx1262Board = sxBoard;
        if (Enum.TryParse<RadioDeviceKind>(savedTxDeviceKind, out var txDevice) && AvailableTxDevices.Contains(txDevice))
            SelectedTxDevice = txDevice;
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
        BiasTee = savedBiasTee;
        TxGainDb = savedTxGainDb;
        TxAmpEnable = savedTxAmpEnable;
        Sx1262TxPowerDbm = savedSx1262TxPowerDbm;
        DcBlockEnable = savedDcBlockEnable;
        WaterfallFloorDb = savedWaterfallFloorDb;
        WaterfallCeilDb = savedWaterfallCeilDb;
        WaterfallAutoLevels = savedWaterfallAutoLevels;
        WaterfallRowsPerSecond = savedWaterfallRowsPerSecond;
        if (Enum.TryParse<WaterfallColormap>(savedWaterfallColormap, out var cmap))
            WaterfallColormap = cmap;
        if (MapNodeLabelModeOptions.Contains(savedMapNodeLabelMode))
            MapNodeLabelMode = savedMapNodeLabelMode;

        MyNodeIdText = $"!{myNodeNum:x8}";
        MyLongName = string.IsNullOrEmpty(savedUserLongName) ? MyLongName : savedUserLongName;
        MyShortName = string.IsNullOrEmpty(savedUserShortName) ? MyShortName : savedUserShortName;
        MyRole = string.IsNullOrEmpty(savedUserRole) ? MyRole : savedUserRole;
        MyIsLicensed = savedUserIsLicensed;
        MyIsUnmessagable = savedUserIsUnmessagable;
        MyHwModel = string.IsNullOrEmpty(savedUserHwModel) ? MyHwModel : savedUserHwModel;
        MyPublicKey = savedUserPublicKey;
        MyPrivateKey = savedUserPrivateKey;
        // Repair a pair that disagrees on disk — a hand-edited settings.json, or
        // one written before the private key re-derived its public half. Called
        // explicitly rather than left to the assignment above, whose change
        // handler only fires when the value actually differs from the default
        // and would otherwise make this depend on the order of these two lines.
        SyncPublicKeyToPrivateKey();
        MyNodeStatus = savedUserNodeStatus;
        HopLimit = savedHopLimit > 0 ? savedHopLimit : HopLimit;
        OkToMqtt = savedOkToMqtt;
        IgnoreMqtt = savedIgnoreMqtt;
        HomeLatitudeText = savedHomeLatitude?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty;
        HomeLongitudeText = savedHomeLongitude?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty;
        HomeAltitudeText = savedHomeAltitude?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        if (!string.IsNullOrEmpty(savedFirmwareVersion)) MyFirmwareVersion = savedFirmwareVersion;
        if (FirmwareEditionOptions.Contains(savedFirmwareEdition)) MyFirmwareEdition = savedFirmwareEdition;
        if (RebroadcastModeOptions.Contains(savedRebroadcastMode)) RebroadcastMode = savedRebroadcastMode;
        RoutingRelayEnabled = savedRoutingRelayEnabled;
        if (HomeLocationSourceOptions.Contains(savedHomeLocationSource)) HomeLocationSource = savedHomeLocationSource;
        GpsSerialPort = savedGpsSerialPort;
        GpsBaudRateText = savedGpsBaudRate > 0 ? savedGpsBaudRate.ToString(CultureInfo.InvariantCulture) : string.Empty;
        if (UnitSystems.Contains(savedUnitSystem)) UnitSystemName = savedUnitSystem;
        if (RingtoneModes.Contains(savedRingtoneMode)) RingtoneMode = savedRingtoneMode;
        RingtoneVolume = savedRingtoneVolume;
        if (!string.IsNullOrWhiteSpace(savedRingtoneRtttl)) RingtoneRtttl = savedRingtoneRtttl;
        LoadNodeFilterSettings(_settings);
        // Must precede the gate below: the SaveSettings() there writes every
        // field this app owns, so loading after it would persist the
        // compile-time defaults over the saved schedules and then read them
        // back as "off".
        LoadAutoReportSettings();
        LoadMqttSettings(_settings);
        // Before the _settingsLoaded gate below, for the same reason as the
        // auto-report load: the SaveSettings() there writes every field, so a
        // later load would persist the defaults over the saved values.
        ScriptsEnabled = _settings.ScriptsEnabled;
        ScriptsDryRun = _settings.ScriptsDryRun;

        // Explicit rather than relying on OnUnitSystemNameChanged, whose
        // generated setter no-ops when the saved value equals the default —
        // a saved "Metric" that matches the default would otherwise leave
        // dates in US form until the user toggled units.
        UiFormats.European = CurrentUnitSystem == UnitSystem.Metric;

        // Everything is loaded — from here on property changes may persist.
        _settingsLoaded = true;
        SaveSettings();

        // Arms the band check against whatever region was restored. Set here
        // rather than at the field so the region loaded above counts as the
        // starting band and doesn't itself demand confirmation.
        _acknowledgedBandRegion = SelectedRegion;

        // The bridge was deliberately not started during the load above (its
        // change handlers bail out while _settingsLoaded is false), so connect
        // once here with the complete configuration. The map report schedule
        // needs no priming — it starts due, so the first poll tick publishes.
        RefreshMqttBridge();

        HookNodeFilter();
        // After the settings gate: the engine's armed set depends on nothing
        // saved here, but ScriptsEnabled does, and arming before it was loaded
        // would leave the master switch reading as off on the first tick.
        InitScripting();
        RefreshSelfNode(); // our own row, so the configured name resolves from the first frame
        InitTelemetrySources();
        InitGps();
        RestoreSelectedTab(savedLastSelectedChannelIndex, savedSelectedConversationNode);

        // Unconditional (not relying on OnCenterFreqMHzChanged, whose
        // generated setter can no-op if CenterFreqMHz never actually
        // changed value during the loads above).
        SpectrumCenterHz = CenterFreqMHz * 1_000_000.0;

        try
        {
            _core = new MeshtasticCore();
            StatusText = $"Native bridge loaded ({Environment.OSVersion.Platform}).";
            // Push the SX1262 settings BEFORE selecting the devices. The
            // property change handlers that normally do this all guard on
            // `_core is not null`, and they ran during the settings load above
            // — while the core did not yet exist — so without this the core
            // still holds its defaults. Board in particular defaults to
            // Unspecified, which makes the stick refuse to open, and the device
            // selections below are what trigger that open.
            _core.Sx1262Board = SelectedSx1262Board;
            _core.Sx1262Serial = SelectedSx1262Serial;
            _core.TxPowerDbm = (sbyte)Math.Clamp(Sx1262TxPowerDbm, -128, 127);
            // Same reason as the board above: OnSelectedRegionChanged ran during
            // the settings load, before the core existed, so the band it would
            // have pushed never landed. Without this the core holds its
            // undeclared 0/0 and only the chip's own range is enforced.
            ApplyTxBandLimits();
            _core.SetRxDevice(SelectedDevice);
            _core.SetTxDevice(SelectedTxDevice);
            if (SelectedDevice == RadioDeviceKind.Sx1262 ||
                SelectedTxDevice == RadioDeviceKind.Sx1262)
            {
                RefreshSx1262Serials();
                var (min, max) = _core.TxPowerRangeDbm;
                Sx1262MinPowerDbm = min;
                Sx1262MaxPowerDbm = max;
                // Read back what the core actually accepted, so the slider
                // shows the clamped value rather than the request.
                Sx1262TxPowerDbm = _core.TxPowerDbm;
            }
            ApplyGains();
            ApplyTxAncillary();
            RefreshSampleRateSelection(SelectedDevice, GetSavedRxSampleRateHz(SelectedDevice));
            _rxHost.TransmitAutoReply = frame =>
            {
                // An auto-reply only exists because a frame arrived, so RX is
                // normally up by construction — but this is a direct call past
                // TransmitFrameAsync, so it carries the same gate explicitly
                // rather than relying on that.
                if (!IsRunning) return;
                var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
                try { _core.Transmit(SelectedPreset, hz, frame, TxGainDb, TxAmpEnable); }
                catch { /* best-effort auto-reply */ }
            };
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
        // Refreshed before anything reads it: the ticks below gate on RX being
        // up, and must see this poll's state rather than the previous one's.
        if (_core is not null) IsRunning = _core.IsRunning;

        // A map report is published straight to the broker and never goes over
        // the air, so it needs neither a TX-capable device nor a receiver.
        TickMapReport();
        // Ahead of the running check because neither transmits: a message sent
        // just before RX was stopped still deserves to stop saying nothing and
        // settle as failed, and an ack we still owe a peer is dropped rather
        // than left queued.
        _rxHost.SweepPendingAcks();
        // Scheduled script triggers (every:/at:). Not gated here because a
        // script's non-radio actions are still worth running; any send it
        // attempts is refused downstream like every other transmit.
        TickScripts();

        // Both of these key the transmitter unprompted, so both wait for an
        // active receiver: nothing goes on the air before the operator has
        // knowingly started RX, and nothing talks over a channel we cannot
        // hear. The core refuses them anyway — this keeps them from trying.
        if (IsRunning)
        {
            KickAutoReportTick();
            SweepAckRetransmits();
        }

        if (_core is null) return;

        DeviceStatus = $"RX: {_core.DeviceName}  TX: {_core.TxDeviceName} — {_core.DeviceStatus}";
        if (!IsRunning) return;

        RssiDbfs = _core.GetSignalStats().RssiDbfs;
        _rxHost.CurrentRssiDbfs = RssiDbfs;

        DrainDemodEvents();
    }

    private void DecodePayloadIfPossible(string ev)
    {
        if (ev.IndexOf("payload", StringComparison.Ordinal) < 0) return;
        var m = PayloadLineRegex.Match(ev);
        if (!m.Success) return;
        if (!(m.Groups["status"].Success && m.Groups["status"].Value == "OK")) return;

        var frame = HexToBytes(m.Groups["hex"].Value);
        if (frame.Length < MeshHeader.Size) return;
        if (!MeshHeader.TryParse(frame, out var header)) return;

        float? packetRssiDbm = float.IsNegativeInfinity(RssiDbfs) ? null : RssiDbfs;
        // SNR comes from the preamble that opened this frame, matching
        // MeshRF.App — it was being dropped entirely before.
        _rxRouter.ProcessReceivedFrame(frame, header, snrDb: _lastPreamblePeakDb, packetRssiDbm: packetRssiDbm);
        _lastPreamblePeakDb = null;
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

    private void ApplyTxAncillary()
    {
        if (_core is null) return;
        _core.SetDeviceOption("bias_tee", BiasTee ? 1 : 0);
        _core.SetDcBlock(DcBlockEnable);
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

    partial void OnSelectedDeviceChanged(RadioDeviceKind value)
    {
        ApplyGains();
        if (_core is not null)
        {
            _core.SetRxDevice(value);
            RefreshSampleRateSelection(value, GetSavedRxSampleRateHz(value));
            if (value == RadioDeviceKind.Sx1262) RefreshSx1262Serials();
        }
        OnPropertyChanged(nameof(IsHackRf));
        OnPropertyChanged(nameof(IsRtlSdr));
        OnPropertyChanged(nameof(IsRxSx1262));
        OnPropertyChanged(nameof(HasSpectrum));
        OnPropertyChanged(nameof(ShowSx1262SerialPicker));
        // Receiving through the stick makes Send available too, since the same
        // radio transmits.
        SendMessageCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    /// <summary>Re-reads the attached sticks. Only called when an SX1262 is
    /// selected, since enumeration claims each CH341 in turn.</summary>
    private void RefreshSx1262Serials()
    {
        if (_core is null) return;
        Sx1262Serials = _core.ListSx1262Serials();
        // Drop a saved selection for a stick that is no longer plugged in,
        // rather than failing to open it every time.
        if (SelectedSx1262Serial.Length > 0 && !Sx1262Serials.Contains(SelectedSx1262Serial))
            SelectedSx1262Serial = string.Empty;
        OnPropertyChanged(nameof(ShowSx1262SerialPicker));
    }

    partial void OnSelectedSx1262SerialChanged(string value)
    {
        if (_core is not null) _core.Sx1262Serial = value ?? string.Empty;
        SaveSettings();
    }
    partial void OnSelectedTxDeviceChanged(RadioDeviceKind value)
    {
        _core?.SetTxDevice(value);
        // Push the power through on every switch to Sx1262: the native side
        // only clamps on assignment, so a board changed while another TX
        // device was selected would otherwise leave a stale value.
        if (value == RadioDeviceKind.Sx1262)
        {
            ApplySx1262Power();
            RefreshSx1262Serials();
        }
        OnPropertyChanged(nameof(IsTxHackRf));
        OnPropertyChanged(nameof(IsTxSx1262));
        OnPropertyChanged(nameof(ShowSx1262SerialPicker));
        OnPropertyChanged(nameof(ShowSx1262BoardPrompt));
        OnPropertyChanged(nameof(ShowSx1262PowerWarning));
        OnPropertyChanged(nameof(ShowSx1262BandWarning));
        // Send is gated on CanTransmit, which this switch can flip in either
        // direction — an SX1262 that opened makes it true where a bare TX
        // selection change previously left the button stale.
        SendMessageCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    partial void OnSelectedSx1262BoardChanged(Sx1262Board value)
    {
        if (_core is not null)
        {
            _core.Sx1262Board = value;
            // Choosing a board is what opens the transmitter, so this is also
            // where Send becomes available.
            if (value != Sx1262Board.Unspecified)
            {
                var (min, max) = _core.TxPowerRangeDbm;
                Sx1262MinPowerDbm = min;
                Sx1262MaxPowerDbm = max;
                // Moving from a MeshToad to a MeshStick has to pull an
                // out-of-range 30 dBm back down, or the slider would sit past
                // its own maximum.
                Sx1262TxPowerDbm = Math.Clamp(Sx1262TxPowerDbm, min, max);
                ApplySx1262Power();
            }
            SendMessageCommand.NotifyCanExecuteChanged();
        }
        OnPropertyChanged(nameof(IsSx1262BoardChosen));
        OnPropertyChanged(nameof(ShowSx1262BoardPrompt));
        OnPropertyChanged(nameof(ShowSx1262PowerWarning));
        SaveSettings();
    }

    partial void OnSx1262TxPowerDbmChanged(int value)
    {
        ApplySx1262Power();
        OnPropertyChanged(nameof(ShowSx1262PowerWarning));
        SaveSettings();
    }

    /// <summary>Writes the requested antenna-port power to the core and reads
    /// back what it actually accepted, so the UI shows the clamped value rather
    /// than the request.</summary>
    private void ApplySx1262Power()
    {
        if (_core is null) return;
        _core.TxPowerDbm = (sbyte)Math.Clamp(Sx1262TxPowerDbm, -128, 127);
        var applied = _core.TxPowerDbm;
        if (applied != Sx1262TxPowerDbm) Sx1262TxPowerDbm = applied;
    }

    partial void OnSelectedRxSampleRateChanged(SampleRateOption? value)
    {
        if (_suppressSampleRateUpdate || value is null) return;
        _core?.SetDeviceOption("rx_sample_rate_hz", checked((int)value.Hz));
        StoreSavedRxSampleRateHz(SelectedDevice, value.Hz);
        SpectrumSpanHz = value.Hz;
        SaveSettings();
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
        IEnumerable<uint> rates = maxRateHz > 0 ? baseRates.Where(r => r <= maxRateHz) : baseRates;
        return rates.Select(r => new SampleRateOption(r, FormatSampleRateLabel(r))).ToArray();
    }

    private void RefreshSampleRateSelection(RadioDeviceKind kind, uint requestedHz)
    {
        SampleRateOptions = BuildRxSampleRateOptions(kind);
        _suppressSampleRateUpdate = true;
        try { SelectedRxSampleRate = SelectNearestSampleRate(SampleRateOptions, requestedHz); }
        finally { _suppressSampleRateUpdate = false; }

        if (SelectedRxSampleRate is { } opt)
        {
            _core?.SetDeviceOption("rx_sample_rate_hz", checked((int)opt.Hz));
            SpectrumSpanHz = opt.Hz;
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
        if (requestedHz == 0) return options.FirstOrDefault(o => o.Hz == 2_400_000u) ?? options[0];

        var best = options[0];
        var bestDelta = AbsDiff(best.Hz, requestedHz);
        for (int i = 1; i < options.Count; i++)
        {
            var delta = AbsDiff(options[i].Hz, requestedHz);
            if (delta < bestDelta) { best = options[i]; bestDelta = delta; }
        }
        return best;
    }

    private static ulong AbsDiff(uint left, uint right) => left >= right ? (ulong)(left - right) : (ulong)(right - left);

    private static string FormatSampleRateLabel(uint hz) =>
        $"{(hz / 1_000_000.0).ToString("0.###", CultureInfo.InvariantCulture)} MS/s";

    /// <summary>Each device kind remembers its own last-used sample rate,
    /// mirroring MeshRF.App's per-device storage (shared settings.json).</summary>
    private uint GetSavedRxSampleRateHz(RadioDeviceKind kind) => kind switch
    {
        RadioDeviceKind.HackRf => _settings.HackRfRxSampleRateHz != 2_400_000u || _settings.RxSampleRateHz == 2_400_000u
            ? _settings.HackRfRxSampleRateHz : _settings.RxSampleRateHz,
        RadioDeviceKind.RtlSdr => _settings.RtlSdrRxSampleRateHz != 2_400_000u || _settings.RxSampleRateHz == 2_400_000u
            ? _settings.RtlSdrRxSampleRateHz : _settings.RxSampleRateHz,
        _ => _settings.RxSampleRateHz,
    };

    private void StoreSavedRxSampleRateHz(RadioDeviceKind kind, uint hz)
    {
        switch (kind)
        {
            case RadioDeviceKind.HackRf: _settings.HackRfRxSampleRateHz = hz; break;
            case RadioDeviceKind.RtlSdr: _settings.RtlSdrRxSampleRateHz = hz; break;
        }
        _settings.RxSampleRateHz = hz;
    }

    partial void OnSelectedPresetChanged(LoraPreset value)
    {
        // Autofill SF/BW/CR from the new preset — preset is the anchor, so
        // overwriting any prior manual override here is the right UX.
        ApplyPresetToLoraParams(value);
        RebuildSlots(snapToDefault: true);
        // An unnamed default primary channel is named after the preset, so it
        // has to follow the preset when that changes.
        _rxHost.SyncPrimaryChannelName(value);
        SaveSettings();
    }

    partial void OnSelectedRegionChanged(Region value)
    {
        RebuildSlots(snapToDefault: true);
        ApplyTxBandLimits();
        // A move to a band that doesn't touch the one we were operating in has
        // to be confirmed. Only once the constructor has established a starting
        // band: restoring a saved region is not a change. Nothing to confirm
        // either when the bands overlap, which covers every ordinary retune.
        if (_acknowledgedBandRegion is { } previous)
        {
            if (ChannelPlan.BandsOverlap(previous, value))
            {
                _acknowledgedBandRegion = value;
                BandChangeNeedsAck = false;
            }
            else
            {
                BandChangeNeedsAck = true;
            }
        }
        OnPropertyChanged(nameof(Sx1262BandWarningText));
        OnPropertyChanged(nameof(ShowSx1262BandWarning));
        SaveSettings();
    }

    /// <summary>Tells the core which band the selected region permits, so the
    /// SX1262 driver can refuse a transmit outside it. Receive is unaffected.
    /// </summary>
    private void ApplyTxBandLimits()
    {
        if (_core is null) return;
        var range = ChannelPlan.Range(SelectedRegion);
        _core.TxBandLimitsHz = (
            (ulong)Math.Round(range.FreqStartMHz * 1_000_000.0),
            (ulong)Math.Round(range.FreqEndMHz * 1_000_000.0));
    }

    partial void OnSelectedSlotChanged(int value)
    {
        if (_suppressSlotSync || value <= 0) return;
        CenterFreqMHz = ChannelPlan.FrequencyMHz(SelectedRegion, SelectedPreset, value);
        SaveSettings();
    }

    partial void OnOverrideSfChanged(byte value)      { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); SaveSettings(); } }
    partial void OnOverrideBwKhzChanged(double value) { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); SaveSettings(); } }
    partial void OnOverrideCrChanged(byte value)      { if (!_suppressLoraParamSync) { OnPropertyChanged(nameof(IsCustomLoraParams)); SaveSettings(); } }

    partial void OnCenterFreqMHzChanged(double value)
    {
        if (!_suppressRetune) SaveSettings();
        SpectrumCenterHz = value * 1_000_000.0;
    }

    partial void OnLnaGainDbChanged(byte value) { ApplyGains(); SaveSettings(); }
    partial void OnVgaGainDbChanged(byte value) { ApplyGains(); SaveSettings(); }
    partial void OnAmpEnableChanged(bool value) { ApplyGains(); SaveSettings(); }
    partial void OnRtlGainDbChanged(byte value) { ApplyGains(); SaveSettings(); }
    partial void OnRtlAgcEnableChanged(bool value) { ApplyGains(); SaveSettings(); }
    partial void OnBiasTeeChanged(bool value) { _core?.SetDeviceOption("bias_tee", value ? 1 : 0); SaveSettings(); }
    partial void OnDcBlockEnableChanged(bool value) { _core?.SetDcBlock(value); SaveSettings(); }
    partial void OnTxGainDbChanged(byte value) => SaveSettings();
    partial void OnTxAmpEnableChanged(bool value) => SaveSettings();
    partial void OnWaterfallColormapChanged(WaterfallColormap value) => SaveSettings();
    partial void OnWaterfallRowsPerSecondChanged(double value) => SaveSettings();
    partial void OnWaterfallFloorDbChanged(double value) => SaveSettings();
    partial void OnWaterfallCeilDbChanged(double value) => SaveSettings();
    partial void OnWaterfallAutoLevelsChanged(bool value) => SaveSettings();
    partial void OnPendingReplyPacketIdChanged(uint value) => OnPropertyChanged(nameof(HasPendingReply));

    partial void OnMyNodeIdTextChanged(string value)
    {
        var parsed = ParseNodeId(value);
        if (parsed != 0 && parsed != 0xFFFFFFFFu)
        {
            _rxHost.UpdateMyNodeNum(parsed);
            _settings.UserNodeNum = parsed;
        }
        OnPropertyChanged(nameof(MyMacAddress)); // derived from the node number
        SaveSettings();
        RefreshSelfNode();
    }

    private static uint ParseNodeId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var s = text.Trim();
        if (s.StartsWith('!')) s = s[1..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            return dec;
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : 0;
    }

    [RelayCommand]
    private void GenerateRandomNodeId()
    {
        uint id;
        do { id = (uint)Random.Shared.NextInt64(1, 0xFFFFFFFE); } while (id == 0 || id == 0xFFFFFFFFu);
        MyNodeIdText = $"!{id:x8}";
    }

    [RelayCommand]
    private void GenerateKeyPair()
    {
        var priv = Curve25519.GeneratePrivateKey();
        var pub = Curve25519.GetPublicKey(priv);
        MyPrivateKey = Convert.ToBase64String(priv);
        MyPublicKey = Convert.ToBase64String(pub);
    }

    // Identity edits have to reach the node store too, not just settings.json.
    partial void OnMyLongNameChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnMyShortNameChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnMyRoleChanged(string value)
    {
        ApplyRoleDefaults(value);
        SaveSettings();
        RefreshSelfNode();
    }

    /// <summary>
    /// Coerce the schedules and rebroadcast mode the way firmware's
    /// <c>installRoleDefaults</c> does, so a role means the same thing on air
    /// here as it does on a real node.
    ///
    /// Skipped while settings are still loading: the constructor assigns the
    /// saved role, and applying defaults there would overwrite the intervals
    /// the user tuned with the role's canned ones on every launch.
    /// </summary>
    private void ApplyRoleDefaults(string? role)
    {
        if (!_settingsLoaded) return;

        var d = RoleDefaults.For(role);
        if (d.NodeInfoEnabled is bool ni) AutoReportNodeInfoEnabled = ni;
        if (d.NodeInfoSeconds is int nis) AutoReportNodeInfoSeconds = nis;
        if (d.PositionEnabled is bool p) AutoReportPositionEnabled = p;
        if (d.PositionSeconds is int ps) AutoReportPositionSeconds = ps;
        if (d.DeviceMetricsEnabled is bool dm) AutoReportDeviceMetricsEnabled = dm;
        if (d.DeviceMetricsSeconds is int dms) AutoReportDeviceMetricsSeconds = dms;
        if (d.EnvironmentMetricsEnabled is bool em) AutoReportEnvironmentMetricsEnabled = em;
        if (d.EnvironmentMetricsSeconds is int ems) AutoReportEnvironmentMetricsSeconds = ems;
        if (d.AirQualityMetricsEnabled is bool aq) AutoReportAirQualityMetricsEnabled = aq;
        if (d.NodeStatusEnabled is bool st) AutoReportNodeStatusEnabled = st;
        if (d.IsUnmessagable is bool um) MyIsUnmessagable = um;
        if (d.RebroadcastMode is string rb && RebroadcastModeOptions.Contains(rb)) RebroadcastMode = rb;
    }
    partial void OnMyHwModelChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnMyIsLicensedChanged(bool value)
    {
        SaveSettings();
        RefreshSelfNode();
        OnPropertyChanged(nameof(LicensedEncryptedChannelWarning));
        OnPropertyChanged(nameof(HasLicensedEncryptedChannelWarning));
    }
    partial void OnMyIsUnmessagableChanged(bool value) { SaveSettings(); RefreshSelfNode(); }
    // The self node record embeds the public key and derives our node number
    // from it, so a key change has to reach the node store like any other
    // identity edit — otherwise the grid keeps reporting the old key's
    // derived-node-number match.
    partial void OnMyPublicKeyChanged(string value) { SaveSettings(); RefreshSelfNode(); }

    partial void OnMyPrivateKeyChanged(string value)
    {
        SyncPublicKeyToPrivateKey();
        SaveSettings();
    }

    /// <summary>
    /// Re-derive the public key whenever the private key changes. The private
    /// key is editable so an identity can be imported from a real node, and the
    /// public key is a pure function of it — leaving a stale one behind breaks
    /// PKI messaging silently in both directions, since the stale key is what
    /// NodeInfo advertises for peers to encrypt to while we decrypt with the
    /// new private key. Nothing on the air reports that mismatch.
    ///
    /// A private key that is absent or not 32 bytes leaves the pair untouched:
    /// that is a field mid-edit, not an instruction to clear the public key.
    /// </summary>
    private void SyncPublicKeyToPrivateKey()
    {
        if (!Curve25519.TryGetPublicKeyBase64(MyPrivateKey, out var derived)) return;
        if (!string.Equals(derived, MyPublicKey, StringComparison.Ordinal))
            MyPublicKey = derived;
    }
    partial void OnMyNodeStatusChanged(string value) { SaveSettings(); RefreshSelfNode(); }
    partial void OnHopLimitChanged(int value) => SaveSettings();
    partial void OnOkToMqttChanged(bool value) => SaveSettings();

    partial void OnIgnoreMqttChanged(bool value)
    {
        // The host owns the relay gate, so the flag is pushed rather than
        // pulled — including here during settings load, which is what applies
        // a persisted "on" before the first packet arrives.
        _rxHost.IgnoreMqttNodes = value;
        SaveSettings();
    }

    // The map's home marker reads these, so a manual edit has to redraw it.
    partial void OnHomeLatitudeTextChanged(string value) { SaveSettings(); RaiseMapDataChanged(); }
    partial void OnHomeLongitudeTextChanged(string value) { SaveSettings(); RaiseMapDataChanged(); }
    partial void OnHomeAltitudeTextChanged(string value) => SaveSettings();
    partial void OnMyFirmwareVersionChanged(string value) => SaveSettings();
    partial void OnMyFirmwareEditionChanged(string value) => SaveSettings();
    partial void OnRebroadcastModeChanged(string value) => SaveSettings();
    partial void OnRoutingRelayEnabledChanged(bool value) => SaveSettings();
    // Port/baud edits restart the reader so a correction takes effect without
    // toggling the source.
    partial void OnGpsSerialPortChanged(string value) { ApplyLocationSource(startOrStop: true); SaveSettings(); }
    partial void OnGpsBaudRateTextChanged(string value) { ApplyLocationSource(startOrStop: true); SaveSettings(); }

    partial void OnHomeLocationSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsUsbSerialLocationSource));
        OnPropertyChanged(nameof(IsManualLocationSource));
        ApplyLocationSource(startOrStop: true);
        SaveSettings();
    }

    /// <summary>
    /// Re-renders everything whose display depends on the unit system, so a
    /// metric/imperial toggle takes effect immediately instead of waiting for
    /// the next packet or window reopen. Three mechanisms, matched to how each
    /// surface renders: all-properties notifications for rows whose display
    /// properties compute on read (chat bubbles, node and waypoint grids),
    /// a store reload for history points whose strings are pre-rendered at
    /// build time, and explicit rebuilds for stored summary strings.
    /// </summary>
    private void RefreshUnitDependentDisplays()
    {
        foreach (var tab in Tabs)
        {
            switch (tab)
            {
                case ChannelTabViewModel channel:
                    foreach (var m in channel.Messages) m.NotifyDisplayChanged();
                    break;
                case ConversationTabViewModel convo:
                    foreach (var m in convo.Messages) m.NotifyDisplayChanged();
                    convo.RefreshNodeSnapshot();    // temp/pressure/last-heard panel
                    convo.ReloadHistoryDisplays();  // pre-rendered strings need a rebuild
                    break;
            }
        }

        foreach (var node in _rxHost.Nodes) node.NotifyChanged();
        foreach (var wp in _rxHost.Waypoints) wp.NotifyChanged();
        UpdateAutoReportSummary();
    }

    partial void OnUnitSystemNameChanged(string value)
    {
        // Metric mode uses European date/time conventions everywhere dates
        // are rendered (grids, chat bubbles, log stamps, history windows).
        UiFormats.European = CurrentUnitSystem == UnitSystem.Metric;
        // Skipped mid-construction: nothing is rendered yet, and the explicit
        // UiFormats sync after settings load covers the initial state.
        if (_settingsLoaded) RefreshUnitDependentDisplays();
        OnPropertyChanged(nameof(CurrentUnitSystem));
        OnPropertyChanged(nameof(UseImperial));
        OnPropertyChanged(nameof(UseFahrenheit));
        OnPropertyChanged(nameof(UseMiles));
        OnPropertyChanged(nameof(HomeAltitudeLabel));
        OnPropertyChanged(nameof(NodeDistanceUnitShort));
        SaveSettings();
    }

    /// <summary>Reselect the tab that was active last session: the saved DM
    /// conversation if it reopened, otherwise the saved channel index.</summary>
    private void RestoreSelectedTab(int channelIndex, uint conversationNode)
    {
        if (conversationNode != 0)
        {
            var convo = Tabs.OfType<ConversationTabViewModel>().FirstOrDefault(t => t.NodeNum == conversationNode);
            if (convo is not null) { SelectedTab = convo; return; }
        }
        var channel = Tabs.OfType<ChannelTabViewModel>().FirstOrDefault(t => t.Config.Index == channelIndex)
                      ?? Tabs.OfType<ChannelTabViewModel>().FirstOrDefault();
        if (channel is not null) SelectedTab = channel;
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(CanSelectRxSampleRate));
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    private void SaveSettings()
    {
        // Mirrors MeshRF.App's _settingsLoaded guard. Assigning the view
        // model's properties during construction fires OnXxxChanged ->
        // SaveSettings() cascades; without this gate those mid-construction
        // saves write whatever fields haven't been loaded from disk yet
        // (still at their compile-time defaults) straight back over the real
        // saved values — which is how a saved short name got overwritten with
        // the default "MRF". Nothing persists until the load finishes.
        if (!_settingsLoaded) return;

        // Read-modify-write against the FILE, not our startup snapshot.
        // settings.json is shared with MeshRF.App, and Save() serializes the
        // whole object — so writing our own copy would silently revert every
        // field MeshRF.App changed since we launched (map centre/zoom, hardware
        // model, hop limit, ...). Only the fields this app owns are applied on
        // top of what's currently on disk. MeshRF.App's SaveLayout does the same.
        var disk = AppSettings.Load();
        ApplyOwnedSettings(disk);
        disk.Save();
        // Keep the in-memory copy in step for any later reads.
        ApplyOwnedSettings(_settings);
    }

    /// <summary>Copy the settings this app is the source of truth for onto
    /// <paramref name="s"/>. Everything else on <paramref name="s"/> is left
    /// exactly as loaded, so concurrent MeshRF.App edits survive.</summary>
    private void ApplyOwnedSettings(AppSettings settings)
    {
        var _settings = settings; // shadow so the assignments below read naturally
        _settings.RxDeviceKind = SelectedDevice.ToString();
        _settings.TxDeviceKind = SelectedTxDevice.ToString();
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
        _settings.BiasTee = BiasTee;
        _settings.TxGainDb = TxGainDb;
        _settings.TxAmpEnable = TxAmpEnable;
        _settings.Sx1262Board = SelectedSx1262Board.ToString();
        _settings.Sx1262Serial = SelectedSx1262Serial;
        _settings.Sx1262TxPowerDbm = (sbyte)Math.Clamp(Sx1262TxPowerDbm, -128, 127);
        _settings.DcBlockEnable = DcBlockEnable;
        _settings.WaterfallColormap = WaterfallColormap.ToString();
        _settings.WaterfallFloorDb = WaterfallFloorDb;
        _settings.WaterfallCeilDb = WaterfallCeilDb;
        _settings.WaterfallAutoLevels = WaterfallAutoLevels;
        _settings.WaterfallRowsPerSecond = WaterfallRowsPerSecond;
        StoreNodeFilterSettings(_settings);
        _settings.UserFirmwareVersion = MyFirmwareVersion;
        _settings.UserFirmwareEdition = MyFirmwareEdition;
        _settings.RebroadcastMode = RebroadcastMode;
        _settings.RoutingRelayEnabled = RoutingRelayEnabled;
        _settings.HomeLocationSource = HomeLocationSource;
        _settings.GpsSerialPort = GpsSerialPort;
        _settings.GpsBaudRate = int.TryParse(GpsBaudRateText, out var baud) ? baud : 0;
        _settings.RingtoneMode = RingtoneMode;
        _settings.RingtoneVolume = (int)Math.Round(RingtoneVolume);
        _settings.RingtoneRtttl = RingtoneRtttl;
        _settings.MutedRingtoneChannels = Tabs.OfType<ChannelTabViewModel>()
            .Where(t => t.MuteRtttl).Select(t => t.Config.Index).ToList();
        _settings.MapNodeLabelMode = MapNodeLabelMode;
        StoreAutoReportSettings();
        _settings.ScriptsEnabled = ScriptsEnabled;
        _settings.ScriptsDryRun = ScriptsDryRun;
        // Must be here, not merely mutated in place: SaveSettings writes a copy
        // freshly loaded from the file, so anything not applied here is silently
        // replaced by whatever was on disk. Credentials edited in the dialog
        // looked fine all session and vanished on restart without this line.
        _settings.ScriptCredentials = ScriptCredentials;
        _settings.UnitSystem = UnitSystemName;
        _settings.UseFahrenheit = UseFahrenheit;
        _settings.UseMiles = UseMiles;
        _settings.LastSelectedChannelIndex = _lastSelectedChannelIndex;
        _settings.SelectedChannelIndex = _lastSelectedChannelIndex;
        _settings.SelectedConversationNode = SelectedTab is ConversationTabViewModel c ? c.NodeNum : 0;
        _settings.UserNodeNum = _rxHost.MyNodeNum;
        _settings.UserLongName = MyLongName;
        _settings.UserShortName = MyShortName;
        _settings.UserRole = MyRole;
        _settings.UserIsLicensed = MyIsLicensed;
        _settings.UserIsUnmessagable = MyIsUnmessagable;
        _settings.UserHwModel = MyHwModel;
        _settings.UserPublicKey = MyPublicKey;
        _settings.UserPrivateKey = MyPrivateKey;
        _settings.UserNodeStatus = MyNodeStatus;
        _settings.HopLimit = HopLimit;
        _settings.OkToMqtt = OkToMqtt;
        _settings.IgnoreMqtt = IgnoreMqtt;
        _settings.HomeLatitude = double.TryParse(HomeLatitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ? lat : null;
        _settings.HomeLongitude = double.TryParse(HomeLongitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ? lon : null;
        _settings.HomeAltitude = int.TryParse(HomeAltitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var alt) ? alt : null;
        SaveMqttSettings(_settings);
    }

    private void SaveOpenConversations()
    {
        if (!_settingsLoaded) return; // Same gate as SaveSettings.
        // Read-modify-write, for the same reason as SaveSettings.
        var disk = AppSettings.Load();
        // Taken from the tab strip, not the host's lookup dictionary: this list
        // is what restores the tabs, so it has to carry the order the user
        // dragged them into. Dictionary key order would not.
        disk.OpenConversations = Tabs.OfType<ConversationTabViewModel>()
            .Select(t => t.NodeNum)
            .ToList();
        disk.Save();
        _settings.OpenConversations = disk.OpenConversations;
    }

    private uint NextPacketId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    /// <summary>Appends a log line from whichever thread the caller is on.
    /// LogLines is bound to the UI, so background callers must be marshalled.</summary>
    private void LogFromAnyThread(string line)
    {
        if (Dispatcher.UIThread.CheckAccess()) _rxHost.Log(line);
        else Dispatcher.UIThread.Post(() => _rxHost.Log(line));
    }

    /// <summary>Transmit a pre-built frame using the currently selected
    /// preset/frequency/TX gain settings. Runs off the UI thread.</summary>
    /// <summary>Transmits one frame, serialised against every other send and
    /// deferred until the channel is idle. Never call Core.Transmit directly —
    /// concurrent sends race on the shared native handle, and keying up during
    /// a reception collides with the traffic being received.</summary>
    private async Task<bool> TransmitFrameAsync(byte[] frame)
    {
        if (_core is null) return false;
        // Refused here as well as in the core so the reason is a sentence rather
        // than a failed send, and so nothing occupies the TX semaphore or waits
        // for a channel-idle opportunity that a stopped receiver can never
        // report.
        if (!IsRunning)
        {
            StatusText = "Start RX before transmitting — nothing is sent while the receiver is stopped.";
            return false;
        }
        if (LicensedChannelBlocking(frame) is { } blockedChannel)
        {
            StatusText = $"Licensed mode: \"{blockedChannel}\" still has a PSK — encryption isn't permitted on amateur bands. Clear its key to transmit.";
            return false;
        }
        // Band checks, both SX1262-only: the HackRF's front end is broadband,
        // so an off-band frequency there costs efficiency rather than hardware.
        // The driver enforces the same limits natively — this is here so the
        // refusal names the region and arrives before the send is queued.
        if (IsTxSx1262)
        {
            if (BandChangeNeedsAck)
            {
                StatusText = $"Confirm the band change to {SelectedRegion} before transmitting — " +
                             "a stick built for another band can be damaged by transmitting here.";
                return false;
            }
            if (!ChannelPlan.Contains(SelectedRegion, CenterFreqMHz))
            {
                var r = ChannelPlan.Range(SelectedRegion);
                StatusText = $"{CenterFreqMHz:0.###} MHz is outside {SelectedRegion} " +
                             $"({r.FreqStartMHz:0.###}–{r.FreqEndMHz:0.###} MHz) — " +
                             "pick a slot, or change region if that's really your band.";
                return false;
            }
        }
        var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
        var preset = SelectedPreset; var gain = TxGainDb; var amp = TxAmpEnable;

        await _txSemaphore.WaitAsync().ConfigureAwait(false);
        bool sent;
        try
        {
            await WaitForTxOpportunityAsync().ConfigureAwait(false);
            sent = await Task.Run(() => _core.Transmit(preset, hz, frame, gain, amp))
                             .ConfigureAwait(true);
        }
        finally
        {
            _txSemaphore.Release();
        }

        // Every send in the app funnels through here, so this is the one place
        // self-originated traffic needs to be offered to MQTT — firmware
        // uplinks its own packets from Router::send for the same reason.
        //
        // Posted rather than called inline: the ConfigureAwait(false) above
        // drops the UI context, and TransmitBackground enters this from a
        // thread-pool thread anyway, so the uplink would otherwise read the
        // channel tabs and append log lines off the UI thread.
        if (sent) Dispatcher.UIThread.Post(() => UplinkSelfOriginatedIfEligible(frame));
        return sent;
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        if (_core is null || string.IsNullOrWhiteSpace(MessageText)) return;

        ObservableCollection<ChannelMessage>? messages;
        ChannelConfig? channel;
        uint to = 0xFFFFFFFFu;

        switch (SelectedTab)
        {
            case ChannelTabViewModel chanTab:
                messages = chanTab.Messages;
                channel = chanTab.Config;
                break;
            case ConversationTabViewModel convoTab:
                messages = convoTab.Messages;
                to = convoTab.NodeNum;
                // The channel is only needed for the legacy fallback.
                channel = Tabs.OfType<ChannelTabViewModel>().FirstOrDefault()?.Config;
                break;
            default:
                return;
        }

        if (await SendTextAsync(channel, to, MessageText.Trim(),
                                PendingReplyPacketId, PendingReplyContext, messages) is false)
            return;

        MessageText = string.Empty;
        CancelReply();
    }

    /// <summary>
    /// Sends one text message and records it, independently of what the UI has
    /// selected.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="SendMessageAsync"/> so automation scripts send
    /// through exactly the same path the compose box does — duty-cycle gating,
    /// licensed-mode blocking, MQTT uplink, delivery tracking and history
    /// persistence all come along, rather than being reimplemented (and
    /// eventually diverging) on a second send path.
    /// </remarks>
    /// <param name="channel">Channel to send on, or the fallback channel for a
    /// legacy DM. May be null only when the message is PKC-sealed.</param>
    /// <param name="to">Destination node, or 0xFFFFFFFF to broadcast.</param>
    /// <param name="replyId">Packet to thread under, or 0.</param>
    /// <param name="replyContext">Quote line shown above the echoed bubble.</param>
    /// <param name="messages">Bubble list to echo into, or null to send without
    /// a visible conversation (a script answering a channel it has no tab for).</param>
    private async Task<bool> SendTextAsync(
        ChannelConfig? channel,
        uint to,
        string text,
        uint replyId,
        string replyContext,
        ObservableCollection<ChannelMessage>? messages)
    {
        if (_core is null || text.Length == 0) return false;

        // A direct message must be PKC-sealed. Firmware rejects a text message
        // addressed to it that decrypted with the channel PSK outright
        // ("Rejecting legacy DM", Router.cpp) unless the node is licensed, so a
        // legacy unicast DM is silently dropped by the peer.
        byte[] myPriv = Array.Empty<byte>(), peerPub = Array.Empty<byte>();
        bool usePkc = false;

        if (to != 0xFFFFFFFFu)
        {
            myPriv = TryParseKeyBase64(MyPrivateKey);
            peerPub = TryParseHex(_rxHost.PublicKeyHexFor(to));
            // Licensed operation rules PKC out entirely (firmware
            // wouldEncryptWithPKC), so a DM falls back to the plaintext
            // legacy form — which is the only lawful one on ham bands.
            usePkc = !MyIsLicensed && myPriv.Length == 32 && peerPub.Length == 32;
        }
        if (!usePkc && channel is null) return false;

        var packetId = NextPacketId();

        var frame = usePkc
            ? MeshEncoder.EncodePkcTextMessage(
                _rxHost.MyNodeNum, to, packetId, text, myPriv, peerPub,
                hopLimit: (byte)HopLimit, wantAck: true, replyId: replyId, okToMqtt: OkToMqtt)
            : MeshEncoder.EncodeTextMessage(
                channel!, _rxHost.MyNodeNum, packetId, text, to: to,
                hopLimit: (byte)HopLimit, wantAck: to != 0xFFFFFFFFu,
                replyId: replyId, okToMqtt: OkToMqtt);

        bool ok = await TransmitFrameAsync(frame);
        if (!ok)
        {
            StatusText = "Failed to transmit (no TX-capable device selected?).";
            return false;
        }

        // Echo locally — we won't decode our own transmission back off the
        // air (MeshRxRouter treats hearing it as isFromUs and drops it).
        var sent = new ChannelMessage
        {
            // Our configured name, not the raw node ID — this is the label the
            // user sees against their own messages, and it must match what
            // history replay resolves to.
            FromId = _rxHost.NodeDisplayName(_rxHost.MyNodeNum),
            SenderNodeNum = _rxHost.MyNodeNum,
            Text = replyId != 0 ? $"{replyContext}\n{text}" : text,
            PacketId = packetId,
            IsOutgoing = true,
            IsReplyLinked = replyId != 0,
            ReplyTargetFound = replyId != 0,
            ReplyToPacketId = replyId,
            Delivery = MessageDelivery.Sent,
        };
        messages?.Add(sent);
        // Both kinds are tracked, but they settle on different evidence — a DM
        // on the recipient's ROUTING reply, a channel message on hearing a
        // neighbour relay it. See AvaloniaMeshRxHost.PendingAck.
        _rxHost.TrackPendingAck(sent, broadcast: to == 0xFFFFFFFFu);
        // Persist the raw body (not the quoted display text) so a reload
        // rebuilds the quote from reply_id, exactly like MeshRF.App.
        // "PKC" is the channel name the router reports for PKC traffic, so
        // history reloads classify these the same way received ones are.
        _rxHost.PersistOutgoingText(to, packetId, text,
                                    usePkc ? "PKC" : channel!.Name, replyId);
        return true;
    }

    private bool CanSendMessage() =>
        _core?.CanTransmit == true && IsRunning &&
        SelectedTab is not null && !string.IsNullOrWhiteSpace(MessageText);

    partial void OnMessageTextChanged(string value) => SendMessageCommand.NotifyCanExecuteChanged();
    partial void OnSelectedTabChanged(ITabItem? value)
    {
        // Looking at the tab is what marks its activity seen; without this the
        // header would keep pulsing forever once anything arrived.
        if (value is not null) value.TabNeedsAttention = false;
        if (value is not null) _previousTab = value;
        if (value is ChannelTabViewModel ch) _lastSelectedChannelIndex = ch.Config.Index;
        SendMessageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveSelectedChannel));
        RemoveSelectedChannelCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    [RelayCommand]
    private void MessageNode(NodeRecord? node)
    {
        if (node is null) return;
        SelectedTab = _rxHost.OpenConversation(node.NodeNum);
    }

    [RelayCommand]
    private void ReplyToMessage(ChannelMessage? target)
    {
        if (target is null || target.PacketId == 0) return;
        PendingReplyPacketId = target.PacketId;
        var from = string.IsNullOrWhiteSpace(target.FromId) ? "unknown" : target.FromId;
        var preview = (target.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (preview.Length > 80) preview = preview[..80] + "...";
        if (preview.Length == 0) preview = "(empty)";
        PendingReplyContext = $"replying to {from}: \"{preview}\"";
    }

    [RelayCommand]
    private void CancelReply()
    {
        PendingReplyPacketId = 0;
        PendingReplyContext = string.Empty;
    }

    /// <summary>Send an emoji tapback for <paramref name="target"/>. Rides the
    /// same TEXT_MESSAGE_APP port as a normal message: the glyph is the
    /// payload text, reply_id points at the target packet, and the emoji
    /// flag (Data.emoji=1) marks it as a reaction rather than a reply.</summary>
    public async Task SendReactionAsync(ChannelMessage? target, string emoji)
    {
        if (_core is null || target is null || target.PacketId == 0 || string.IsNullOrEmpty(emoji)) return;

        ChannelConfig channel;
        uint to = 0xFFFFFFFFu;
        switch (SelectedTab)
        {
            case ChannelTabViewModel chanTab: channel = chanTab.Config; break;
            case ConversationTabViewModel convoTab:
                var primary = Tabs.OfType<ChannelTabViewModel>().FirstOrDefault();
                if (primary is null) return;
                channel = primary.Config;
                to = convoTab.NodeNum;
                break;
            default: return;
        }

        var packetId = NextPacketId();
        var frame = MeshEncoder.EncodeTextMessage(channel, _rxHost.MyNodeNum, packetId, emoji,
            to: to, replyId: target.PacketId, emoji: 1);

        if (await TransmitFrameAsync(frame))
        {
            target.AddReaction(emoji, _rxHost.NodeDisplayName(_rxHost.MyNodeNum));
            _rxHost.PersistOutgoingReaction(to, packetId, target.PacketId, emoji, channel.Name);
        }
        else
        {
            StatusText = "Failed to transmit reaction.";
        }
    }

    [RelayCommand]
    private void AddChannel() => SelectedTab = _rxHost.AddChannel();

    public bool CanRemoveSelectedChannel => SelectedTab is ChannelTabViewModel { Config.Role: not ChannelRole.Primary };

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedChannel))]
    private void RemoveSelectedChannel()
    {
        if (SelectedTab is not ChannelTabViewModel channel) return;
        if (_rxHost.RemoveChannel(channel))
            SelectedTab = Tabs.OfType<ChannelTabViewModel>().FirstOrDefault();
    }

    /// <summary>Persists edits made in the channel Settings dialog.</summary>
    public void SaveChannelSettings(ChannelTabViewModel channel)
    {
        _rxHost.SaveChannelConfig(channel);
        // Clearing the primary's name, or promoting a different channel, can
        // leave it eligible to inherit the preset name again.
        _rxHost.SyncPrimaryChannelName(SelectedPreset);
        // MuteRtttl lives in settings.json, not the channel store, so the
        // dialog's Save has to flush settings too.
        SaveSettings();
        // A channel's name or downlink flag decides which broker topics we
        // subscribe to, so the bridge has to re-evaluate its subscriptions.
        RefreshMqttBridge();
    }

    [RelayCommand]
    private void CloseTab(ITabItem? tab)
    {
        if (tab is not ConversationTabViewModel convo) return;
        if (ReferenceEquals(SelectedTab, convo))
        {
            var restoreTo = _previousTab is not null && !ReferenceEquals(_previousTab, convo) && Tabs.Contains(_previousTab)
                ? _previousTab
                : Tabs.OfType<ChannelTabViewModel>().FirstOrDefault();
            if (restoreTo is not null) SelectedTab = restoreTo;
        }
        _rxHost.CloseConversation(convo);
    }

    // ----- Node context-menu actions -----

    /// <summary>A TX-capable device, an identity to send as, and an active
    /// receiver. RX is required because nothing should key up before the
    /// operator has knowingly put the node on the air, and because without a
    /// receiver there is no listen-before-talk. The core enforces the same rule
    /// so the unsolicited senders cannot slip past it.</summary>
    private bool CanTransmit =>
        _core?.CanTransmit == true && IsRunning && _rxHost.MyNodeNum != 0;

    private ChannelConfig? PrimaryChannel() =>
        (Tabs.OfType<ChannelTabViewModel>().FirstOrDefault(t => t.Config.Role == ChannelRole.Primary)
         ?? Tabs.OfType<ChannelTabViewModel>().FirstOrDefault())?.Config;

    private IEnumerable<ChannelConfig> AllChannelConfigs() =>
        Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config);

    /// <summary>Channels that would still transmit ciphertext under ham rules.
    /// Ham mode deliberately doesn't clear anyone's keys, so this is how the
    /// conflict surfaces instead.</summary>
    private IEnumerable<ChannelConfig> EncryptedChannels() =>
        AllChannelConfigs().Where(c => c.EffectiveKey.Length > 0);

    public bool HasLicensedEncryptedChannelWarning => MyIsLicensed && EncryptedChannels().Any();

    public string LicensedEncryptedChannelWarning =>
        HasLicensedEncryptedChannelWarning
            ? $"Transmit is blocked on {string.Join(", ", EncryptedChannels().Select(c => c.Name))} — those channels still have a PSK, and encryption isn't permitted on amateur allocations. Clear the key in each channel's Settings."
            : string.Empty;

    /// <summary>
    /// The channel a frame would go out on, if licensed mode forbids it. The
    /// frame's channel-hash byte identifies the channel; a hash matching none of
    /// ours is foreign traffic, which the LOCAL_ONLY coercion in RelayPolicy has
    /// already excluded from relaying.
    /// </summary>
    private string? LicensedChannelBlocking(byte[] frame)
    {
        if (!MyIsLicensed || frame.Length < MeshHeader.Size) return null;
        byte hash = frame[13];
        foreach (var c in AllChannelConfigs())
            if (c.Hash == hash && c.EffectiveKey.Length > 0) return c.Name;
        return null;
    }

    private bool IsSelf(NodeRecord? node) => node is null || (_rxHost.MyNodeNum != 0 && node.NodeNum == _rxHost.MyNodeNum);

    [RelayCommand]
    private async Task RequestNodeInfo(NodeRecord? node)
    {
        if (node is null || IsSelf(node) || !CanTransmit) return;
        var channel = PrimaryChannel();
        if (channel is null) return;
        var packetId = NextPacketId();
        var frame = MeshEncoder.EncodeNodeInfoRequest(channel, _rxHost.MyNodeNum, node.NodeNum, packetId);
        if (!await TransmitFrameAsync(frame)) { StatusText = "Transmit failed."; return; }
        var name = _rxHost.NodeDisplayName(node.NodeNum);
        _rxHost.AddNote(node.NodeNum, outgoing: true, packetId, "nodeinfo", $"Requested NodeInfo from {name}…");
    }

    [RelayCommand]
    private async Task ExchangeNodeInfo(NodeRecord? node)
    {
        if (node is null || IsSelf(node) || !CanTransmit) return;
        var channel = PrimaryChannel();
        if (channel is null) return;
        var packetId = NextPacketId();
        var frame = MeshEncoder.EncodeNodeInfo(channel, _rxHost.MyNodeNum, packetId,
            _settings.UserLongName, _settings.UserShortName,
            hwModel: (uint)Math.Max(0, HardwareModels.Id(_settings.UserHwModel)), role: RoleEnumValue(_settings.UserRole),
            publicKey: TryParseKeyBase64(_settings.UserPublicKey),
            to: node.NodeNum, wantResponse: true);
        if (!await TransmitFrameAsync(frame)) { StatusText = "Transmit failed."; return; }
        var name = _rxHost.NodeDisplayName(node.NodeNum);
        _rxHost.AddNote(node.NodeNum, outgoing: true, packetId, "nodeinfo", $"Exchanged NodeInfo with {name}…");
    }

    [RelayCommand]
    private async Task RequestLocation(NodeRecord? node)
    {
        if (node is null || IsSelf(node) || !CanTransmit) return;
        var remaining = PositionRequestCooldown - (DateTime.UtcNow - _lastPositionRequestUtc);
        if (remaining > TimeSpan.Zero) { StatusText = $"Position request on cooldown — wait {Math.Ceiling(remaining.TotalSeconds):F0}s."; return; }
        var channel = PrimaryChannel();
        if (channel is null) return;
        var packetId = NextPacketId();
        var frame = MeshEncoder.EncodePositionRequest(channel, _rxHost.MyNodeNum, node.NodeNum, packetId);
        if (!await TransmitFrameAsync(frame)) { StatusText = "Transmit failed."; return; }
        _lastPositionRequestUtc = DateTime.UtcNow;
        var name = _rxHost.NodeDisplayName(node.NodeNum);
        _rxHost.AddNote(node.NodeNum, outgoing: true, packetId, "position", $"Position requested from {name}…");
    }

    [RelayCommand]
    private async Task ExchangeLocation(NodeRecord? node)
    {
        if (node is null || IsSelf(node) || !CanTransmit) return;
        await RequestLocation(node);
        var channel = PrimaryChannel();
        if (channel is null || channel.PositionPrecision == 0) return;
        if (_settings.HomeLatitude is not double lat || _settings.HomeLongitude is not double lon) return;
        try
        {
            var packetId = NextPacketId();
            var frame = MeshEncoder.EncodePosition(channel, _rxHost.MyNodeNum, packetId, lat, lon,
                altitudeM: _settings.HomeAltitude, precisionBits: channel.PositionPrecision, to: node.NodeNum);
            await TransmitFrameAsync(frame);
        }
        catch { /* precision 0 or similar — best-effort */ }
    }

    [RelayCommand]
    private async Task RequestTelemetry(NodeRecord? node)
    {
        if (node is null || IsSelf(node) || !CanTransmit) return;
        var channel = PrimaryChannel();
        if (channel is null) return;
        var packetId = NextPacketId();
        var frame = MeshEncoder.EncodeTelemetryRequest(channel, _rxHost.MyNodeNum, node.NodeNum, packetId);
        if (!await TransmitFrameAsync(frame)) { StatusText = "Transmit failed."; return; }
        var name = _rxHost.NodeDisplayName(node.NodeNum);
        _rxHost.AddNote(node.NodeNum, outgoing: true, packetId, "telemetry", $"Requested telemetry from {name}…");
    }

    [RelayCommand]
    private async Task Traceroute(NodeRecord? node)
    {
        if (node is null || IsSelf(node) || !CanTransmit) return;
        var remaining = TracerouteCooldown - (DateTime.UtcNow - _lastTracerouteUtc);
        if (remaining > TimeSpan.Zero) { StatusText = $"Traceroute on cooldown — wait {Math.Ceiling(remaining.TotalSeconds):F0}s."; return; }
        var primary = PrimaryChannel();
        if (primary is null) return;
        var packetId = NextPacketId();
        var frame = MeshEncoder.EncodeTraceroute(primary, _rxHost.MyNodeNum, node.NodeNum, packetId);
        if (!await TransmitFrameAsync(frame)) { StatusText = "Transmit failed."; return; }
        _lastTracerouteUtc = DateTime.UtcNow;
        _rxHost.RegisterOutgoingTraceroute(packetId, node.NodeNum);
        var name = _rxHost.NodeDisplayName(node.NodeNum);
        StatusText = $"Traceroute requested to {name}";
        _rxHost.AddNote(node.NodeNum, outgoing: true, packetId, "traceroute", $"Traceroute requested to {name}…");
    }

    [RelayCommand]
    private async Task RequestNewKeys(NodeRecord? node)
    {
        if (node is null || IsSelf(node) || !CanTransmit) return;
        _nodeStore.ClearPublicKey(node.NodeNum);
        await ExchangeNodeInfo(node);
    }

    // ----- Quick send (broadcast our own payloads on the primary channel) -----

    [RelayCommand]
    private Task SendSelfNodeInfo() => SendNodeInfoOnChannelAsync(null, null);

    /// <summary>Sends our NodeInfo on a chosen channel, or directed at a peer.
    /// Null channel means the primary — that's the auto-report path.</summary>
    public async Task SendNodeInfoOnChannelAsync(ChannelConfig? channel, uint? to)
    {
        if (!CanTransmit) { StatusText = "Set your node ID and a TX-capable device first."; return; }
        channel ??= PrimaryChannel();
        if (channel is null) return;
        var frame = MeshEncoder.EncodeNodeInfo(channel, _rxHost.MyNodeNum, NextPacketId(),
            MyLongName, MyShortName,
            hwModel: (uint)Math.Max(0, HardwareModels.Id(MyHwModel)), role: RoleEnumValue(MyRole),
            publicKey: TryParseKeyBase64(MyPublicKey),
            to: to ?? 0xFFFFFFFFu,
            hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt,
            isLicensed: MyIsLicensed, isUnmessagable: MyIsUnmessagable);
        StatusText = await TransmitFrameAsync(frame) ? "Sent NodeInfo." : "Transmit failed.";
    }

    [RelayCommand]
    private Task SendSelfPosition() => SendPositionOnChannelAsync(null, null);

    public async Task SendPositionOnChannelAsync(ChannelConfig? channel, uint? to)
    {
        if (!CanTransmit) { StatusText = "Set your node ID and a TX-capable device first."; return; }
        channel ??= PrimaryChannel();
        if (channel is null) return;
        if (channel.PositionPrecision == 0) { StatusText = "Location sharing is disabled on this channel."; return; }
        if (!double.TryParse(HomeLatitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(HomeLongitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            StatusText = "Set your home location in My Node → Configure first.";
            return;
        }
        int? alt = int.TryParse(HomeAltitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : null;
        var frame = MeshEncoder.EncodePosition(channel, _rxHost.MyNodeNum, NextPacketId(), lat, lon,
            altitudeM: alt, precisionBits: channel.PositionPrecision,
            to: to ?? 0xFFFFFFFFu,
            hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
        StatusText = await TransmitFrameAsync(frame) ? "Sent position." : "Transmit failed.";
    }

    [RelayCommand]
    private Task SendSelfDeviceMetrics() => SendDeviceMetricsOnChannelAsync(null, null);

    public async Task SendDeviceMetricsOnChannelAsync(ChannelConfig? channel, uint? to)
    {
        if (!CanTransmit) { StatusText = "Set your node ID and a TX-capable device first."; return; }
        channel ??= PrimaryChannel();
        if (channel is null) return;
        var frame = MeshEncoder.EncodeTelemetryDeviceMetrics(channel, _rxHost.MyNodeNum, NextPacketId(),
            batteryLevel: 101, // 101 = "powered from mains", same sentinel MeshRF.App uses on AC.
            to: to ?? 0xFFFFFFFFu,
            hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
        StatusText = await TransmitFrameAsync(frame) ? "Sent device metrics." : "Transmit failed.";
    }

    [RelayCommand]
    private void ToggleIgnoreNode(NodeRecord? node)
    {
        if (node is null) return;
        _rxHost.SetNodeIgnored(node.NodeNum, !node.Ignored);
    }

    [RelayCommand]
    private void ToggleFavoriteNode(NodeRecord? node)
    {
        if (node is null) return;
        _rxHost.SetNodeFavorite(node.NodeNum, !node.Favorite);
    }

    [RelayCommand]
    private void DeleteNode(NodeRecord? node)
    {
        if (node is null || IsSelf(node)) return;
        _rxHost.ForgetNode(node.NodeNum);
    }

    private static uint RoleEnumValue(string? role) => role switch
    {
        "Client" => 0,
        "ClientMute" => 1,
        "Router" => 2,
        "RouterClient" => 3,
        "Repeater" => 4,
        "Tracker" => 5,
        "Sensor" => 6,
        "TAK" => 7,
        "ClientHidden" => 8,
        "LostAndFound" => 9,
        "TakTracker" => 10,
        "RouterLate" => 11,
        "ClientBase" => 12,
        _ => 0,
    };

    private static byte[] TryParseKeyBase64(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<byte>();
        try { return Convert.FromBase64String(s.Trim()); }
        catch { return Array.Empty<byte>(); }
    }

    // ----- Waypoint context-menu actions -----

    [RelayCommand]
    private async Task ResendWaypoint(WaypointRecord? wp)
    {
        if (wp is null || !CanTransmit) return;
        var channel = _rxHost.FindChannelByName(wp.Channel);
        if (channel is null) { StatusText = "No enabled channel to resend waypoint on."; return; }
        var packetId = NextPacketId();
        var frame = MeshEncoder.EncodeWaypoint(channel, _rxHost.MyNodeNum, packetId, wp.WaypointId,
            wp.Latitude, wp.Longitude, name: wp.Name, description: wp.Description,
            expireEpoch: wp.ExpireEpoch, lockedTo: wp.LockedTo, icon: wp.Icon,
            geofenceRadiusM: wp.GeofenceRadius,
            bboxWest: wp.BboxWest, bboxSouth: wp.BboxSouth, bboxEast: wp.BboxEast, bboxNorth: wp.BboxNorth,
            notifyOnEnter: wp.NotifyOnEnter, notifyOnExit: wp.NotifyOnExit, notifyFavoritesOnly: wp.NotifyFavoritesOnly);
        StatusText = await TransmitFrameAsync(frame)
            ? $"Resent waypoint \"{wp.Name}\""
            : "Transmit failed (device cannot transmit).";
    }

    [RelayCommand]
    private async Task DeleteWaypoint(WaypointRecord? wp)
    {
        if (wp is null) return;
        bool lockedToOther = wp.LockedTo != 0 && wp.LockedTo != _rxHost.MyNodeNum;
        bool expired = wp.ExpireEpoch != 0 && wp.ExpireEpoch != WaypointRecord.NeverExpiresEpoch
                       && wp.ExpireEpoch < DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!lockedToOther && !expired && CanTransmit)
        {
            var channel = _rxHost.FindChannelByName(wp.Channel);
            if (channel is not null)
            {
                try
                {
                    var packetId = NextPacketId();
                    // expireEpoch=1 is the Meshtastic delete convention (no
                    // dedicated delete message type).
                    var frame = MeshEncoder.EncodeWaypoint(channel, _rxHost.MyNodeNum, packetId, wp.WaypointId,
                        wp.Latitude, wp.Longitude, name: wp.Name, description: wp.Description,
                        expireEpoch: 1, lockedTo: wp.LockedTo, icon: wp.Icon);
                    await TransmitFrameAsync(frame);
                }
                catch { /* best-effort delete broadcast */ }
            }
        }
        ForgetWaypointLocal(wp);
    }

    private void ForgetWaypointLocal(WaypointRecord wp)
    {
        _waypointStore.Forget(wp.Id);
        for (int i = 0; i < Waypoints.Count; i++)
        {
            if (Waypoints[i].Id == wp.Id) { Waypoints.RemoveAt(i); break; }
        }
    }

    /// <summary>Applies an edit to an existing waypoint and resends it (same
    /// id, new content) over the mesh, then swaps the local cache entry.</summary>
    public async Task<bool> UpdateWaypointAsync(WaypointRecord original, WaypointEditResult edit)
    {
        if (!CanTransmit) { StatusText = "Set your node ID and a TX-capable device before editing waypoints."; return false; }
        var channel = _rxHost.FindChannelByName(original.Channel);
        if (channel is null) { StatusText = "No enabled channel to send the edit on."; return false; }

        var packetId = NextPacketId();
        var frame = MeshEncoder.EncodeWaypoint(channel, _rxHost.MyNodeNum, packetId, original.WaypointId,
            edit.Latitude, edit.Longitude, name: edit.Name, description: edit.Description,
            expireEpoch: edit.ExpireEpoch, lockedTo: edit.LockedTo, icon: edit.Icon,
            geofenceRadiusM: edit.GeofenceRadius,
            bboxWest: edit.BboxWest, bboxSouth: edit.BboxSouth, bboxEast: edit.BboxEast, bboxNorth: edit.BboxNorth,
            notifyOnEnter: edit.NotifyOnEnter, notifyOnExit: edit.NotifyOnExit, notifyFavoritesOnly: edit.NotifyFavoritesOnly);

        if (!await TransmitFrameAsync(frame)) { StatusText = "Transmit failed (device cannot transmit)."; return false; }

        var updated = new WaypointRecord
        {
            Id = original.Id,
            FromNode = _rxHost.MyNodeNum,
            WaypointId = original.WaypointId,
            PacketId = packetId,
            Channel = original.Channel,
            Name = edit.Name,
            Description = edit.Description,
            Icon = edit.Icon,
            Latitude = edit.Latitude,
            Longitude = edit.Longitude,
            AltitudeM = original.AltitudeM,
            ExpireEpoch = edit.ExpireEpoch,
            LockedTo = edit.LockedTo,
            RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            GeofenceRadius = edit.GeofenceRadius,
            BboxWest = edit.BboxWest,
            BboxSouth = edit.BboxSouth,
            BboxEast = edit.BboxEast,
            BboxNorth = edit.BboxNorth,
            NotifyOnEnter = edit.NotifyOnEnter,
            NotifyOnExit = edit.NotifyOnExit,
            NotifyFavoritesOnly = edit.NotifyFavoritesOnly,
        };
        _waypointStore.Upsert(updated);
        for (int i = 0; i < Waypoints.Count; i++)
        {
            if (Waypoints[i].Id == original.Id) { Waypoints[i] = updated; break; }
        }
        StatusText = $"Updated waypoint \"{edit.Name}\"";
        return true;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        DisposeMyNode();
        DisposeMqtt();
        _ringtone.Dispose();
        _rxRouter.Dispose();
        _rxHost.Dispose();
        _nodeStore.Dispose();
        _messageStore.Dispose();
        _core?.Dispose();
    }
}
