// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Waypoints;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A stored waypoint carries the row it landed on. Everything that acts on one
/// record — the list's delete and edit, the map's hit test — matches on that
/// id, so records that all share a default 0 would each act on the first one
/// instead of themselves.
/// </summary>
public class WaypointStoreIdTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public WaypointStoreIdTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-wp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "nodes.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WaypointRecord Waypoint(uint waypointId, string name) => new()
    {
        FromNode = 0xcafebabe,
        WaypointId = waypointId,
        Name = name,
        Latitude = 39.05,
        Longitude = -121.07,
        RxEpoch = 1_000,
    };

    [Fact]
    public void UpsertStampsDistinctIds()
    {
        using var store = new WaypointStore(_db);
        var first = Waypoint(1, "Chute Fire");
        var second = Waypoint(2, "Yuba Fire");

        store.Upsert(first);
        store.Upsert(second);

        Assert.NotEqual(0, first.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void UpsertOfTheSameIdKeepsTheSameRow()
    {
        using var store = new WaypointStore(_db);
        var placed = Waypoint(1, "Chute Fire");
        store.Upsert(placed);

        var resent = Waypoint(1, "Chute Fire");
        resent.RxEpoch = 2_000;
        store.Upsert(resent);

        Assert.Equal(placed.Id, resent.Id);
        Assert.Single(store.All());
    }

    [Fact]
    public void ForgetRemovesOnlyTheStampedRow()
    {
        using var store = new WaypointStore(_db);
        var doomed = Waypoint(1, "Magnolia Intermediate School");
        var spared = Waypoint(2, "Nevada County Horseman");
        store.Upsert(doomed);
        store.Upsert(spared);

        store.Forget(doomed.Id);

        var left = store.All();
        Assert.Equal(spared.WaypointId, Assert.Single(left).WaypointId);
    }

    /// <summary>
    /// A marker is identified by its id alone, so an unlocked one retired by
    /// somebody other than the node that placed it lands on the row already
    /// held rather than starting a second one.
    /// </summary>
    [Fact]
    public void AnotherSenderRetiringTheSameIdLandsOnTheSameRow()
    {
        using var store = new WaypointStore(_db);
        var placed = Waypoint(1, "Chute Fire");
        store.Upsert(placed);

        var retired = Waypoint(1, "Chute Fire");
        retired.FromNode = 0x885ec106;
        retired.ExpireEpoch = 1;
        retired.RxEpoch = 2_000;
        store.Upsert(retired);

        Assert.Equal(placed.Id, retired.Id);
        var row = Assert.Single(store.All());
        Assert.Equal(1u, row.ExpireEpoch);
        Assert.True(row.IsExpired);
        // Retiring it does not make it theirs.
        Assert.Equal(0xcafebabeu, row.FromNode);
    }

    /// <summary>
    /// A DB written under the old sender-scoped key could hold one id once per
    /// sender. Opening it collapses those to the freshest, which is what the
    /// unique index on the id alone then holds.
    /// </summary>
    [Fact]
    public void OpeningASenderScopedDbCollapsesDuplicateIds()
    {
        using (var store = new WaypointStore(_db))
        {
            store.Upsert(Waypoint(1, "Chute Fire"));
        }
        SqliteConnection.ClearAllPools();

        // Reinstate the old key and slip in the row it used to allow.
        using (var conn = new SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP INDEX idx_waypoints_waypoint_id;
                CREATE UNIQUE INDEX idx_waypoints_sender_id
                    ON waypoints(from_node, waypoint_id);
                INSERT INTO waypoints (from_node, waypoint_id, name, latitude, longitude, rx_epoch)
                    VALUES (2287911174, 1, 'Chute Fire', 39.05, -121.07, 2000);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var reopened = new WaypointStore(_db);

        var row = Assert.Single(reopened.All());
        Assert.Equal(2_000, row.RxEpoch);
        Assert.Equal(2287911174u, row.FromNode);
    }
}
