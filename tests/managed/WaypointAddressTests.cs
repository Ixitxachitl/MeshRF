// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Waypoints;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The node a marker was addressed to, through the store. Recorded late, so the
/// column has to arrive on databases that already exist — and the rows already
/// in them have to keep meaning what they meant.
/// </summary>
public class WaypointAddressTests : IDisposable
{
    private const uint Peer = 0xa1b2c3d4;

    private readonly string _dir;
    private readonly string _db;

    public WaypointAddressTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-wpto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "nodes.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WaypointRecord Waypoint(uint waypointId, uint toNode = 0) => new()
    {
        FromNode = 0xcafebabe,
        WaypointId = waypointId,
        Name = "Trailhead",
        Channel = "LongFast",
        ToNode = toNode,
        Latitude = 39.05,
        Longitude = -121.07,
        RxEpoch = 1_000,
    };

    [Fact]
    public void AnAddressSurvivesTheRoundTrip()
    {
        using (var store = new WaypointStore(_db))
        {
            store.Upsert(Waypoint(1, toNode: Peer));
            store.Upsert(Waypoint(2));
        }

        using var reopened = new WaypointStore(_db);
        var all = reopened.All().ToDictionary(w => w.WaypointId);

        Assert.Equal(Peer, all[1].ToNode);
        Assert.True(all[1].IsDirected);
        Assert.Equal("!a1b2c3d4", all[1].ToId);

        Assert.Equal(0u, all[2].ToNode);
        Assert.False(all[2].IsDirected);
    }

    /// <summary>Re-sending a marker to somebody else re-addresses it, rather
    /// than leaving the row pointing at whoever had it first.</summary>
    [Fact]
    public void ReAddressingAMarkerOverwritesTheOldRecipient()
    {
        using var store = new WaypointStore(_db);

        store.Upsert(Waypoint(1, toNode: Peer));
        store.Upsert(Waypoint(1, toNode: 0x00000042));
        Assert.Equal(0x00000042u, Assert.Single(store.All()).ToNode);

        // And back to a broadcast, which is the absence of an address rather
        // than an address of its own.
        store.Upsert(Waypoint(1));
        Assert.Equal(0u, Assert.Single(store.All()).ToNode);
    }

    /// <summary>
    /// A database written before markers recorded their addressee. The column
    /// has to be added on open, and every row already there has to read as a
    /// broadcast — nothing on those rows could say otherwise, and guessing
    /// would put a recipient on markers that never had one.
    /// </summary>
    [Fact]
    public void ADatabaseWrittenBeforeTheColumnExistedStillOpens()
    {
        using (var legacy = new SqliteConnection($"Data Source={_db}"))
        {
            legacy.Open();
            using var cmd = legacy.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE waypoints (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    from_node     INTEGER NOT NULL,
                    waypoint_id   INTEGER NOT NULL,
                    packet_id     INTEGER NOT NULL DEFAULT 0,
                    channel       TEXT    NOT NULL DEFAULT '',
                    name          TEXT    NOT NULL DEFAULT '',
                    description   TEXT    NOT NULL DEFAULT '',
                    icon          INTEGER,
                    latitude      REAL    NOT NULL,
                    longitude     REAL    NOT NULL,
                    altitude_m    INTEGER,
                    expire_epoch  INTEGER NOT NULL DEFAULT 0,
                    locked_to     INTEGER NOT NULL DEFAULT 0,
                    rx_epoch      INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO waypoints (from_node, waypoint_id, channel, name, latitude, longitude)
                VALUES (3405691582, 7, 'LongFast', 'Old Marker', 39.05, -121.07);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new WaypointStore(_db);
        var old = Assert.Single(store.All());

        Assert.Equal("Old Marker", old.Name);
        Assert.Equal(0u, old.ToNode);
        Assert.False(old.IsDirected);

        // And the migrated table takes an address from here on.
        store.Upsert(Waypoint(8, toNode: Peer));
        Assert.Equal(Peer, store.All().Single(w => w.WaypointId == 8).ToNode);
    }
}
