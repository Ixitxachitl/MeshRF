// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshRF;

/// <summary>
/// User-facing app settings persisted as JSON under
/// %APPDATA%\MeshRF\settings.json — the exact same file and schema as
/// MeshRF.App's own <c>AppSettings</c> (that WPF-only class is untouched;
/// this is a separate, parallel copy so the Avalonia app can read/write the
/// same file losslessly without depending on the WPF assembly). Keep the
/// two in sync by hand when either gains a new field — there's no automated
/// check for drift.
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

    /// <summary>Legacy shared RX sample rate used by older builds; kept for migration.</summary>
    public uint RxSampleRateHz { get; set; } = 2_400_000;

    /// <summary>Requested HackRF RX device sample rate in Hz.</summary>
    public uint HackRfRxSampleRateHz { get; set; } = 2_400_000;

    /// <summary>Requested RTL-SDR RX device sample rate in Hz.</summary>
    public uint RtlSdrRxSampleRateHz { get; set; } = 2_400_000;

    /// <summary>Selected TX radio backend. HackRF can transmit; RTL-SDR cannot.</summary>
    public string TxDeviceKind { get; set; } = "HackRf";

    /// <summary>HackRF TX VGA gain in dB (0..47).</summary>
    public byte TxGainDb { get; set; } = 47;

    /// <summary>Enable the HackRF RF amplifier during TX.</summary>
    public bool TxAmpEnable { get; set; } = false;

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
    /// Range 5–480 in the Avalonia app; MeshRF.App still clamps to 5–240 and
    /// rewrites anything above that when it saves.</summary>
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

    /// <summary>Self-reported firmware edition.</summary>
    public string UserFirmwareEdition { get; set; } = "VANILLA";

    /// <summary>Rebroadcast mode for when TX is added (firmware
    /// <c>Config.DeviceConfig.RebroadcastMode</c>).</summary>
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

    public bool AutoReportDeviceMetricsEnabled { get; set; } = false;
    public int AutoReportDeviceMetricsSeconds { get; set; } = 3600;

    public bool AutoReportEnvironmentMetricsEnabled { get; set; } = false;
    public int AutoReportEnvironmentMetricsSeconds { get; set; } = 3600;

    public bool AutoReportNodeStatusEnabled { get; set; } = false;
    public int AutoReportNodeStatusSeconds { get; set; } = 3600;

    public bool AutoReportAirQualityMetricsEnabled { get; set; } = false;
    public int AutoReportAirQualityMetricsSeconds { get; set; } = 3600;

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
    public double? SpectrumTopPaneStar { get; set; }
    public double? SpectrumBottomPaneStar { get; set; }
    public double? LastPacketPanelWidth { get; set; }
    public bool LastPacketExpanded { get; set; } = true;
    public double? MessagesTopPaneStar { get; set; }
    public double? MessagesBottomPaneStar { get; set; }
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

    /// <summary>Show the Snake/Tetris/Breakout/Chirpy Runner high-score
    /// buttons on the primary channel tab. Off by default.</summary>
    public bool ShowGameHighScores { get; set; } = false;

    public int SelectedChannelIndex { get; set; } = -1;
    public int LastSelectedChannelIndex { get; set; } = -1;
    public uint SelectedConversationNode { get; set; }
    public List<uint> OpenConversations { get; set; } = new();
    public List<int> MutedRingtoneChannels { get; set; } = new();
    public string RingtoneMode { get; set; } = "Play once";
    public int RingtoneVolume { get; set; } = 70;
    public string RingtoneRtttl { get; set; } =
        "24:d=32,o=5,b=565:f6,p,f6,4p,p,f6,p,f6,2p,p,b6,p,b6,p,b6,p,b6,p,b,p,b,p,b,p,b,p,b,p,b,p,b,p,b,1p.,2p.,p";

    // -- Map viewport ------------------------------------------------------

    public double? MapCenterLat { get; set; }
    public double? MapCenterLon { get; set; }
    public int MapZoom { get; set; } = 0;
    public bool MapClusterNodes { get; set; } = true;
    public string MapTileTheme { get; set; } = "Auto";

    // -- Node list filters -------------------------------------------------

    public string NodeFilterSearch { get; set; } = string.Empty;
    public string NodeFilterHops { get; set; } = "Any";
    public string NodeFilterKey { get; set; } = "Any";
    public string NodeFilterSigned { get; set; } = "Show all";
    public string NodeFilterLocation { get; set; } = "Any";
    public bool NodeFilterHideInvalidLocations { get; set; } = false;
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
    public string NodeFilterDistanceKm { get; set; } = string.Empty;
    public string NodeFilterMaxAgeMinutes { get; set; } = string.Empty;

    public List<double> WaypointColumnWidths { get; set; } = new();
    public List<double> NodeColumnWidths { get; set; } = new();
    public List<string> NodeColumnDisplayOrder { get; set; } = new();

    public List<PersistedSnakeScore> SnakeHighScores { get; set; } = new();
    public List<PersistedTetrisScore> TetrisHighScores { get; set; } = new();
    public List<PersistedBreakoutScore> BreakoutHighScores { get; set; } = new();
    public List<PersistedChirpyRunnerScore> ChirpyRunnerHighScores { get; set; } = new();

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
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.NormalizeUnitSystem();
            settings.UserPrivateKey = UnprotectSecretText(settings.UserPrivateKeyOnDisk, s_privateKeyEntropy, base64: true);
            settings.MqttPassword = UnprotectSecretText(settings.MqttPasswordOnDisk, s_mqttPasswordEntropy, base64: false);
            foreach (var credential in settings.ScriptCredentials)
                credential.Value = UnprotectSecretText(credential.ValueOnDisk, s_scriptCredentialEntropy, base64: false);
            return settings;
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
            NormalizeUnitSystem();
            UserPrivateKeyOnDisk = ProtectSecretText(UserPrivateKey, s_privateKeyEntropy, base64: true);
            MqttPasswordOnDisk = ProtectSecretText(MqttPassword, s_mqttPasswordEntropy, base64: false);
            foreach (var credential in ScriptCredentials)
                credential.ValueOnDisk = ProtectSecretText(credential.Value, s_scriptCredentialEntropy, base64: false);
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

    // Secrets at rest — private key and MQTT password. Windows uses DPAPI
    // (CurrentUser scope); entropy is a fixed app-specific constant, not a
    // secret — it just scopes the protected blob to this app. Elsewhere,
    // MachineBoundSecret provides AES-GCM under a machine/user-derived key:
    // weaker than DPAPI (see its remarks) but no longer plaintext. Both paths
    // treat unrecognized stored values as legacy plaintext, so existing
    // settings files keep working and encrypt on the next save.
    private static readonly byte[] s_privateKeyEntropy = Encoding.UTF8.GetBytes("MeshRF.UserPrivateKey.v1");
    private static readonly byte[] s_mqttPasswordEntropy = Encoding.UTF8.GetBytes("MeshRF.MqttPassword.v1");
    private static readonly byte[] s_scriptCredentialEntropy = Encoding.UTF8.GetBytes("MeshRF.ScriptCredential.v1");

    private static string SecretKeyDir => Path.GetDirectoryName(SettingsPath)!;

    private static string ProtectSecretText(string plain, byte[] entropy, bool base64)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return Security.MachineBoundSecret.Protect(plain, SecretKeyDir);
        try
        {
            var bytes = base64 ? Convert.FromBase64String(plain) : Encoding.UTF8.GetBytes(plain);
            var protectedBytes = ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return plain;
        }
    }

    private static string UnprotectSecretText(string onDisk, byte[] entropy, bool base64)
    {
        if (string.IsNullOrEmpty(onDisk)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return Security.MachineBoundSecret.Unprotect(onDisk, SecretKeyDir);
        try
        {
            var blob = Convert.FromBase64String(onDisk);
            var bytes = ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser);
            return base64 ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Not a DPAPI blob for this app/user (legacy plaintext, or a
            // different user/machine) — treat as plaintext, same fallback
            // MeshRF.App's copy uses.
            return onDisk;
        }
    }

    private void NormalizeUnitSystem()
    {
        if (string.IsNullOrWhiteSpace(UnitSystem))
            UnitSystem = (UseFahrenheit || UseMiles) ? "Imperial" : "Metric";

        bool imperial = string.Equals(UnitSystem, "Imperial", StringComparison.OrdinalIgnoreCase);
        UseFahrenheit = imperial;
        UseMiles = imperial;
    }
}

/// <summary>One entry in the persisted snake high-score table.</summary>
public sealed class PersistedSnakeScore
{
    public uint NodeNum { get; set; }
    public string ShortName { get; set; } = string.Empty;
    public uint Score { get; set; }
    public uint ScoreId { get; set; }
}

/// <summary>One entry in the persisted Tetris high-score table.</summary>
public sealed class PersistedTetrisScore
{
    public uint NodeNum { get; set; }
    public string ShortName { get; set; } = string.Empty;
    public uint Score { get; set; }
    public uint ScoreId { get; set; }
}

/// <summary>One entry in the persisted Breakout high-score table.</summary>
public sealed class PersistedBreakoutScore
{
    public uint NodeNum { get; set; }
    public string ShortName { get; set; } = string.Empty;
    public uint Score { get; set; }
    public uint ScoreId { get; set; }
}

/// <summary>One entry in the persisted Chirpy Runner high-score table.</summary>
public sealed class PersistedChirpyRunnerScore
{
    public uint NodeNum { get; set; }
    public string ShortName { get; set; } = string.Empty;
    public uint Score { get; set; }
    public uint ScoreId { get; set; }
}
