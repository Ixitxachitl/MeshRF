// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Nodes;

/// <summary>
/// Mirrors the fields Meshtastic firmware tracks per node in its
/// <c>NodeInfo</c> protobuf. We persist the union so the UI / future MQTT
/// bridge can use it directly.
/// </summary>
public sealed class NodeRecord
{
    /// <summary>32-bit Meshtastic node number (e.g. 0xAABBCCDD).</summary>
    public uint NodeNum { get; set; }

    /// <summary>"!aabbccdd" canonical user id.</summary>
    public string UserId { get; set; } = string.Empty;

    public string LongName  { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string HwModel   { get; set; } = string.Empty;
    public string Role      { get; set; } = string.Empty;

    /// <summary>Unix epoch seconds, 0 if never heard.</summary>
    public long LastHeardEpoch { get; set; }

    /// <summary>True when any stored sighting for this node reported <c>via_mqtt</c>.</summary>
    public bool SeenViaMqtt { get; set; }

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
