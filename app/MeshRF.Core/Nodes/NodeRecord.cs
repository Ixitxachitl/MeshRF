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
        FirstHeardEpoch = source.FirstHeardEpoch;
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
    /// <c>aa:bb:cc:dd:ee:ff</c>; empty until it sends us one.</summary>
    /// <remarks>
    /// Deprecated on the wire, and a phone client almost never sees a usable
    /// one: <c>TypeConversions::ConvertToUser</c> zero-fills it for anything a
    /// node serves out of its NodeDB, which is everything a phone reads. We
    /// take NodeInfo off the air instead, where it is the sender's own
    /// <c>owner</c> record with the real MAC in it, so every NodeInfo we have
    /// accepted has carried one. That makes it a durable per-radio identity —
    /// see <see cref="NodeIdentity"/>, which uses it to recognise a node that
    /// changed its number.
    /// </remarks>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Convenience flag for UI visibility bindings.</summary>
    public bool HasMacAddress => !string.IsNullOrEmpty(MacAddress);

    /// <summary>Unix epoch seconds, 0 if never heard.</summary>
    public long LastHeardEpoch { get; set; }

    /// <summary>
    /// When this node was first heard, in Unix epoch seconds; 0 when unknown.
    /// </summary>
    /// <remarks>
    /// Written once, by the insert that creates the row, and never touched
    /// again -- the upsert leaves it out of its update list, which is what
    /// makes it a first sighting rather than a second copy of last-heard. It is
    /// 0 for every node already known before the column existed, and for a row
    /// created by a write that carried no timestamp; callers fall back to the
    /// oldest stored history for those.
    /// </remarks>
    public long FirstHeardEpoch { get; set; }

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

    /// <summary>Hops the most recent packet took, as the protocol reports it.
    /// </summary>
    /// <remarks>A property of that packet rather than of this node: the
    /// firmware overwrites it on every sighting, so a direct neighbour whose
    /// path faded once reads as relayed from then on. See
    /// <see cref="BestPath"/> for the question the RF tools actually ask.
    /// </remarks>
    public byte?  HopsAway    { get; set; }

    /// <summary>What the node was last heard on: a preset name, or
    /// <see cref="Mesh.HeardOn.Custom"/> for a custom-parameter primary.
    /// Empty until it has been heard over the air since this was recorded.
    /// Last sighting wins, like <see cref="HopsAway"/>: a node has one radio
    /// configuration, and this is the most recent evidence of it.</summary>
    public string HeardOnPreset { get; set; } = string.Empty;

    /// <summary>The channel centre it was heard on, in MHz, beside
    /// <see cref="HeardOnPreset"/>; the two together say which mesh.</summary>
    public double? HeardOnFreqMHz { get; set; }

    // The best path this node has been heard over at the geometry it is at
    // now, kept beside the protocol's value rather than replacing it. Cleared
    // whenever either end moves -- see MeshRF.Mesh.Directness.
    public byte?   BestHops        { get; set; }
    public long?   BestHopsEpoch   { get; set; }
    public float?  BestHopsSnrDb   { get; set; }
    public float?  BestHopsRssiDbm { get; set; }
    public double? BestHopsMyLat   { get; set; }
    public double? BestHopsMyLon   { get; set; }
    public double? BestHopsPeerLat { get; set; }
    public double? BestHopsPeerLon { get; set; }

    /// <summary>The stored hearing, or null when this node has never been
    /// heard with both positions known.</summary>
    public Mesh.DirectSighting? BestPath =>
        BestHops is { } hops && BestHopsEpoch is { } epoch
            && BestHopsMyLat is { } myLat && BestHopsMyLon is { } myLon
            && BestHopsPeerLat is { } peerLat && BestHopsPeerLon is { } peerLon
            ? new Mesh.DirectSighting(
                hops, DateTimeOffset.FromUnixTimeSeconds(epoch),
                BestHopsSnrDb, BestHopsRssiDbm,
                new Map.GeoPoint(myLat, myLon), new Map.GeoPoint(peerLat, peerLon))
            : null;

    /// <summary>Whether this node has been heard over a direct path from where
    /// both ends are now, whatever its last packet did.</summary>
    public bool HeardDirect(DateTimeOffset now) => Mesh.Directness.HeardDirect(BestPath, now);

    /// <summary>The best hop count still worth believing, or null when there is
    /// no usable hearing on file.</summary>
    private byte? BestHopsNow =>
        BestPath is { } best && Mesh.Directness.IsFresh(best, DateTimeOffset.UtcNow)
            ? best.HopsAway
            : null;

    /// <summary>A remembered path worth showing: one strictly better than the
    /// hop count the last packet reported.</summary>
    /// <remarks>Strictly better, not merely different. The bracket exists to
    /// reveal a short path the protocol's figure is hiding; a remembered path
    /// that is longer reveals nothing, because the protocol's own figure is
    /// already the better one and is what every RF tool uses. Showing it
    /// implied knowledge this app does not have — "4 (6)" reads as a claim
    /// about six hops when all it meant was that the shorter path had not been
    /// recorded yet.</remarks>
    private byte? BetterPath =>
        HopsAway is { } hops && BestHopsNow is { } best && best < hops ? best : null;

    /// <summary>The hops cell: what the last packet did, and in brackets the
    /// shorter path this node has actually been heard over when there is one.
    /// </summary>
    /// <remarks>Both, never one. The bare protocol figure hides a direct
    /// neighbour whose path faded once; replacing it would put this app at odds
    /// with the radio's own node list and every other client.</remarks>
    public string HopsDisplay =>
        HopsAway is not { } hops ? string.Empty
        : BetterPath is { } best ? $"{hops} ({best})"
        : hops.ToString();

    /// <summary>Why the hops cell reads as it does.</summary>
    public string HopsTip =>
        HopsAway is not { } hops ? "Never heard from"
        : BetterPath is { } best
            ? $"Last packet arrived over {hops} hop{(hops == 1 ? "" : "s")}, " +
              $"but this node has been heard at {best} from where both ends are now" +
              (BestPath is { } b ? $" ({Age(DateTimeOffset.UtcNow - b.When)} ago)" : string.Empty)
        : $"Last packet arrived over {hops} hop{(hops == 1 ? "" : "s")}";

    private static string Age(TimeSpan since) =>
        since.TotalMinutes < 90 ? $"{Math.Max(1, (int)since.TotalMinutes)} min"
        : since.TotalHours < 36 ? $"{(int)since.TotalHours} h"
        : $"{(int)since.TotalDays} d";
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
