// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// A waypoint a script drops on the mesh.
/// </summary>
/// <remarks>
/// Coordinates are templates rather than numbers because the interesting case
/// is a position that came out of an <c>http:</c> action. The literal string
/// <c>home</c> uses this node's configured home location, which is what a
/// script marking local weather almost always wants.
/// </remarks>
public sealed class ScriptWaypoint
{
    /// <summary>Latitude template, or <c>home</c>.</summary>
    public string Latitude { get; init; } = string.Empty;

    /// <summary>Longitude template, or empty when <see cref="Latitude"/> is
    /// <c>home</c>.</summary>
    public string Longitude { get; init; } = string.Empty;

    /// <summary>True when the position is this node's home location rather than
    /// an explicit pair.</summary>
    public bool UseHome { get; init; }

    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Emoji shown on the map, or empty for the default marker.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Geofence radius in metres. 0 = a plain point with no fence.</summary>
    public uint RadiusM { get; init; }

    /// <summary>How long the waypoint lasts. Zero means it does not expire —
    /// rarely what a script wants, since an automated marker nobody clears
    /// stays on everyone's map forever.</summary>
    public TimeSpan Expires { get; init; }

    /// <summary>Alert receivers when they cross into the geofence. Only
    /// meaningful alongside a radius.</summary>
    public bool NotifyOnEnter { get; init; }

    public bool NotifyOnExit { get; init; }

    /// <summary>
    /// Node to address the marker to, as <c>!a1b2c3d4</c> or a placeholder.
    /// Empty broadcasts it on <see cref="Channel"/>.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="Channel"/>: a marker goes to one node
    /// or out on one channel, the same rule <c>send:</c> follows. A directed
    /// marker still travels under the primary channel's key — the address only
    /// says who it is for, so this saves everyone else drawing it rather than
    /// keeping it from them.
    /// </remarks>
    public string To { get; init; } = string.Empty;

    /// <summary>Channel to send on, or empty for the primary. The literal
    /// <c>primary</c> names it by role, for a mesh whose primary has no name of
    /// its own.</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>Only this node may edit or clear it. Keeps a script's markers
    /// from being rewritten by anyone who receives them.</summary>
    public bool LockToMe { get; init; } = true;

    /// <summary>Hop limit for the marker, or null to use the app's configured
    /// limit.</summary>
    public byte? Hops { get; init; }
}
