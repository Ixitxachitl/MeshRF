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

    public static TerrainTiles Tiles => s_tiles.Value;
}
