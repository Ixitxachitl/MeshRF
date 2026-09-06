// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A node records what it was last heard on. Last sighting wins, a sighting
/// that came over no radio leaves it alone, and a database from before the
/// column existed gains it on open.
/// </summary>
public sealed class NodeStoreHeardOnTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public NodeStoreHeardOnTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-heardon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "nodes.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void LastSightingWinsAndAnUnknownOneKeepsWhatIsThere()
    {
        using var store = new NodeStore(_db);
        store.RecordSighting(7, heardOnPreset: "LongFast", heardOnFreqMHz: 906.875);
        var first = store.Get(7)!;
        Assert.Equal("LongFast", first.HeardOnPreset);
        Assert.Equal(906.875, first.HeardOnFreqMHz!.Value, 6);

        store.RecordSighting(7, heardOnPreset: "MediumFast", heardOnFreqMHz: 913.125);
        Assert.Equal("MediumFast", store.Get(7)!.HeardOnPreset);
        Assert.Equal(913.125, store.Get(7)!.HeardOnFreqMHz!.Value, 6);

        // A sighting with no radio behind it, like one from the broker.
        store.RecordSighting(7, rssiDbm: -90);
        Assert.Equal("MediumFast", store.Get(7)!.HeardOnPreset);
        Assert.Equal(913.125, store.Get(7)!.HeardOnFreqMHz!.Value, 6);

        store.RecordSighting(7, heardOnPreset: HeardOn.Custom, heardOnFreqMHz: 915.0);
        Assert.Equal(HeardOn.Custom, store.Get(7)!.HeardOnPreset);
    }

    [Fact]
    public void SurvivesReopening()
    {
        using (var store = new NodeStore(_db))
            store.RecordSighting(9, heardOnPreset: "LongFast", heardOnFreqMHz: 906.875);
        SqliteConnection.ClearAllPools();
        using var reopened = new NodeStore(_db);
        Assert.Equal("LongFast", reopened.Get(9)!.HeardOnPreset);
    }

    [Fact]
    public void ADatabaseFromBeforeTheColumnGainsItOnOpen()
    {
        using (var conn = new SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE nodes (
                    node_num         INTEGER PRIMARY KEY,
                    user_id          TEXT    NOT NULL DEFAULT '',
                    long_name        TEXT    NOT NULL DEFAULT '',
                    short_name       TEXT    NOT NULL DEFAULT '',
                    hw_model         TEXT    NOT NULL DEFAULT '',
                    role             TEXT    NOT NULL DEFAULT '',
                    last_heard_epoch INTEGER NOT NULL DEFAULT 0,
                    seen_via_mqtt    INTEGER NOT NULL DEFAULT 0,
                    snr_db           REAL,
                    rssi_dbm         REAL,
                    hops_away        INTEGER,
                    latitude         REAL,
                    longitude        REAL,
                    altitude_m       INTEGER,
                    battery_pct      INTEGER,
                    voltage_v        REAL,
                    channel_util_pct REAL,
                    air_util_tx_pct  REAL,
                    node_status      TEXT    NOT NULL DEFAULT ''
                );
                INSERT INTO nodes (node_num, long_name, last_heard_epoch) VALUES (5, 'Old', 1000);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new NodeStore(_db);
        var old = store.Get(5)!;
        Assert.Equal("Old", old.LongName);
        Assert.Equal(string.Empty, old.HeardOnPreset);
        Assert.Null(old.HeardOnFreqMHz);

        store.RecordSighting(5, heardOnPreset: "ShortFast", heardOnFreqMHz: 904.625);
        Assert.Equal("ShortFast", store.Get(5)!.HeardOnPreset);
    }
}
