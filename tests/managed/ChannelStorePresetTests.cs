// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// One channel list per preset. Rows are keyed by (preset, index), so two
/// lists can each have an index 0; a database from before there were lists
/// is rebuilt with every row in the primary's.
/// </summary>
public sealed class ChannelStorePresetTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public ChannelStorePresetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-chanpreset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "channels.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ChannelConfig Channel(string preset, int index, string name, ChannelRole role = ChannelRole.Secondary) =>
        new() { Preset = preset, Index = index, Name = name, Role = role, Psk = new byte[] { 0x01 } };

    [Fact]
    public void TwoListsCanEachHoldAnIndexZero()
    {
        using var store = new ChannelStore(_db);
        store.Upsert(Channel("", 0, "MediumFast", ChannelRole.Primary));
        store.Upsert(Channel("LongFast", 0, "LongFast", ChannelRole.Primary));
        store.Upsert(Channel("LongFast", 1, "club"));

        var all = store.All();
        Assert.Equal(3, all.Count);
        // The primary's list first, then the preset's, each in index order.
        Assert.Equal(new[] { "", "LongFast", "LongFast" }, all.Select(c => c.Preset));
        Assert.Equal(new[] { "MediumFast", "LongFast", "club" }, all.Select(c => c.Name));

        var longFast = store.ForPreset("LongFast");
        Assert.Equal(2, longFast.Count);
        Assert.Single(store.ForPreset(""));
        Assert.Equal(new[] { "", "LongFast" }, store.Presets());
    }

    [Fact]
    public void DeleteTakesTheListIntoAccount()
    {
        using var store = new ChannelStore(_db);
        store.Upsert(Channel("", 0, "MediumFast", ChannelRole.Primary));
        store.Upsert(Channel("LongFast", 0, "LongFast", ChannelRole.Primary));

        store.Delete("LongFast", 0);
        Assert.Single(store.All());
        Assert.Equal("", store.All()[0].Preset);

        // The one-argument form is the primary's list.
        store.Delete(0);
        Assert.Empty(store.All());
    }

    [Fact]
    public void UpsertReplacesWithinItsOwnList()
    {
        using var store = new ChannelStore(_db);
        store.Upsert(Channel("", 1, "one"));
        store.Upsert(Channel("LongFast", 1, "other"));
        store.Upsert(Channel("", 1, "renamed"));

        Assert.Equal("renamed", Assert.Single(store.ForPreset("")).Name);
        Assert.Equal("other", Assert.Single(store.ForPreset("LongFast")).Name);
    }

    [Fact]
    public void ADatabaseKeyedByIndexAloneIsRebuiltIntoThePrimaryList()
    {
        using (var conn = new SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE channels (
                    idx                 INTEGER PRIMARY KEY,
                    name                TEXT    NOT NULL DEFAULT '',
                    psk                 BLOB    NOT NULL,
                    role                INTEGER NOT NULL DEFAULT 0,
                    position_precision  INTEGER NOT NULL DEFAULT 13,
                    uplink_enabled      INTEGER NOT NULL DEFAULT 0,
                    downlink_enabled    INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO channels (idx, name, psk, role, position_precision, uplink_enabled, downlink_enabled)
                    VALUES (0, 'MediumFast', X'01', 1, 13, 1, 0),
                           (2, 'club', X'01', 2, 0, 0, 1);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new ChannelStore(_db);
        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.All(all, c => Assert.Equal("", c.Preset));
        Assert.Equal(new[] { 0, 2 }, all.Select(c => c.Index));
        Assert.Equal(ChannelRole.Primary, all[0].Role);
        Assert.True(all[0].UplinkEnabled);
        Assert.True(all[1].DownlinkEnabled);

        // And the rebuilt table takes a second list.
        store.Upsert(Channel("LongFast", 0, "LongFast", ChannelRole.Primary));
        Assert.Equal(3, store.All().Count);
    }
}
