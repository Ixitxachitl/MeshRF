// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Waypoints;

/// <summary>A stored waypoint shared over the mesh.</summary>
public sealed class WaypointRecord : System.ComponentModel.INotifyPropertyChanged
{
    // Coarse INPC, same shape as NodeRecord's: rows are replaced wholesale on
    // update, and NotifyChanged lets the unit-system owner re-run every
    // binding (the expiry/heard columns render dates in the unit-aware form).
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raise the all-properties change so bound rows re-render.</summary>
    public void NotifyChanged() =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));

    /// <summary>Sentinel for "never expires" that matches the official
    /// Meshtastic Android/iOS clients (they set/compare against
    /// <c>Int.MAX_VALUE</c> for a fresh waypoint's <c>expire</c> field, rather
    /// than 0 — firmware's single-slot OLED display treats a 0 expire as
    /// already-expired, so 0 alone isn't a reliable "never expires" signal).
    /// Both 0 and this value are treated as "no expiration" for compatibility
    /// with older waypoints that did use 0.</summary>
    public const uint NeverExpiresEpoch = 2147483647u; // Int32.MaxValue

    public long Id { get; set; }
    public uint FromNode { get; set; }
    public uint WaypointId { get; set; }
    public uint PacketId { get; set; }
    public string Channel { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public uint? Icon { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int? AltitudeM { get; set; }

    public uint ExpireEpoch { get; set; }

    /// <summary>
    /// Node the marker was addressed to, or 0 for one broadcast to a channel.
    /// </summary>
    /// <remarks>
    /// Says who draws it, not who may read it: a directed marker still travels
    /// under a channel key, so everyone on that channel can decrypt it and
    /// simply declines to put it on the map. Distinct from
    /// <see cref="LockedTo"/>, which says who may change it — a marker can be
    /// addressed to one node and locked to another, or to neither.
    /// </remarks>
    public uint ToNode { get; set; }

    /// <summary>Whether the marker names a recipient rather than going to a
    /// whole channel.</summary>
    public bool IsDirected => ToNode != 0;

    /// <summary>Recipient for the list, empty for a broadcast.</summary>
    public string ToId => IsDirected ? $"!{ToNode:x8}" : string.Empty;

    /// <summary>
    /// Node this waypoint is locked to, or 0 for one anybody may edit.
    /// </summary>
    /// <remarks>
    /// The lock is a rule each client keeps for itself — firmware never reads
    /// the field, and holds no waypoint list to apply it to. An unlocked
    /// marker is therefore open to anybody: any node may move it, rename it or
    /// retire it, and this map accepts that. A locked one takes changes only
    /// from the node named here, so it belongs to whoever placed it for as
    /// long as it lives.
    /// </remarks>
    public uint LockedTo { get; set; }

    /// <summary>
    /// The node reading the list — us — so a row can say whether a lock is
    /// ours without the view having to work it out per cell.
    /// </summary>
    /// <remarks>
    /// Stamped by the host as records arrive and again when this node's
    /// identity changes, rather than being read from ambient state, so the
    /// lock columns stay testable without a radio.
    /// </remarks>
    public uint ViewerNodeNum { get; set; }

    /// <summary>Whether the waypoint is locked to any node at all.</summary>
    public bool IsLocked => LockedTo != 0;

    /// <summary>Locked, and to us — we may edit it and retire it on the
    /// mesh.</summary>
    public bool IsLockedToUs => LockedTo != 0 && LockedTo == ViewerNodeNum;

    /// <summary>
    /// Locked to somebody else. It can be deleted from this node's own list,
    /// but nothing is sent when it is: the owner keeps it, and an expiry we
    /// broadcast for it would be ignored.
    /// </summary>
    public bool IsLockedToAnother => LockedTo != 0 && LockedTo != ViewerNodeNum;

    /// <summary>Who the lock names, for a tooltip.</summary>
    public string LockedToId => $"!{LockedTo:x8}";

    /// <summary>Circular geofence radius in meters. 0 = no circular geofence.</summary>
    public uint GeofenceRadius { get; set; }

    /// <summary>Optional rectangular geofence bounds (degrees), all four set together.</summary>
    public double? BboxWest { get; set; }
    public double? BboxSouth { get; set; }
    public double? BboxEast { get; set; }
    public double? BboxNorth { get; set; }

    public bool NotifyOnEnter { get; set; }
    public bool NotifyOnExit { get; set; }
    public bool NotifyFavoritesOnly { get; set; }

    public bool HasCircularGeofence => GeofenceRadius > 0;
    public bool HasBoundingBoxGeofence => BboxWest is not null;
    public bool HasGeofence => HasCircularGeofence || HasBoundingBoxGeofence;

    /// <summary>Short label for the waypoint list's "Type" column: whether this
    /// is a plain point or carries a circular and/or rectangular geofence.</summary>
    public string GeofenceKindText => (HasCircularGeofence, HasBoundingBoxGeofence) switch
    {
        (true, true) => "Circle+Box",
        (true, false) => "Circle",
        (false, true) => "Box",
        _ => "Point",
    };

    /// <summary>
    /// The channel for the list, naming the primary by role when the record
    /// carries no name for it.
    /// </summary>
    /// <remarks>
    /// Worth showing rather than leaving implicit: this is the channel a resend
    /// looks up by name, and the one a geofence crossing is posted into, so a
    /// marker whose channel is not on this mesh fails both in ways nothing else
    /// on the row would explain. A default-preset primary has no name of its
    /// own, which is why an empty one is a label rather than a blank.
    /// </remarks>
    public string ChannelText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Channel) ? "(primary)" : Channel;
            // Two meshes can each have a channel of that name, so the preset
            // is what says which one this marker is on — and which one a
            // resend or an edit would go out on.
            return Preset.Length == 0 ? name : $"{name} · {Preset}";
        }
    }

    /// <summary>
    /// Which channel list this marker belongs to, and so which listener's
    /// settings it was heard on or sent with: empty for the primary's,
    /// otherwise a preset name.
    /// </summary>
    /// <remarks>
    /// Empty on every row written before there was more than one list, which
    /// is correct for them: there was only the primary to have heard them.
    /// </remarks>
    public string Preset { get; set; } = string.Empty;

    public long RxEpoch { get; set; }

    public DateTime RxTime =>
        DateTimeOffset.FromUnixTimeSeconds(RxEpoch).LocalDateTime;

    public string FromId => $"!{FromNode:x8}";

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"Waypoint {WaypointId:x8}"
        : Name;

    public string ToolTipText => string.IsNullOrWhiteSpace(Description)
        ? DisplayName
        : Description;

    public bool HasIcon => Icon is not null and > 0;

    public string IconText
    {
        get
        {
            if (Icon is not uint icon || icon == 0) return string.Empty;
            try { return char.ConvertFromUtf32((int)icon); }
            catch { return string.Empty; }
        }
    }

    public bool HasExpiry => ExpireEpoch != 0 && ExpireEpoch != NeverExpiresEpoch;

    public bool IsExpired =>
        HasExpiry && ExpireEpoch <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public string ExpiryStatus => IsExpired ? "EXPIRED" : "ACTIVE";

    public DateTime? ExpireTime =>
        HasExpiry ? DateTimeOffset.FromUnixTimeSeconds(ExpireEpoch).LocalDateTime : null;
}
