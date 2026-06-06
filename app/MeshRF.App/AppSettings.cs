// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;

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

    public byte LnaGainDb { get; set; } = 24;
    public byte VgaGainDb { get; set; } = 20;
    public bool AmpEnable { get; set; } = false;

    /// <summary>Selected radio backend: "Auto", "HackRf", "RtlSdr" or "Null".
    /// Matches <see cref="MeshRF.RadioDeviceKind"/>.</summary>
    public string DeviceKind { get; set; } = "Auto";

    /// <summary>Auto-Gain-Control: when on, the app pushes LNA/VGA to keep
    /// the peak power around <see cref="AgcTargetDbfs"/>.</summary>
    public bool AgcEnable { get; set; } = false;
    public double AgcTargetDbfs { get; set; } = -15.0;

    /// <summary>RTL-SDR manual tuner gain in dB (0..49).</summary>
    public byte RtlGainDb { get; set; } = 30;
    /// <summary>RTL-SDR 5 V bias-T on the antenna port. Off by default.</summary>
    public bool BiasTee { get; set; } = false;

    /// <summary>Visual color ramp for the waterfall ("Turbo" or "Inferno").</summary>
    public string WaterfallColormap { get; set; } = "Turbo";
    /// <summary>When true, waterfall floor/ceil track recent-frame percentiles.</summary>
    public bool WaterfallAutoLevels { get; set; } = true;
    public double WaterfallFloorDb { get; set; } = -100.0;
    public double WaterfallCeilDb { get; set; } = 0.0;

    /// <summary>UI theme: "Light", "Dark", or "System".</summary>
    public string Theme { get; set; } = "System";

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

    /// <summary>Base64 X25519 public key for PKI direct messages (TX).</summary>
    public string UserPublicKey  { get; set; } = string.Empty;

    /// <summary>Base64 X25519 private key for PKI direct messages (TX).</summary>
    public string UserPrivateKey { get; set; } = string.Empty;

    // -- Home / base-station location (shown on the map) ---------------------

    /// <summary>Home latitude in degrees, null if unset.</summary>
    public double? HomeLatitude  { get; set; }

    /// <summary>Home longitude in degrees, null if unset.</summary>
    public double? HomeLongitude { get; set; }

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
            var json = JsonSerializer.Serialize(this, s_opts);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Persistence failures are non-fatal.
        }
    }
}
