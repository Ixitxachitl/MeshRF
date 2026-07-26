// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Waypoints;

/// <summary>A stored waypoint shared over the mesh.</summary>
public sealed class WaypointRecord
{
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

    public bool HasGeofence => GeofenceRadius > 0 || BboxWest is not null;

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

    public bool IsExpired =>
        ExpireEpoch != 0 && ExpireEpoch <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public string ExpiryStatus => IsExpired ? "EXPIRED" : "ACTIVE";

    public DateTime? ExpireTime =>
        ExpireEpoch == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(ExpireEpoch).LocalDateTime;
}
