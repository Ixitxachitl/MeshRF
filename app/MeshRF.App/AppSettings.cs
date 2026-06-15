// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MeshRF.App;

/// <summary>
/// User-facing app settings persisted as JSON under
/// %APPDATA%\MeshRF\settings.json.
/// </summary>
public sealed class AppSettings
{
    public string Region { get; set; } = "US";
    public string Preset { get; set; } = "LongFast";
    public int Slot { get; set; } = 20;
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

    /// <summary>Legacy selected radio backend, now treated as RX on migration.</summary>
    public string DeviceKind { get; set; } = "Null";

    /// <summary>Selected RX radio backend: "Auto", "HackRf", "RtlSdr" or "Null".
    /// Matches <see cref="MeshRF.RadioDeviceKind"/>.</summary>
    public string RxDeviceKind { get; set; } = "Null";

    /// <summary>Selected TX radio backend. HackRF can transmit; RTL-SDR cannot.</summary>
    public string TxDeviceKind { get; set; } = "HackRf";

    /// <summary>Auto-Gain-Control: when on, the app pushes LNA/VGA to keep
    /// the peak power around <see cref="AgcTargetDbfs"/>.</summary>
    public bool AgcEnable { get; set; } = false;
    public double AgcTargetDbfs { get; set; } = -15.0;

    /// <summary>RTL-SDR manual tuner gain in dB (0..49).</summary>
    public byte RtlGainDb { get; set; } = 30;
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

    /// <summary>UI theme: "Light", "Dark", or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>When true, display temperatures in Fahrenheit; otherwise Celsius.</summary>
    public bool UseFahrenheit { get; set; } = false;

    /// <summary>When true, display and parse distance filters in miles; otherwise km.</summary>
    public bool UseMiles { get; set; } = false;

    // -- Local node identity (used to recognise / display direct messages) ----

    /// <summary>Our 32-bit node number. 0 = unset (DMs to us can't be matched).</summary>
    public uint UserNodeNum { get; set; }

    public string UserLongName  { get; set; } = string.Empty;
    public string UserShortName { get; set; } = string.Empty;

    /// <summary>Device role string (Client, Router, etc.) — display only.</summary>
    public string UserRole { get; set; } = "Client";

    /// <summary>Hardware model name (e.g. "HELTEC_V3"). Display / future TX use.</summary>
    public string UserHwModel { get; set; } = "UNSET";

    /// <summary>Rebroadcast mode for when TX is added (firmware
    /// <c>Config.DeviceConfig.RebroadcastMode</c>).</summary>
    public string RebroadcastMode { get; set; } = "ALL";

    /// <summary>Default hop limit for transmitted packets (firmware
    /// <c>Config.LoRaConfig.hop_limit</c>, 1..7). Meshtastic default is 3.</summary>
    public int HopLimit { get; set; } = 3;

    /// <summary>When true, transmitted packets set the <c>Data.bitfield</c>
    /// ok_to_mqtt flag so gateways may uplink them to the public MQTT broker
    /// (firmware <c>Config.LoRaConfig.config_ok_to_mqtt</c>). Off by default.</summary>
    public bool OkToMqtt { get; set; } = false;

    /// <summary>Automatically transmit NODEINFO_APP at a fixed interval.</summary>
    public bool AutoReportNodeInfoEnabled { get; set; } = false;

    /// <summary>Interval in seconds for automatic NODEINFO_APP transmission.</summary>
    public int AutoReportNodeInfoSeconds { get; set; } = 300;

    /// <summary>Automatically transmit POSITION_APP at a fixed interval.</summary>
    public bool AutoReportPositionEnabled { get; set; } = false;

    /// <summary>Interval in seconds for automatic POSITION_APP transmission.</summary>
    public int AutoReportPositionSeconds { get; set; } = 300;

    /// <summary>Automatically transmit TELEMETRY device metrics at a fixed interval.</summary>
    public bool AutoReportDeviceMetricsEnabled { get; set; } = false;

    /// <summary>Interval in seconds for automatic TELEMETRY device metrics transmission.</summary>
    public int AutoReportDeviceMetricsSeconds { get; set; } = 300;

    /// <summary>Base64 X25519 public key for PKI direct messages (TX).</summary>
    public string UserPublicKey  { get; set; } = string.Empty;

    /// <summary>Base64 X25519 private key for PKI direct messages (TX).</summary>
    public string UserPrivateKey { get; set; } = string.Empty;

    // -- Home / base-station location (shown on the map) ---------------------

    /// <summary>Selected home location source: "Manual" or "UsbSerial".</summary>
    public string HomeLocationSource { get; set; } = "Manual";

    /// <summary>Manual home latitude in degrees, null if unset.</summary>
    public double? HomeLatitude  { get; set; }

    /// <summary>Manual home longitude in degrees, null if unset.</summary>
    public double? HomeLongitude { get; set; }

    /// <summary>Manual home altitude in metres above sea level, null if unset.</summary>
    public int? HomeAltitude { get; set; }

    /// <summary>Optional USB GPS COM port override (blank = auto-detect).</summary>
    public string GpsSerialPort { get; set; } = string.Empty;

    /// <summary>Optional USB GPS baud override (0 = auto-detect).</summary>
    public int GpsBaudRate { get; set; }

    // -- Main window geometry / layout -------------------------------------

    /// <summary>Left edge of the main window in device-independent pixels.</summary>
    public double? WindowLeft { get; set; }

    /// <summary>Top edge of the main window in device-independent pixels.</summary>
    public double? WindowTop { get; set; }

    /// <summary>Width of the main window in device-independent pixels.</summary>
    public double? WindowWidth { get; set; }

    /// <summary>Height of the main window in device-independent pixels.</summary>
    public double? WindowHeight { get; set; }

    /// <summary>Window state at shutdown: Normal, Maximized, or Minimized.</summary>
    public string WindowState { get; set; } = "Normal";

    /// <summary>Main 2x2 shell left-pane star width.</summary>
    public double? MainLeftPaneStar { get; set; }

    /// <summary>Main 2x2 shell right-pane star width.</summary>
    public double? MainRightPaneStar { get; set; }

    /// <summary>Main 2x2 shell top-pane star height.</summary>
    public double? MainTopPaneStar { get; set; }

    /// <summary>Main 2x2 shell bottom-pane star height.</summary>
    public double? MainBottomPaneStar { get; set; }

    /// <summary>Left pane top star height (map).</summary>
    public double? MainLeftTopPaneStar { get; set; }

    /// <summary>Left pane bottom star height (identity + nodes).</summary>
    public double? MainLeftBottomPaneStar { get; set; }

    /// <summary>Right pane top star height (spectrum/waterfall).</summary>
    public double? MainRightTopPaneStar { get; set; }

    /// <summary>Right pane bottom star height (channels/messages/log).</summary>
    public double? MainRightBottomPaneStar { get; set; }

    /// <summary>Spectrum panel top star height.</summary>
    public double? SpectrumTopPaneStar { get; set; }

    /// <summary>Waterfall panel bottom star height.</summary>
    public double? SpectrumBottomPaneStar { get; set; }

    /// <summary>Width of the frozen last-packet popup in device-independent pixels.</summary>
    public double? LastPacketPanelWidth { get; set; }

    /// <summary>Messages panel star height above the log.</summary>
    public double? MessagesTopPaneStar { get; set; }

    /// <summary>Log panel star height below the tabs/messages.</summary>
    public double? MessagesBottomPaneStar { get; set; }

    /// <summary>Conversation tab messages pane star width.</summary>
    public double? ConversationMessagesPaneStar { get; set; }

    /// <summary>Conversation tab right panel (telemetry/history) star width.</summary>
    public double? ConversationRightPaneStar { get; set; }

    /// <summary>Conversation tab telemetry pane star height.</summary>
    public double? ConversationTelemetryPaneStar { get; set; }

    /// <summary>Conversation tab location-history pane star height.</summary>
    public double? ConversationLocationHistoryPaneStar { get; set; }

    /// <summary>Whether the identity expander is open.</summary>
    public bool IdentityExpanded { get; set; } = false;

    /// <summary>Persisted selected channel index; -1 when a DM tab was active.</summary>
    public int SelectedChannelIndex { get; set; } = -1;

    /// <summary>Persisted selected DM conversation node number; 0 when not applicable.</summary>
    public uint SelectedConversationNode { get; set; }

    /// <summary>Node numbers of the direct-message conversation tabs the user
    /// had open, so only those are reopened on the next launch (rather than
    /// every node we happen to have chat history with).</summary>
    public List<uint> OpenConversations { get; set; } = new();

    /// <summary>Channel indexes whose incoming text messages should not play the RTTTL ringtone.</summary>
    public List<int> MutedRingtoneChannels { get; set; } = new();

    /// <summary>Incoming-message ringtone duration: "Off", "Play once",
    /// "5 seconds", "10 seconds" or "30 seconds".</summary>
    public string RingtoneMode { get; set; } = "Play once";

    /// <summary>Ringtone volume, 0..100.</summary>
    public int RingtoneVolume { get; set; } = 70;

    /// <summary>RTTTL ringtone string; defaults to the stock Meshtastic tune.</summary>
    public string RingtoneRtttl { get; set; } =
        "24:d=32,o=5,b=565:f6,p,f6,4p,p,f6,p,f6,2p,p,b6,p,b6,p,b6,p,b6,p,b,p,b,p,b,p,b,p,b,p,b,p,b,p,b,1p.,2p.,p";

    // -- Map viewport ------------------------------------------------------

    /// <summary>Map center latitude when the app was last closed.</summary>
    public double? MapCenterLat { get; set; }

    /// <summary>Map center longitude when the app was last closed.</summary>
    public double? MapCenterLon { get; set; }

    /// <summary>Map zoom level (2–19) when the app was last closed. 0 = unset.</summary>
    public int MapZoom { get; set; } = 0;

    /// <summary>Whether map markers are clustered (true) or always expanded (false).</summary>
    public bool MapClusterNodes { get; set; } = true;

    // -- Node list filters -------------------------------------------------

    /// <summary>Persisted node text search filter.</summary>
    public string NodeFilterSearch { get; set; } = string.Empty;

    /// <summary>Persisted node hops filter (e.g. "Any", "Direct").</summary>
    public string NodeFilterHops { get; set; } = "Any";

    /// <summary>Persisted node key filter.</summary>
    public string NodeFilterKey { get; set; } = "Any";

    /// <summary>Persisted node location filter.</summary>
    public string NodeFilterLocation { get; set; } = "Any";

    /// <summary>Persisted node ignored filter.</summary>
    public string NodeFilterIgnored { get; set; } = "Show all";

    /// <summary>Persisted node temperature filter.</summary>
    public string NodeFilterTemperature { get; set; } = "Any";

    /// <summary>Persisted node humidity filter.</summary>
    public string NodeFilterHumidity { get; set; } = "Any";

    /// <summary>Persisted node pressure filter.</summary>
    public string NodeFilterPressure { get; set; } = "Any";

    /// <summary>Persisted map node label mode.</summary>
    public string MapNodeLabelMode { get; set; } = "Short name";

    /// <summary>Persisted max-distance filter in km (empty = off).</summary>
    public string NodeFilterDistanceKm { get; set; } = string.Empty;

    /// <summary>Persisted max-age filter in minutes (empty = off).</summary>
    public string NodeFilterMaxAgeMinutes { get; set; } = string.Empty;

    /// <summary>Persisted display widths (in DIP) for the Waypoints grid columns.</summary>
    public List<double> WaypointColumnWidths { get; set; } = new();

    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
    };

    public static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MeshRF");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable — fall back to defaults rather than crashing.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            // Serialize on the caller's thread (fast, <1 ms), then write the
            // file on a thread-pool thread so the UI never blocks on disk I/O.
            // Each call captures its own snapshot so concurrent calls are safe.
            var json = JsonSerializer.Serialize(this, s_opts);
            _ = Task.Run(() =>
            {
                try { File.WriteAllText(SettingsPath, json); }
                catch { }
            });
        }
        catch
        {
            // Persistence failures are non-fatal.
        }
    }
}
