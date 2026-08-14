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
        DeviceMetricsEnabled = false,
        EnvironmentMetricsEnabled = false,
        AirQualityMetricsEnabled = false,
        NodeStatusEnabled = false,
        RebroadcastMode = rebroadcastMode,
    };

    public static RoleDefaults For(string? role) => (role ?? string.Empty).Trim().ToUpperInvariant() switch
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

        "TAK" => new RoleDefaults
        {
            NodeInfoSeconds = OneDay,
            PositionSeconds = OneDay,
            DeviceMetricsSeconds = OneDay,
        },

        "TAKTRACKER" => new RoleDefaults
        {
            NodeInfoSeconds = OneDay,
            PositionEnabled = true,
            PositionSeconds = 3 * 60,
            DeviceMetricsSeconds = OneDay,
            IsUnmessagable = true,
        },

        "LOSTANDFOUND" => new RoleDefaults
        {
            PositionEnabled = true,
            PositionSeconds = 300,
        },

        "CLIENTHIDDEN" => Silent("LocalOnly"),

        // Firmware has no CLIENT_HIDDEN-style branch for repeaters, but the role
        // is defined as originating nothing at all (config.proto REPEATER).
        "REPEATER" => Silent(),

        _ => new RoleDefaults(),
    };
}
