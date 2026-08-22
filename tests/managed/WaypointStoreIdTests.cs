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
    public void UpsertOfSameSenderAndIdKeepsTheSameRow()
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
}
