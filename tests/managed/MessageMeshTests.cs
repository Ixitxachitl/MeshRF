// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Messages;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A stored message remembers which mesh it was heard on. A channel name is
/// not unique across meshes — somebody running MediumFast may well have named
/// their primary channel "LongFast", and the LongFast mesh has a channel of
/// that name too — so the name alone cannot say where a message belongs.
/// </summary>
public sealed class MessageMeshTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public MessageMeshTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-msgmesh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "messages.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MessageRecord Text(uint packetId, string channel, string preset, string text) => new()
    {
        PacketId = packetId,
        FromNode = 0x3840dd32u,
        ToNode = 0xFFFFFFFFu,
        Channel = channel,
        Preset = preset,
        PortNum = (int)PortNum.TextMessage,
        Text = text,
        Decrypted = true,
        RxEpoch = 1000 + packetId,
    };

    [Fact]
    public void TheMeshIsStoredAndComesBack()
    {
        using var store = new MessageStore(_db);
        store.Add(Text(1, "LongFast", "", "on my own mesh"));
        store.Add(Text(2, "LongFast", "LongFast", "on the LongFast mesh"));

        var all = store.TextHistory();
        Assert.Equal(string.Empty, Assert.Single(all, m => m.PacketId == 1).Preset);
        Assert.Equal("LongFast", Assert.Single(all, m => m.PacketId == 2).Preset);
    }

    [Fact]
    public void ClearingOneMeshsChannelLeavesTheOtherAlone()
    {
        using var store = new MessageStore(_db);
        store.Add(Text(1, "LongFast", "", "on my own mesh"));
        store.Add(Text(2, "LongFast", "LongFast", "on the LongFast mesh"));

        store.ClearChannel("LongFast", "LongFast");

        var left = store.TextHistory();
        Assert.Equal(1u, Assert.Single(left).PacketId);

        // And the primary's, named the same, clears on its own terms.
        store.ClearChannel("LongFast");
        Assert.Empty(store.TextHistory());
    }

    /// <summary>Messages stored before the column existed were all on the one
    /// mesh there was, which is what an empty preset means.</summary>
    [Fact]
    public void MessagesStoredBeforeTheColumnReadAsThePrimarys()
    {
        using (var conn = new SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE messages (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    packet_id   INTEGER NOT NULL,
                    from_node   INTEGER NOT NULL,
                    to_node     INTEGER NOT NULL,
                    channel     TEXT    NOT NULL DEFAULT '',
                    portnum     INTEGER NOT NULL DEFAULT 0,
                    text        TEXT    NOT NULL DEFAULT '',
                    payload_hex TEXT    NOT NULL DEFAULT '',
                    decrypted   INTEGER NOT NULL DEFAULT 0,
                    rx_epoch    INTEGER NOT NULL DEFAULT 0,
                    rssi_dbfs   REAL,
                    snr_db      REAL
                );
                INSERT INTO messages (packet_id, from_node, to_node, channel, portnum, text, decrypted, rx_epoch)
                    VALUES (7, 17, 4294967295, 'LongFast', 1, 'old one', 1, 500);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new MessageStore(_db);
        var old = Assert.Single(store.TextHistory());
        Assert.Equal("old one", old.Text);
        Assert.Equal(string.Empty, old.Preset);

        store.Add(Text(8, "LongFast", "LongFast", "a second mesh"));
        Assert.Equal(2, store.TextHistory().Count);
    }
}
