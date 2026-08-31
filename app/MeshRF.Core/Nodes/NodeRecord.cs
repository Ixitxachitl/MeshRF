// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;

namespace MeshRF.Nodes;

/// <summary>
/// Mirrors the fields Meshtastic firmware tracks per node in its
/// <c>NodeInfo</c> protobuf. We persist the union so the UI / future MQTT
/// bridge can use it directly.
/// </summary>
/// <remarks>
/// Implements <see cref="INotifyPropertyChanged"/> coarsely: callers that
/// mutate a record bound to the UI use <see cref="UpdateFrom"/> (or
/// <see cref="NotifyChanged"/>) to raise a single all-properties change
/// notification. This lets list rows update in place instead of being
/// replaced, which avoids DataGrid row-container churn (visible flicker).
/// </remarks>
public sealed class NodeRecord : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private static readonly PropertyChangedEventArgs AllPropertiesChanged = new(string.Empty);

    /// <summary>Raises a single "every property changed" notification so all
    /// bindings on this record re-read their values.</summary>
    public void NotifyChanged() => PropertyChanged?.Invoke(this, AllPropertiesChanged);

    /// <summary>Copies every persisted field from <paramref name="source"/>
    /// into this record and raises one all-properties change notification.
    /// Used to refresh a UI-bound row without replacing the instance.</summary>
    public void UpdateFrom(NodeRecord source)
    {
        NodeNum         = source.NodeNum;
        UserId          = source.UserId;
        LongName        = source.LongName;
        ShortName       = source.ShortName;
        HwModel         = source.HwModel;
        MacAddress      = source.MacAddress;
        Role            = source.Role;
        LastHeardEpoch  = source.LastHeardEpoch;
        SeenViaMqtt     = source.SeenViaMqtt;
        SnrDb           = source.SnrDb;
        RssiDbm         = source.RssiDbm;
        HopsAway        = source.HopsAway;
        Latitude        = source.Latitude;
        Longitude       = source.Longitude;
        AltitudeM       = source.AltitudeM;
        BatteryPct      = source.BatteryPct;
        VoltageV        = source.VoltageV;
        ChannelUtilPct  = source.ChannelUtilPct;
        AirUtilTxPct    = source.AirUtilTxPct;
        UptimeSeconds   = source.UptimeSeconds;
        TemperatureC          = source.TemperatureC;
        RelativeHumidityPct   = source.RelativeHumidityPct;
        BarometricPressureHpa = source.BarometricPressureHpa;
        GasResistanceMohm     = source.GasResistanceMohm;
        Iaq                   = source.Iaq;
        Pm10Standard          = source.Pm10Standard;
        Pm25Standard          = source.Pm25Standard;
        Pm100Standard         = source.Pm100Standard;
        Pm10Environmental     = source.Pm10Environmental;
        Pm25Environmental     = source.Pm25Environmental;
        Pm100Environmental    = source.Pm100Environmental;
        Ch1VoltageV     = source.Ch1VoltageV;
        Ch1CurrentMa    = source.Ch1CurrentMa;
        Ch2VoltageV     = source.Ch2VoltageV;
        Ch2CurrentMa    = source.Ch2CurrentMa;
        Ch3VoltageV     = source.Ch3VoltageV;
        Ch3CurrentMa    = source.Ch3CurrentMa;
        NodeStatus      = source.NodeStatus;
        PublicKey       = source.PublicKey;
        KeyMismatch     = source.KeyMismatch;
        IsUnmessagable  = source.IsUnmessagable;
        IsLicensed      = source.IsLicensed;
        HasXeddsaSigned = source.HasXeddsaSigned;
        MuteRtttl       = source.MuteRtttl;
        Ignored         = source.Ignored;
        Favorite        = source.Favorite;
        NotifyChanged();
    }
    /// <summary>32-bit Meshtastic node number (e.g. 0xAABBCCDD).</summary>
    public uint NodeNum { get; set; }

    /// <summary>"!aabbccdd" canonical user id.</summary>
    public string UserId { get; set; } = string.Empty;

    public string LongName  { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string HwModel   { get; set; } = string.Empty;
    public string Role      { get; set; } = string.Empty;

    /// <summary>MAC the peer advertised in NodeInfo (field 4), as
    /// <c>aa:bb:cc:dd:ee:ff</c>; empty when it has never sent a real one.
    /// The field is deprecated, and firmware zero-fills it for any node whose
    /// record came back from flash, so most peers never populate it.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Convenience flag for UI visibility bindings.</summary>
    public bool HasMacAddress => !string.IsNullOrEmpty(MacAddress);

    /// <summary>Unix epoch seconds, 0 if never heard.</summary>
    public long LastHeardEpoch { get; set; }

    /// <summary>Transport of the most recent sighting: true when it arrived
    /// <c>via_mqtt</c>. Mirrors firmware's per-node <c>VIA_MQTT</c> bitfield,
    /// which <c>NodeDB::updateFrom</c> overwrites from every packet — so a node
    /// first heard over MQTT reverts to local once we hear it over the air.
    /// Null on an upsert that carries no packet (identity/position-only writes)
    /// and leaves the stored value alone.</summary>
    public bool? SeenViaMqtt { get; set; }

    /// <summary>Convenience flag for UI visibility bindings.</summary>
    public bool IsSeenViaMqtt => SeenViaMqtt == true;

    public float? SnrDb       { get; set; }
    public float? RssiDbm     { get; set; }
    public byte?  HopsAway    { get; set; }
    public double? Latitude   { get; set; }
    public double? Longitude  { get; set; }
    public int?    AltitudeM  { get; set; }
    public byte?   BatteryPct { get; set; }
    public float?  VoltageV   { get; set; }
    public float?  ChannelUtilPct { get; set; }
    public float?  AirUtilTxPct   { get; set; }
    public uint?   UptimeSeconds  { get; set; }

    // Environment metrics (from TELEMETRY_APP EnvironmentMetrics).
    public float?  TemperatureC          { get; set; }
    public float?  RelativeHumidityPct   { get; set; }
    public float?  BarometricPressureHpa { get; set; }
    public float?  GasResistanceMohm     { get; set; }
    public int?    Iaq                   { get; set; }

    // Air quality metrics (from TELEMETRY_APP AirQualityMetrics).
    // pm10_standard = PM1.0 µg/m³, pm25_standard = PM2.5 µg/m³, pm100_standard = PM10 µg/m³
    public uint? Pm10Standard      { get; set; }
    public uint? Pm25Standard      { get; set; }
    public uint? Pm100Standard     { get; set; }
    public uint? Pm10Environmental  { get; set; }
    public uint? Pm25Environmental  { get; set; }
    public uint? Pm100Environmental { get; set; }

    // Power metrics (from TELEMETRY_APP PowerMetrics, field 5).
    // Voltages in volts, currents in milliamps.
    public float? Ch1VoltageV  { get; set; }
    public float? Ch1CurrentMa { get; set; }
    public float? Ch2VoltageV  { get; set; }
    public float? Ch2CurrentMa { get; set; }
    public float? Ch3VoltageV  { get; set; }
    public float? Ch3CurrentMa { get; set; }

    /// <summary>Most recent NODE_STATUS_APP status string heard from this node.</summary>
    public string NodeStatus { get; set; } = string.Empty;

    /// <summary>Peer's 32-byte X25519 public key (from NODEINFO field 8),
    /// hex-encoded; empty if not yet learned. Enables PKC direct messages.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>True once we hold a valid 32-byte X25519 public key for this
    /// node, meaning encrypted (PKC) direct messages are possible.</summary>
    public bool HasPublicKey =>
        !string.IsNullOrEmpty(PublicKey) && PublicKey.Length == 64;

    /// <summary>Set when a NodeInfo arrived carrying a public key that differs
    /// from the one we already trust for this node (a possible key-substitution
    /// or the peer re-keyed). Null = unknown/unchanged. The previously trusted
    /// key is kept until the user explicitly requests new keys.</summary>
    public bool? KeyMismatch { get; set; }

    /// <summary>Convenience flag for the UI: the stored key is in a mismatch
    /// state and should be shown as suspect (red key icon).</summary>
    public bool HasKeyMismatch => KeyMismatch == true;

    /// <summary>When true, the peer advertises that it should not be messaged.
    /// Null when unknown / not advertised.</summary>
    public bool? IsUnmessagable { get; set; }

    /// <summary>The peer advertises amateur-radio operation (User.is_licensed).
    /// Null when it has never said either way, which the licensed relay rules
    /// treat differently from an explicit false.</summary>
    public bool? IsLicensed { get; set; }

    /// <summary>Set once we've verified a valid XEdDSA signature on a
    /// broadcast from this node (mirrors firmware's per-node
    /// <c>HAS_XEDDSA_SIGNED</c> bit — see <see cref="MeshRF.Mesh.MeshCrypto.XeddsaVerify"/>).
    /// Monotonic like firmware: only ever set true, cleared only when the
    /// node's public key changes. Null = never verified / no key on file.</summary>
    public bool? HasXeddsaSigned { get; set; }

    /// <summary>Convenience flag for the UI: show the "verified" shield icon.</summary>
    public bool IsXeddsaVerified => HasXeddsaSigned == true;

    /// <summary>Convenience flag for UI visibility bindings.</summary>
    public bool IsUnmessagableTrue => IsUnmessagable == true;

    /// <summary>True when this node's stored public key hashes back to the
    /// same node number Meshtastic derives from PKI identity.</summary>
    public bool HasDerivedNodeNumMatch =>
        PkiNodeNumber.TryFromHexPublicKey(PublicKey, out var derivedNodeNum)
        && derivedNodeNum == NodeNum;

    /// <summary>True when both latitude and longitude are present.</summary>
    public bool HasLocation => Latitude.HasValue && Longitude.HasValue;

    private const double NearOriginInvalidDegrees = 0.01;

    /// <summary>True when a position exists but is clearly invalid for mapping:
    /// near the (0,0) sentinel or coordinates outside the legal lat/lon bounds.</summary>
    public bool HasInvalidLocation
    {
        get
        {
            if (Latitude is not double lat || Longitude is not double lon)
                return false;

            if (Math.Abs(lat) < NearOriginInvalidDegrees &&
                Math.Abs(lon) < NearOriginInvalidDegrees)
                return true;

            return lat is < -90 or > 90 || lon is < -180 or > 180;
        }
    }

    /// <summary>When true, text messages from this node do not play the RTTTL ringtone.</summary>
    public bool MuteRtttl { get; set; }

    /// <summary>When true, packets from this node are ignored by the app.</summary>
    public bool Ignored { get; set; }

    /// <summary>When true, this node is marked as a favorite for quick access.</summary>
    public bool Favorite { get; set; }

    public DateTime LastHeard =>
        LastHeardEpoch == 0 ? DateTime.MinValue
            : DateTimeOffset.FromUnixTimeSeconds(LastHeardEpoch).LocalDateTime;

    /// <summary>Convenient identifier for UI display.</summary>
    public string DisplayId =>
        !string.IsNullOrEmpty(UserId) ? UserId : $"!{NodeNum:x8}";
}
