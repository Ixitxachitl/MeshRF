// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The one elevation source the app reads terrain through.
///
/// Shared rather than created per window because the state that matters lives
/// in the source: the disk cache, and the backoff that stops a failing tile
/// being re-requested. A calibration run walks the same ground as every link
/// profile around it, so the second window to ask for a path usually pays
/// nothing at all.
/// </summary>
public static class SharedTerrain
{
    private static readonly Lazy<TerrainTiles> s_tiles = new(() => new TerrainTiles());
    private static readonly Lazy<OverpassBuildings> s_buildings = new(() => new OverpassBuildings());

    public static TerrainTiles Tiles => s_tiles.Value;

    /// <summary>Building footprints, on the same terms and for the same
    /// reasons: one disk cache, one backoff, and a public service that should
    /// see one client rather than one per window.</summary>
    public static OverpassBuildings Buildings => s_buildings.Value;

    /// <summary>The footprints around a point, or an empty index when
    /// buildings are switched off. Keeps every caller from repeating the
    /// settings check.</summary>
    public static Task<BuildingExtract> BuildingsAroundAsync(
        AppSettings settings, GeoPoint centre, double radiusM, CancellationToken ct = default) =>
        settings.BuildingLossEnabled
            ? Buildings.AroundAsync(centre, radiusM, ct)
            : Task.FromResult(BuildingExtract.None);

    /// <summary>The loss model the user has configured.</summary>
    public static BuildingLossModel LossModel(AppSettings settings) =>
        new(settings.BuildingLossPerCrossingDb, settings.BuildingLossPerHundredMetresDb);
}
