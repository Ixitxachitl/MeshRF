// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Waypoints;

/// <summary>A stored waypoint shared over the mesh.</summary>
public sealed class WaypointRecord
{
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
    public uint LockedTo { get; set; }

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
