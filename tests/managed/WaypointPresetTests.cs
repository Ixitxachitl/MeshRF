// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Waypoints;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A waypoint belongs to the mesh it was heard on. Two presets can each have
/// a channel of the same name, so without recording which, a resend would be
/// sealed with one mesh's key and put on another mesh's frequency.
/// </summary>
public sealed class WaypointPresetTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public WaypointPresetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-wp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "waypoints.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WaypointRecord Marker(uint id, string channel, string preset) => new()
    {
        FromNode = 0x11111111u,
        WaypointId = id,
        PacketId = id,
        Channel = channel,
        Preset = preset,
        Name = "Repeater",
        Latitude = 47.6,
        Longitude = -122.3,
        RxEpoch = 1000,
    };

    [Fact]
    public void ThePresetIsStoredAndComesBack()
    {
        using var store = new WaypointStore(_db);
        store.Upsert(Marker(1, "club", "LongFast"));
        store.Upsert(Marker(2, "club", ""));

        var all = store.All();
        Assert.Equal("LongFast", Assert.Single(all, w => w.WaypointId == 1).Preset);
        Assert.Equal(string.Empty, Assert.Single(all, w => w.WaypointId == 2).Preset);
    }

    [Fact]
    public void AnEditKeepsTheMeshItIsOn()
    {
        using var store = new WaypointStore(_db);
        store.Upsert(Marker(1, "club", "LongFast"));

        var edited = Marker(1, "club", "LongFast");
        edited.Name = "Repeater (moved)";
        store.Upsert(edited);

        var back = Assert.Single(store.All());
        Assert.Equal("Repeater (moved)", back.Name);
        Assert.Equal("LongFast", back.Preset);
    }

    /// <summary>The channel column is the channel; which mesh it came off is
    /// a column of its own, as it is in the node list.</summary>
    [Fact]
    public void TheChannelColumnIsTheChannelAndTheMeshIsItsOwn()
    {
        Assert.Equal("club", Marker(1, "club", "LongFast").ChannelText);
        // A default primary has no name of its own, so the label stands in.
        Assert.Equal("(primary)", Marker(1, "", "").ChannelText);
    }

    /// <summary>
    /// Which list a marker is in and which mesh it came off are different
    /// facts: the list is empty for the primary's, but the mesh is whatever
    /// preset the primary is running, which is what the column has to show.
    /// </summary>
    [Fact]
    public void TheMeshItWasHeardOnIsStoredBesideTheListItIsIn()
    {
        using var store = new WaypointStore(_db);

        var onPrimary = Marker(1, "club", "");
        onPrimary.HeardOnPreset = "MediumFast";
        store.Upsert(onPrimary);

        var onLongFast = Marker(2, "club", "LongFast");
        onLongFast.HeardOnPreset = "LongFast";
        store.Upsert(onLongFast);

        var all = store.All();
        var first = Assert.Single(all, w => w.WaypointId == 1);
        Assert.Equal(string.Empty, first.Preset);
        Assert.Equal("MediumFast", first.HeardOnPreset);
        var second = Assert.Single(all, w => w.WaypointId == 2);
        Assert.Equal("LongFast", second.Preset);
        Assert.Equal("LongFast", second.HeardOnPreset);

        // An edit that says nothing about where it was heard leaves it alone.
        var edit = Marker(2, "club", "LongFast");
        edit.Name = "moved";
        store.Upsert(edit);
        Assert.Equal("LongFast", Assert.Single(store.All(), w => w.WaypointId == 2).HeardOnPreset);
    }

    /// <summary>
    /// A database from before there were several meshes has no such column.
    /// Its markers were all heard on the only listener there was, which is
    /// what an empty preset means.
    /// </summary>
    [Fact]
    public void MarkersStoredBeforeTheColumnExistedReadAsThePrimarys()
    {
        using (var conn = new SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE waypoints (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    from_node    INTEGER NOT NULL,
                    waypoint_id  INTEGER NOT NULL,
                    packet_id    INTEGER NOT NULL,
                    channel      TEXT    NOT NULL DEFAULT '',
                    name         TEXT    NOT NULL DEFAULT '',
                    description  TEXT    NOT NULL DEFAULT '',
                    icon         INTEGER,
                    latitude     REAL    NOT NULL,
                    longitude    REAL    NOT NULL,
                    altitude_m   INTEGER,
                    expire_epoch INTEGER NOT NULL DEFAULT 0,
                    locked_to    INTEGER NOT NULL DEFAULT 0,
                    rx_epoch     INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO waypoints (from_node, waypoint_id, packet_id, channel, name, latitude, longitude, rx_epoch)
                    VALUES (17, 9, 9, 'club', 'Old marker', 47.6, -122.3, 500);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new WaypointStore(_db);
        var old = Assert.Single(store.All());
        Assert.Equal("Old marker", old.Name);
        Assert.Equal("club", old.Channel);
        Assert.Equal(string.Empty, old.Preset);

        // And the reopened store takes markers on another mesh alongside it.
        store.Upsert(Marker(10, "club", "LongFast"));
        Assert.Equal(2, store.All().Count);
    }
}
