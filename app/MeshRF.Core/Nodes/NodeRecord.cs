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

    public DateTime LastHeard =>
        LastHeardEpoch == 0 ? DateTime.MinValue
            : DateTimeOffset.FromUnixTimeSeconds(LastHeardEpoch).LocalDateTime;

    /// <summary>Convenient identifier for UI display.</summary>
    public string DisplayId =>
        !string.IsNullOrEmpty(UserId) ? UserId : $"!{NodeNum:x8}";
}
