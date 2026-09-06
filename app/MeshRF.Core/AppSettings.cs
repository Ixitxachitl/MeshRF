// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshRF;

/// <summary>
/// User-facing app settings persisted as JSON under
/// %APPDATA%\MeshRF\settings.json. Lives in Core rather than in the app so the
/// settings schema stays independent of the UI framework.
/// </summary>
public sealed class AppSettings
{
    public string Region { get; set; } = "US";
    public string Preset { get; set; } = "LongFast";
    /// <summary>Frequency slot, 1-based. 0 = Auto: derive it from the region,
    /// preset and primary channel name, as firmware does for channel_num 0.
    /// </summary>
    public int Slot { get; set; } = 0;
    public double CenterFreqMHz { get; set; } = 906.875;

    /// <summary>Spreading factor override (5–12). 0 = derive from preset.</summary>
    public byte OverrideSf { get; set; } = 0;
    /// <summary>Bandwidth override in Hz (e.g. 250000). 0 = derive from preset.</summary>
    public uint OverrideBwHz { get; set; } = 0;
    /// <summary>Coding rate denominator override (5–8 → 4/N). 0 = derive from preset.</summary>
    public byte OverrideCr { get; set; } = 0;

    public byte LnaGainDb { get; set; } = 24;
    public byte VgaGainDb { get; set; } = 20;
    public bool AmpEnable { get; set; } = false;

    /// <summary>Selected RX radio backend: "Auto", "HackRf", "RtlSdr" or "Null".
    /// Matches <see cref="MeshRF.RadioDeviceKind"/>.</summary>
    public string RxDeviceKind { get; set; } = "Null";

    /// <summary>Device-independent RX sample rate, rewritten alongside whichever
    /// per-device rate below is in play. A device still on its 2.4 MHz default
    /// falls back to this, so a rate chosen before that device was ever selected
    /// carries over instead of reverting.</summary>
    public uint RxSampleRateHz { get; set; } = 2_400_000;

    /// <summary>Requested HackRF RX device sample rate in Hz.</summary>
    public uint HackRfRxSampleRateHz { get; set; } = 2_400_000;

    /// <summary>Requested RTL-SDR RX device sample rate in Hz.</summary>
    public uint RtlSdrRxSampleRateHz { get; set; } = 2_400_000;

    // -- Listening on several presets at once ---------------------------------

    /// <summary>Whether the SDR also listens for every other preset whose
    /// default-slot channel fits inside the capture, each a full participant
    /// on its own mesh. Off by default: it widens the capture, costs CPU per
    /// preset, and turns one node into several.</summary>
    public bool MultiPresetEnabled { get; set; }

    /// <summary>Presets the user has unticked, by name. Everything else the
    /// region supports and the capture reaches is listened for.</summary>
    public List<string> MonitorExcludedPresets { get; set; } = new();

    /// <summary>Where the capture is centred relative to the primary, in
    /// kHz. Null lets the plan slide the window to take in the most presets;
    /// a value is clamped so the primary stays inside.</summary>
    public double? MonitorCenterOffsetKHz { get; set; }

    /// <summary>Selected TX radio backend: "HackRf", "Sx1262" or "Null".
    /// RTL-SDR cannot transmit.</summary>
    public string TxDeviceKind { get; set; } = "HackRf";

    /// <summary>HackRF TX VGA gain in dB (0..47).</summary>
    public byte TxGainDb { get; set; } = 47;

    /// <summary>Enable the HackRF RF amplifier during TX.</summary>
    public bool TxAmpEnable { get; set; } = false;

    /// <summary>Which SX126x board is attached: "MeshStick", "MeshToad",
    /// "UConsoleAio", "CustomSpi", or "Unspecified". Matches
    /// <see cref="MeshRF.Sx1262Board"/>. The two USB sticks share USB IDs and
    /// wiring, so between them this selects the power model only — but it
    /// cannot be detected, and the wrong answer misreports radiated power by
    /// ~8 dB, so it defaults to Unspecified and the transmitter stays shut
    /// until the user picks.</summary>
    public string Sx1262Board { get; set; } = "Unspecified";

    /// <summary>Wiring and power model for the "CustomSpi" board — an SX1262
    /// on the host's own SPI bus that MeshRF ships no preset for. Ignored for
    /// every other board. See the HAT pin maps in the README.</summary>
    public CustomSpiBoardSettings CustomSpi { get; set; } = new();

    /// <summary>EEPROM serial of the SX1262 stick to use when several are
    /// attached. Empty takes the first that answers.</summary>
    public string Sx1262Serial { get; set; } = "";

    /// <summary>SX1262 transmit power at the antenna port, in dBm. Clamped by
    /// the native side to the selected board's range (MeshStick -9..22,
    /// MeshToad -1..30). Defaults to the MeshStick maximum, which is also a
    /// safe starting point on a MeshToad.</summary>
    public sbyte Sx1262TxPowerDbm { get; set; } = 22;

    /// <summary>RTL-SDR manual tuner gain in dB (0..49).</summary>
    public byte RtlGainDb { get; set; } = 30;
    /// <summary>RTL-SDR tuner automatic gain control.</summary>
    public bool RtlAgcEnable { get; set; } = false;
    /// <summary>RTL-SDR 5 V bias-T on the antenna port. Off by default.</summary>
    public bool BiasTee { get; set; } = false;
    /// <summary>When true (default), the single-pole IIR DC blocker runs before
    /// the spectrum / modem to suppress the LO leakage spike at the tuned centre
    /// frequency. Disable only for diagnostic / calibration purposes.</summary>
    public bool DcBlockEnable { get; set; } = true;

    /// <summary>Visual color ramp for the waterfall ("Turbo" or "Inferno").</summary>
    public string WaterfallColormap { get; set; } = "Turbo";
    /// <summary>When true, waterfall floor/ceil track recent-frame percentiles.</summary>
    public bool WaterfallAutoLevels { get; set; } = true;
    public double WaterfallFloorDb { get; set; } = -100.0;
    public double WaterfallCeilDb { get; set; } = 0.0;
    /// <summary>Waterfall scroll speed in rows per second (time resolution).
    /// Clamped to 5–480.</summary>
    public double WaterfallRowsPerSecond { get; set; } = 60.0;

    /// <summary>UI theme: "Light", "Dark", or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Display unit system: "Metric" or "Imperial".</summary>
    public string UnitSystem { get; set; } = "Metric";

    /// <summary>When true, display temperatures in Fahrenheit; otherwise Celsius.</summary>
    public bool UseFahrenheit { get; set; } = false;

    /// <summary>When true, display and parse distance filters in miles; otherwise km.</summary>
    public bool UseMiles { get; set; } = false;

    // -- Local node identity (used to recognise / display direct messages) ----

    /// <summary>Our 32-bit node number. 0 = unset (DMs to us can't be matched).</summary>
    public uint UserNodeNum { get; set; }

    public string UserLongName  { get; set; } = string.Empty;
    public string UserShortName { get; set; } = string.Empty;
    public string UserNodeStatus { get; set; } = string.Empty;

    /// <summary>Device role string (Client, Router, etc.). Drives the relay
    /// policy and the broadcast schedules firmware's role defaults dictate.</summary>
    public string UserRole { get; set; } = "Client";

    /// <summary>Firmware <c>User.is_licensed</c>: operate under amateur-radio
    /// rules — plaintext only, no PKC, and no relaying for unlicensed nodes.</summary>
    public bool UserIsLicensed { get; set; }

    /// <summary>Firmware <c>User.is_unmessagable</c>. Advertised to peers only.</summary>
    public bool UserIsUnmessagable { get; set; }

    /// <summary>Hardware model name (e.g. "HELTEC_V3"). Display / future TX use.</summary>
    public string UserHwModel { get; set; } = "UNSET";

    /// <summary>Self-reported firmware version string.</summary>
    public string UserFirmwareVersion { get; set; } = "2.8.0";

    /// <summary>Self-reported firmware edition, named as mesh.proto's
    /// <c>FirmwareEdition</c> spells it.</summary>
    public string UserFirmwareEdition { get; set; } = MeshRF.Mesh.FirmwareEditions.Default;

    /// <summary>Rebroadcast mode the relay applies to received traffic (firmware
    /// <c>Config.DeviceConfig.RebroadcastMode</c>). Enforced by
    /// <c>RelayPolicy.PassesRebroadcastPolicy</c>.</summary>
    public string RebroadcastMode { get; set; } = "ALL";

    /// <summary>Default hop limit for transmitted packets (firmware
    /// <c>Config.LoRaConfig.hop_limit</c>, 1..7). Meshtastic default is 3.</summary>
    public int HopLimit { get; set; } = 3;

    /// <summary>When true, the relay never rebroadcasts MQTT-derived traffic:
    /// packets that arrived via MQTT downlink, and packets from any node marked
    /// heard-via-MQTT. Mirrors firmware <c>Config.LoRaConfig.ignore_mqtt</c>.</summary>
    public bool IgnoreMqtt { get; set; } = false;

    /// <summary>When true, transmitted packets set the <c>Data.bitfield</c>
    /// ok_to_mqtt flag so gateways may uplink them to the public MQTT broker.</summary>
    public bool OkToMqtt { get; set; } = false;

    /// <summary>When true, rebroadcast eligible received packets using the
    /// current role/rebroadcast policy.</summary>
    public bool RoutingRelayEnabled { get; set; } = false;

    // -- MQTT bridge (uplink/downlink gateway) --------------------------------

    /// <summary>Master switch for the MQTT bridge. Off by default.</summary>
    public bool MqttEnabled { get; set; } = false;

    /// <summary>MQTT broker address, optionally "host:port".</summary>
    public string MqttAddress { get; set; } = string.Empty;

    /// <summary>MQTT username.</summary>
    public string MqttUsername { get; set; } = string.Empty;

    /// <summary>MQTT password, decrypted in memory. Routed through
    /// <see cref="MqttPasswordOnDisk"/> so the on-disk copy is DPAPI-protected.</summary>
    [JsonIgnore]
    public string MqttPassword { get; set; } = string.Empty;

    /// <summary>DPAPI-protected on-disk form of <see cref="MqttPassword"/>.</summary>
    [JsonPropertyName("MqttPassword")]
    public string MqttPasswordOnDisk { get; set; } = string.Empty;

    /// <summary>Publish/subscribe the still-channel-PSK-encrypted packet
    /// bytes (true, the firmware default) rather than decrypted contents.</summary>
    public bool MqttEncryptionEnabled { get; set; } = true;

    /// <summary>Also publish a parallel human-readable JSON copy of every
    /// uplinked packet and accept downlink JSON commands.</summary>
    public bool MqttJsonEnabled { get; set; } = false;

    /// <summary>Connect to the broker over TLS. Off by default.</summary>
    public bool MqttTlsEnabled { get; set; } = false;

    /// <summary>MQTT root topic. Empty means the firmware default ("msh").</summary>
    public string MqttRootTopic { get; set; } = string.Empty;

    /// <summary>Periodically publish an unencrypted MapReport to the broker's
    /// map topic. Off by default.</summary>
    public bool MqttMapReportingEnabled { get; set; } = false;

    /// <summary>Seconds between MapReport publishes. Firmware default: 3600.</summary>
    public int MqttMapReportIntervalSeconds { get; set; } = 3600;

    /// <summary>Bits of location precision included in MapReport (12..15).</summary>
    public int MqttMapReportPositionPrecision { get; set; } = 14;

    /// <summary>Automatically transmit NODEINFO_APP at a fixed interval.</summary>
    public bool AutoReportNodeInfoEnabled { get; set; } = false;
    public int AutoReportNodeInfoSeconds { get; set; } = 3600;

    public bool AutoReportPositionEnabled { get; set; } = false;
    public int AutoReportPositionSeconds { get; set; } = 3600;

    // Firmware's position_broadcast_smart_enabled and its two thresholds, with
    // the same defaults (on, 100 m, 5 min): an extra position send when we have
    // actually moved, on top of the interval above rather than instead of it.
    public bool AutoReportPositionSmartEnabled { get; set; } = true;

    /// <summary>Firmware's <c>lora.override_duty_cycle</c>: transmit past the
    /// region's hourly budget. Off by default, as in firmware.</summary>
    public bool OverrideDutyCycle { get; set; }
    public uint AutoReportPositionSmartMinMoveMeters { get; set; } = 100;
    public int AutoReportPositionSmartMinSeconds { get; set; } = 300;

    public bool AutoReportDeviceMetricsEnabled { get; set; } = false;
    public int AutoReportDeviceMetricsSeconds { get; set; } = 3600;

    public bool AutoReportEnvironmentMetricsEnabled { get; set; } = false;
    public int AutoReportEnvironmentMetricsSeconds { get; set; } = 3600;

    public bool AutoReportNodeStatusEnabled { get; set; } = false;
    public int AutoReportNodeStatusSeconds { get; set; } = 3600;

    public bool AutoReportAirQualityMetricsEnabled { get; set; } = false;
    public int AutoReportAirQualityMetricsSeconds { get; set; } = 3600;

    /// <summary>Channel each auto report goes out on, by name. Empty means the
    /// primary, which is where every one of them used to go: a report is a
    /// broadcast to whoever is listening, and which mesh that is differs per
    /// report — telemetry to the neighbours, a status to the club channel.
    /// A name no channel answers to falls back to the primary rather than
    /// going silent.</summary>
    public string AutoReportNodeInfoChannel { get; set; } = string.Empty;
    public string AutoReportPositionChannel { get; set; } = string.Empty;
    public string AutoReportDeviceMetricsChannel { get; set; } = string.Empty;
    public string AutoReportEnvironmentMetricsChannel { get; set; } = string.Empty;
    public string AutoReportAirQualityMetricsChannel { get; set; } = string.Empty;
    public string AutoReportNodeStatusChannel { get; set; } = string.Empty;

    // -- Automation scripts -------------------------------------------------

    /// <summary>Master switch for the script engine. Off by default: turning it
    /// on is a decision to let the app transmit unattended.</summary>
    public bool ScriptsEnabled { get; set; } = false;

    /// <summary>Evaluate scripts and log what they would do, without
    /// transmitting. How a script is developed without keying up.</summary>
    public bool ScriptsDryRun { get; set; } = false;

    /// <summary>
    /// API keys a script's <c>http:</c> action can authenticate with, by name.
    /// </summary>
    /// <remarks>
    /// Here rather than in the script files because scripts are plain text that
    /// gets copied between machines and pasted into chat when asking for help.
    /// Each value is protected at rest the same way the MQTT password is, and
    /// is never exposed to a script as a placeholder.
    /// </remarks>
    public List<Scripting.ScriptCredential> ScriptCredentials { get; set; } = new();

    /// <summary>Base64 X25519 public key for PKI direct messages (TX).</summary>
    public string UserPublicKey  { get; set; } = string.Empty;

    /// <summary>Base64 X25519 private key for PKI direct messages (TX),
    /// decrypted in memory. Routed through <see cref="UserPrivateKeyOnDisk"/>
    /// so the on-disk copy is DPAPI-protected.</summary>
    [JsonIgnore]
    public string UserPrivateKey { get; set; } = string.Empty;

    /// <summary>On-disk form of <see cref="UserPrivateKey"/>. Written
    /// DPAPI-protected; also accepts a legacy plaintext key.</summary>
    [JsonPropertyName("UserPrivateKey")]
    public string UserPrivateKeyOnDisk { get; set; } = string.Empty;

    // -- Home / base-station location (shown on the map) ---------------------

    public string HomeLocationSource { get; set; } = "Manual";
    public double? HomeLatitude  { get; set; }
    public double? HomeLongitude { get; set; }
    public int? HomeAltitude { get; set; }
    public string GpsSerialPort { get; set; } = string.Empty;
    public int GpsBaudRate { get; set; }

    // Smart position: how much of a serial GPS's stream reaches the stored
    // position and the map. On by default — an NMEA receiver reports every
    // second whether or not it has moved.
    public bool GpsSmartPosition { get; set; } = true;
    public uint GpsSmartPositionMinMoveMeters { get; set; } = 10;
    public int GpsSmartPositionMinSeconds { get; set; } = 30;

    // -- Main window geometry / layout -------------------------------------

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public string WindowState { get; set; } = "Normal";
    public double? MainLeftPaneStar { get; set; }
    public double? MainRightPaneStar { get; set; }
    public double? MainTopPaneStar { get; set; }
    public double? MainBottomPaneStar { get; set; }
    public double? MainLeftTopPaneStar { get; set; }
    public double? MainLeftBottomPaneStar { get; set; }
    public double? MainRightTopPaneStar { get; set; }
    public double? MainRightBottomPaneStar { get; set; }
    public double? NodesPaneStar { get; set; }
    public double? WaypointsPaneStar { get; set; }
    public double? SpectrumTopPaneStar { get; set; }
    public double? SpectrumBottomPaneStar { get; set; }
    public double? LastPacketPanelWidth { get; set; }
    public bool LastPacketExpanded { get; set; } = true;
    public double? MessagesTopPaneStar { get; set; }
    public double? MessagesBottomPaneStar { get; set; }

    /// <summary>Which of the main window's six panels are currently in windows
    /// of their own, and where each of those windows sits. Keyed by the panel
    /// name (see MainWindow.Panels.cs); a panel with no entry is docked.
    /// </summary>
    public Dictionary<string, PanelWindowSettings> PanelWindows { get; set; } = new();
    public double? ConversationMessagesPaneStar { get; set; }
    public double? ConversationRightPaneStar { get; set; }
    public double? ConversationTelemetryPaneStar { get; set; }
    public double? ConversationLocationHistoryPaneStar { get; set; }
    public double? LocationHistoryWindowWidth { get; set; }
    public double? LocationHistoryWindowHeight { get; set; }
    public double? LocationHistoryLeftPaneWidth { get; set; }
    public double? TelemetryHistoryWindowWidth { get; set; }
    public double? TelemetryHistoryWindowHeight { get; set; }
    public double? TelemetryHistoryLeftPaneWidth { get; set; }
    public double? TelemetryHistoryTopPaneHeight { get; set; }
    public double? TelemetryHistoryMiddlePaneHeight { get; set; }
    public double? TelemetryHistoryPowerPaneHeight { get; set; }
    public bool IdentityExpanded { get; set; } = false;

    public int SelectedChannelIndex { get; set; } = -1;
    public int LastSelectedChannelIndex { get; set; } = -1;
    public uint SelectedConversationNode { get; set; }
    public List<uint> OpenConversations { get; set; } = new();
    public List<int> MutedRingtoneChannels { get; set; } = new();
    /// <summary>How long the message tone plays. Each tone carries its own,
    /// so a crossing or an alert bell can be silenced without silencing the
    /// messages -- the setting used to be shared by all of them.</summary>
    public string RingtoneMode { get; set; } = "Play once";

    public string GeofenceRingtoneMode { get; set; } = "Play once";

    public string AlertBellRingtoneMode { get; set; } = "Play once";
    public int RingtoneVolume { get; set; } = 70;
    public string RingtoneRtttl { get; set; } =
        "24:d=32,o=5,b=565:f6,p,f6,4p,p,f6,p,f6,2p,p,b6,p,b6,p,b6,p,b6,p,b,p,b,p,b,p,b,p,b,p,b,p,b,p,b,1p.,2p.,p";

    /// <summary>
    /// Tone for a geofence enter/exit crossing, kept separate from the message
    /// ringtone: a crossing is a background event worth a short chime, not the
    /// insistent alert that a message addressed to you deserves. Shares the
    /// ringtone's mode and volume.
    /// </summary>
    public string GeofenceRtttl { get; set; } = "chirp:d=32,o=5,b=160:c,e,g";

    /// <summary>
    /// Tone for a message carrying Meshtastic's alert bell character. Kept
    /// separate because the bell is the sender saying this one matters, which
    /// is worth hearing differently from the message tone every other message
    /// gets.
    /// </summary>
    public string AlertBellRtttl { get; set; } = "bell:d=16,o=7,b=180:c,p,c,4p,c,p,c";

    // -- Map viewport ------------------------------------------------------

    public double? MapCenterLat { get; set; }
    public double? MapCenterLon { get; set; }
    public int MapZoom { get; set; } = 0;
    public bool MapClusterNodes { get; set; } = true;
    public string MapTileTheme { get; set; } = "Auto";

    // -- Link profile ------------------------------------------------------

    // Antenna facts the elevation model cannot know. Heights are above the
    // ground the station stands on, not above sea level: the terrain supplies
    // the rest. Three metres is a mast on a roofline rather than a radio on a
    // desk, which is what most fixed nodes actually are. The gains are a stock
    // quarter-wave whip, net of feedline.
    //
    // The peer values are defaults for whichever node is being profiled, since
    // nothing on the mesh reports how a node is mounted.
    public double LinkProfileMyAntennaM { get; set; } = 3;
    public double LinkProfilePeerAntennaM { get; set; } = 3;
    public double LinkProfileMyGainDbi { get; set; } = 2.15;
    public double LinkProfilePeerGainDbi { get; set; } = 2.15;

    /// <summary>Headroom over the demodulator's floor that still counts as
    /// reach when sweeping coverage. Zero is the cliff edge, where fading alone
    /// drops the link; ten describes somewhere a packet reliably gets through.
    /// It moves the ring materially, so it belongs to the user rather than to
    /// a constant.</summary>
    public double CoverageRequiredMarginDb { get; set; } = 6;

    /// <summary>Whether every directly-heard packet is written to the survey
    /// log along with where this station was standing. Off by default: it is a
    /// deliberate act, and it writes a row per packet.</summary>
    public bool SurveyRecording { get; set; }

    // -- Building loss ------------------------------------------------------

    /// <summary>Whether link predictions charge for the buildings a path
    /// crosses, using footprints fetched from OpenStreetMap. Off by default: it
    /// puts the app on a shared public service, and terrain alone is the
    /// honest answer until someone asks for more.</summary>
    public bool BuildingLossEnabled { get; set; }

    /// <summary>Whether a swept coverage field is shaded, or shown as the
    /// per-bearing wedges alone. On by default: the shading carries the
    /// gradient at the boundary and the islands past an obstruction, which the
    /// wedges cannot.</summary>
    public bool CoverageHeatmap { get; set; } = true;

    /// <summary>Flat charge for each footprint a path enters — the two walls.
    /// The default is MeshLab RF's, from a paired field survey of its own
    /// region rather than a law of nature.</summary>
    public double BuildingLossPerCrossingDb { get; set; } = 10.8;

    /// <summary>What the contents cost, per hundred metres of path inside a
    /// footprint.</summary>
    public double BuildingLossPerHundredMetresDb { get; set; } = 0.3;

    /// <summary>How far the horizon sweep looks. Remembered because it is a
    /// question about a place — the ridge at the end of the street or the hills
    /// behind it — and the answer does not change between sittings.</summary>
    public double HorizonRadiusM { get; set; } = 15_000;

    // -- Path loss calibration ---------------------------------------------

    // A log-distance model fitted to what this station has heard from its
    // direct neighbours, and applied to link predictions as the clutter loss
    // the terrain model does not carry. Null until a calibration has been run
    // and applied; the rest describes how much that fit is worth.
    public double? PathLossExponent { get; set; }
    public double? PathLossOffsetDb { get; set; }
    public double? PathLossRmsDb { get; set; }
    public int PathLossSampleCount { get; set; }
    public DateTime? PathLossFittedUtc { get; set; }

    /// <summary>Whether the exponent was measured or held at free space because
    /// the neighbours could not pin one down. Stored because it decides how far
    /// the model may be carried: one that never measured a falloff knows
    /// nothing about longer ranges, and a prediction that forgets this reads as
    /// authoritative when it is not.</summary>
    public bool PathLossExponentFitted { get; set; }

    /// <summary>The furthest neighbour the fit was measured over. Everything
    /// past it is extrapolation.</summary>
    public double PathLossFurthestSampleM { get; set; }

    /// <summary>What the peers are taken to be transmitting at. Meshtastic
    /// carries no such field, so this is the one number in the calibration that
    /// has to be told rather than measured. 22 dBm is the stock setting for
    /// most hardware.</summary>
    public double PathLossAssumedPeerTxPowerDbm { get; set; } = 22;

    // -- Node list filters -------------------------------------------------

    public string NodeFilterSearch { get; set; } = string.Empty;
    public string NodeFilterHops { get; set; } = "Any";
    /// <summary>"Any", a preset name, or "Custom": which listener's settings a
    /// node was last heard on.</summary>
    public string NodeFilterHeardOn { get; set; } = "Any";
    public string NodeFilterKey { get; set; } = "Any";
    public string NodeFilterSigned { get; set; } = "Show all";
    public string NodeFilterLocation { get; set; } = "Any";
    public string NodeFilterIgnored { get; set; } = "Show all";
    public string NodeFilterFavorite { get; set; } = "Show all";
    public string NodeFilterMqtt { get; set; } = "Any";
    public string NodeFilterTemperature { get; set; } = "Any";
    public string NodeFilterHumidity { get; set; } = "Any";
    public string NodeFilterPressure { get; set; } = "Any";
    public string NodeFilterGasResistance { get; set; } = "Any";
    public string NodeFilterIaq { get; set; } = "Any";
    public string NodeFilterPm10Std { get; set; } = "Any";
    public string NodeFilterPm25Std { get; set; } = "Any";
    public string NodeFilterPm100Std { get; set; } = "Any";
    public string NodeFilterPm10Env { get; set; } = "Any";
    public string NodeFilterPm25Env { get; set; } = "Any";
    public string NodeFilterPm100Env { get; set; } = "Any";
    public string NodeFilterCh1Voltage { get; set; } = "Any";
    public string NodeFilterCh1Current { get; set; } = "Any";
    public string NodeFilterCh2Voltage { get; set; } = "Any";
    public string NodeFilterCh2Current { get; set; } = "Any";
    public string NodeFilterCh3Voltage { get; set; } = "Any";
    public string NodeFilterCh3Current { get; set; } = "Any";
    public string NodeSortMemberPath { get; set; } = string.Empty;
    public bool NodeSortDescending { get; set; } = false;
    public string MapNodeLabelMode { get; set; } = "Node Number";
    public string MapMarkerFilter { get; set; } = "Nodes and waypoints";
    public string NodeFilterDistanceKm { get; set; } = string.Empty;
    public string NodeFilterMaxAgeMinutes { get; set; } = string.Empty;

    public List<double> WaypointColumnWidths { get; set; } = new();
    public List<double> NodeColumnWidths { get; set; } = new();
    public List<string> NodeColumnDisplayOrder { get; set; } = new();

    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
    };

    /// <summary>Redirects the settings file alone, so the persistence can be
    /// exercised against a temp file instead of the real profile. To move the
    /// whole set of stores together, set <see cref="AppData.DirectoryOverride"/>
    /// instead. The app never sets either; left null, everything lands where it
    /// always did.</summary>
    public static string? PathOverride { get; set; }

    public static string SettingsPath
    {
        get
        {
            if (PathOverride is not { Length: > 0 } custom) return AppData.PathFor("settings.json");

            Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
            return custom;
        }
    }

    /// <summary>Path of the copy left behind by the last successful write of
    /// <paramref name="path"/>. It is a whole file by construction, so it is
    /// what a settings.json that will not parse gets recovered from.</summary>
    private static string BackupPathFor(string path) => path + ".bak";

    /// <summary>Set when <see cref="Load"/> could not read the settings file,
    /// so the app can say so rather than silently coming up on defaults.</summary>
    public static string? LastLoadWarning { get; private set; }

    public static AppSettings Load()
    {
        var path = SettingsPath;
        if (TryLoad(path, out var settings))
        {
            LastLoadWarning = null;
            return settings;
        }

        // Unreadable settings.json. The backup is the copy that was whole when
        // this file replaced it, so falling back to it costs at most the newest
        // change — against coming up on defaults, which loses the window
        // layout, the radio setup and every stored secret at once.
        if (TryLoad(BackupPathFor(path), out var recovered))
        {
            LastLoadWarning = "settings.json could not be read; recovered the previous copy.";
            try { File.Copy(BackupPathFor(path), path, overwrite: true); } catch { /* recovery is best-effort */ }
            return recovered;
        }

        // No file at all is a first run, not a fault.
        LastLoadWarning = File.Exists(path)
            ? "settings.json could not be read and no usable backup was found; starting from defaults."
            : null;
        return new AppSettings();
    }

    private static bool TryLoad(string path, out AppSettings settings)
    {
        settings = new AppSettings();
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            if (JsonSerializer.Deserialize<AppSettings>(json) is not { } loaded) return false;
            loaded.NormalizeUnitSystem();
            loaded.UserPrivateKey = UnprotectSecretText(loaded.UserPrivateKeyOnDisk, s_privateKeyEntropy, base64: true);
            loaded.MqttPassword = UnprotectSecretText(loaded.MqttPasswordOnDisk, s_mqttPasswordEntropy, base64: false);
            foreach (var credential in loaded.ScriptCredentials)
            {
                credential.Value = UnprotectSecretText(credential.ValueOnDisk, s_scriptCredentialEntropy, base64: false);
                credential.Value2 = UnprotectSecretText(credential.Value2OnDisk, s_scriptCredentialEntropy, base64: false);
            }
            settings = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save()
    {
        try
        {
            NormalizeUnitSystem();
            UserPrivateKeyOnDisk = ProtectSecretText(UserPrivateKey, s_privateKeyEntropy, base64: true);
            MqttPasswordOnDisk = ProtectSecretText(MqttPassword, s_mqttPasswordEntropy, base64: false);
            foreach (var credential in ScriptCredentials)
            {
                credential.ValueOnDisk = ProtectSecretText(credential.Value, s_scriptCredentialEntropy, base64: false);
                credential.Value2OnDisk = ProtectSecretText(credential.Value2, s_scriptCredentialEntropy, base64: false);
            }
            QueueWrite(JsonSerializer.Serialize(this, s_opts));
        }
        catch
        {
            // Persistence failures are non-fatal.
        }
    }

    // -- Writing ------------------------------------------------------------
    //
    // One writer at a time, and every write lands whole. Saves come from all
    // over the app and off several threads, and each one carries the entire
    // object: overlapping writes fight over the same handle, and a write
    // interrupted part-way leaves a settings.json that parses as nothing at all.

    private static readonly object s_writeGate = new();
    private static (string Path, string Json)? s_pending;
    private static bool s_writing;
    private static Task s_writer = Task.CompletedTask;

    /// <summary>Hands the serialized settings to the background writer. A save
    /// queued while another is in flight replaces any save still waiting: they
    /// each carry the whole object, so only the newest is worth writing. The
    /// destination is resolved here rather than in the writer, so a save always
    /// lands where the file was when it was asked for.</summary>
    private static void QueueWrite(string json)
    {
        lock (s_writeGate)
        {
            s_pending = (SettingsPath, json);
            if (s_writing) return;
            s_writing = true;
            s_writer = Task.Run(DrainWrites);
        }
    }

    private static void DrainWrites()
    {
        while (true)
        {
            (string Path, string Json) write;
            lock (s_writeGate)
            {
                if (s_pending is not { } queued) { s_writing = false; return; }
                write = queued;
                s_pending = null;
            }
            try { WriteAtomic(write.Path, write.Json); } catch { /* persistence failures are non-fatal */ }
        }
    }

    /// <summary>Blocks until queued saves have reached the disk. Called on the
    /// way out, so the last change of a session is not still sitting in memory
    /// when the process ends.</summary>
    public static void FlushPendingWrites(TimeSpan timeout)
    {
        Task writer;
        lock (s_writeGate) writer = s_writer;
        try { writer.Wait(timeout); } catch { /* nothing useful to do if it will not finish */ }
    }

    /// <summary>Writes the whole file somewhere else, forces it down to the
    /// disk, and only then swaps it into place, keeping what it replaced as the
    /// backup. A crash can cost the newest save; it cannot leave a half-written
    /// settings.json behind.</summary>
    private static void WriteAtomic(string path, string json)
    {
        string tmp = path + ".tmp";

        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }

        try
        {
            File.Replace(tmp, path, BackupPathFor(path), ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // Replace wants both files on one volume and a filesystem that
            // supports it. Where that does not hold, keep the backup by hand and
            // take the ordinary overwrite.
            try { File.Copy(path, BackupPathFor(path), overwrite: true); } catch { /* best-effort */ }
            File.Move(tmp, path, overwrite: true);
        }
    }

    // Secrets at rest. The mechanism and its caveats live in
    // Security.SecretProtection, which the channel store shares; the entropy
    // values below are not secrets, they just scope each protected blob to one
    // kind of secret so a stored MQTT password cannot be handed to the
    // private-key parser.
    private static readonly byte[] s_privateKeyEntropy = Encoding.UTF8.GetBytes("MeshRF.UserPrivateKey.v1");
    private static readonly byte[] s_mqttPasswordEntropy = Encoding.UTF8.GetBytes("MeshRF.MqttPassword.v1");
    private static readonly byte[] s_scriptCredentialEntropy = Encoding.UTF8.GetBytes("MeshRF.ScriptCredential.v1");

    private static string SecretKeyDir => Path.GetDirectoryName(SettingsPath)!;

    private static string ProtectSecretText(string plain, byte[] entropy, bool base64) =>
        Security.SecretProtection.ProtectText(plain, entropy, SecretKeyDir, base64);

    private static string UnprotectSecretText(string onDisk, byte[] entropy, bool base64) =>
        Security.SecretProtection.UnprotectText(onDisk, entropy, SecretKeyDir, base64);

    private void NormalizeUnitSystem()
    {
        if (string.IsNullOrWhiteSpace(UnitSystem))
            UnitSystem = (UseFahrenheit || UseMiles) ? "Imperial" : "Metric";

        bool imperial = string.Equals(UnitSystem, "Imperial", StringComparison.OrdinalIgnoreCase);
        UseFahrenheit = imperial;
        UseMiles = imperial;
    }
}

/// <summary>
/// An SX1262 on the host's own SPI bus, described by the operator. Both halves
/// have to be stated: nothing on an SPI bus announces which GPIO lines a board
/// used, and nothing reports whether a power amplifier sits after the chip.
///
/// The power fields are in dBm at the antenna port. <see cref="PaGainDb"/> is
/// the difference between that and what gets programmed into the chip, so a
/// bare SX1262 leaves it at 0 and a board with an E22-style front end sets it
/// to that module's gain. Leaving it at 0 on a board that has a PA is the one
/// mistake here that under-reports — the UI would show 22 dBm while the
/// antenna saw 30 — which is why MeshRF ships no guesses for these boards.
/// </summary>
/// <summary>
/// One popped-out panel: whether it is out, and the geometry of the window it
/// is out in. Geometry is kept even while the panel is docked, so popping it
/// out again puts the window back where it was last left.
/// </summary>
public sealed class PanelWindowSettings
{
    public bool PoppedOut { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string WindowState { get; set; } = "Normal";
}

public sealed class CustomSpiBoardSettings
{
    /// <summary>SPI device node under /dev, e.g. "spidev0.0".</summary>
    public string SpiDev { get; set; } = "spidev0.0";
    /// <summary>GPIO character device under /dev, e.g. "gpiochip0".</summary>
    public string GpioChip { get; set; } = "gpiochip0";
    /// <summary>SPI clock in Hz. The SX126x tolerates 16 MHz; meshtasticd
    /// uses 2 MHz and so do we.</summary>
    public int SpeedHz { get; set; } = 2_000_000;

    /// <summary>Chip-select GPIO line, or -1 to let the SPI controller drive
    /// its own — which is the usual wiring, with the radio on CE0.</summary>
    public int Cs { get; set; } = -1;
    /// <summary>BUSY line. Required.</summary>
    public int Busy { get; set; } = -1;
    /// <summary>NRST line. Required.</summary>
    public int Reset { get; set; } = -1;
    /// <summary>DIO1 / IRQ line. Required.</summary>
    public int Dio1 { get; set; } = -1;
    /// <summary>RXEN line, or -1 on a board whose DIO2 runs the RF switch.</summary>
    public int RxEn { get; set; } = -1;

    /// <summary>DIO2 drives the RF switch directly. True on most modules.</summary>
    public bool Dio2AsRfSwitch { get; set; } = true;
    /// <summary>DIO3 supplies the TCXO. True on any module with one.</summary>
    public bool Dio3Tcxo { get; set; } = true;
    /// <summary>SetDIO3AsTCXOCtrl voltage code; 0x02 is 1.8 V.</summary>
    public byte TcxoVoltage { get; set; } = 0x02;

    /// <summary>Ceiling programmed into SetTxParams, in dBm.</summary>
    public sbyte MaxChipDbm { get; set; } = 22;
    /// <summary>Antenna-port power minus chip power. 0 on a bare SX1262.</summary>
    public sbyte PaGainDb { get; set; } = 0;
    /// <summary>Lowest selectable antenna-port power, in dBm.</summary>
    public sbyte MinOutDbm { get; set; } = -9;
    /// <summary>Highest selectable antenna-port power, in dBm.</summary>
    public sbyte MaxOutDbm { get; set; } = 22;
}
