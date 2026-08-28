// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// The settings a device role forces, mirroring firmware's
/// <c>NodeDB::installRoleDefaults</c>. A null property means "leave whatever the
/// user has"; a non-null one is a coercion the role applies.
///
/// Without this, picking a role is cosmetic: firmware nodes of the same role
/// broadcast on a schedule the role dictates, and a MeshRF node claiming to be
/// a ROUTER while talking every 30 seconds is misrepresenting itself to the
/// mesh.
/// </summary>
public sealed record RoleDefaults
{
    private const int OneDay = 24 * 60 * 60;

    /// <summary>Firmware <c>default_telemetry_broadcast_interval_secs</c>, whose
    /// IF_ROUTER macro resolves against the role being installed.</summary>
    private const int RouterTelemetrySecs = OneDay / 2;
    private const int ClientTelemetrySecs = 60 * 60;

    public bool? NodeInfoEnabled { get; init; }
    public int? NodeInfoSeconds { get; init; }
    public bool? PositionEnabled { get; init; }
    public int? PositionSeconds { get; init; }
    public bool? DeviceMetricsEnabled { get; init; }
    public int? DeviceMetricsSeconds { get; init; }
    public bool? EnvironmentMetricsEnabled { get; init; }
    public int? EnvironmentMetricsSeconds { get; init; }
    public bool? AirQualityMetricsEnabled { get; init; }
    public bool? NodeStatusEnabled { get; init; }

    /// <summary>Firmware <c>position_broadcast_smart_enabled</c>.</summary>
    public bool? PositionSmartEnabled { get; init; }

    /// <summary>Firmware <c>broadcast_smart_minimum_distance</c>, in metres.</summary>
    public uint? PositionSmartMinMoveMeters { get; init; }

    /// <summary>Firmware <c>broadcast_smart_minimum_interval_secs</c>.</summary>
    public int? PositionSmartMinSeconds { get; init; }

    /// <summary>
    /// Whether the altitude we transmit is height above mean sea level
    /// (<c>Position.altitude</c>, field 3) rather than above the ellipsoid
    /// (<c>Position.altitude_hae</c>, field 9).
    ///
    /// This is the only part of firmware's <c>position_flags</c> that can change
    /// what MeshRF puts on the air. The other flags — SPEED, HEADING, DOP,
    /// SATINVIEW — gate fields fed by a GPS receiver's motion and fix-quality
    /// output, which we have no source for, so modelling them would be config
    /// that does nothing.
    /// </summary>
    public bool? PositionAltitudeMsl { get; init; }

    /// <summary>One of <c>RadioViewModel.RebroadcastModeOptions</c>.</summary>
    public string? RebroadcastMode { get; init; }

    public bool? IsUnmessagable { get; init; }

    /// <summary>Everything a role silences. Firmware expresses this as
    /// MAX_INTERVAL rather than a disable flag, but the effect is the same and
    /// MeshRF's auto-reports are enable/interval pairs.</summary>
    private static RoleDefaults Silent(string? rebroadcastMode = null) => new()
    {
        NodeInfoEnabled = false,
        PositionEnabled = false,
        PositionSmartEnabled = false,
        DeviceMetricsEnabled = false,
        EnvironmentMetricsEnabled = false,
        AirQualityMetricsEnabled = false,
        NodeStatusEnabled = false,
        RebroadcastMode = rebroadcastMode,
    };

    public static RoleDefaults For(string? role) => Canonical(role) switch
    {
        "ROUTER" => new RoleDefaults
        {
            DeviceMetricsEnabled = true,
            DeviceMetricsSeconds = RouterTelemetrySecs,
            RebroadcastMode = "CorePortnumsOnly",
            IsUnmessagable = true,
        },

        "ROUTERLATE" => new RoleDefaults
        {
            DeviceMetricsEnabled = true,
            DeviceMetricsSeconds = OneDay,
            IsUnmessagable = true,
        },

        "SENSOR" => new RoleDefaults
        {
            DeviceMetricsEnabled = true,
            DeviceMetricsSeconds = ClientTelemetrySecs,
            EnvironmentMetricsEnabled = true,
            EnvironmentMetricsSeconds = 300,
            IsUnmessagable = true,
        },

        "TRACKER" => new RoleDefaults
        {
            DeviceMetricsEnabled = true,
            DeviceMetricsSeconds = ClientTelemetrySecs,
            IsUnmessagable = true,
        },

        // CoTs carry height above the ellipsoid, so the TAK roles drop
        // ALTITUDE_MSL from position_flags.
        "TAK" => new RoleDefaults
        {
            NodeInfoSeconds = OneDay,
            PositionSeconds = OneDay,
            PositionSmartEnabled = false,
            PositionAltitudeMsl = false,
            DeviceMetricsSeconds = OneDay,
        },

        "TAKTRACKER" => new RoleDefaults
        {
            NodeInfoSeconds = OneDay,
            PositionEnabled = true,
            PositionSeconds = 3 * 60,
            PositionSmartEnabled = true,
            PositionSmartMinMoveMeters = 20,
            PositionSmartMinSeconds = 15,
            PositionAltitudeMsl = false,
            DeviceMetricsSeconds = OneDay,
            IsUnmessagable = true,
        },

        // The point of the role is an unconditional beacon, so smart broadcast
        // comes off: a stationary lost node still has to be findable.
        "LOSTANDFOUND" => new RoleDefaults
        {
            PositionEnabled = true,
            PositionSeconds = 300,
            PositionSmartEnabled = false,
        },

        "CLIENTHIDDEN" => Silent("LocalOnly"),

        _ => new RoleDefaults(),
    };

    /// <summary>
    /// Roles firmware no longer honours. <c>AdminModule</c> rewrites both to
    /// CLIENT the moment a device config carrying them is applied, so a live
    /// mesh contains no node in either role and MeshRF must not advertise one.
    /// The enum values stay decodable for the sake of stale NodeInfo on the air.
    /// </summary>
    public static bool IsDeprecated(string? role) =>
        Canonical(role) is "ROUTERCLIENT" or "REPEATER";

    /// <summary>The role firmware would actually store for a requested one.</summary>
    public static string Effective(string? role) =>
        IsDeprecated(role) ? "Client" : (role ?? string.Empty).Trim();

    /// <summary>Firmware <c>NodeInfoModule::sendOurNodeInfo</c>: a tracker or a
    /// sensor never asks for a reply, so a battery node's beacon can't set off
    /// a round of NodeInfo from everyone that hears it.</summary>
    public static bool AllowsRequestingReplies(string? role) =>
        Canonical(role) is not ("TRACKER" or "SENSOR");

    private static string Canonical(string? s) =>
        (s ?? string.Empty).Trim().Replace("_", string.Empty).ToUpperInvariant();
}
