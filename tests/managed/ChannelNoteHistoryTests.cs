// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Messages;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A geofence crossing is posted into a channel as a note, on the note port
/// rather than the text port. The startup replay reads its history back out of
/// the store, so a query that asks only for text rows rebuilds the room with
/// every crossing missing — the alerts were in the database the whole time.
/// </summary>
public class ChannelNoteHistoryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public ChannelNoteHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "messages.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>What AvaloniaMeshRxHost.PersistChannelNote writes: nobody in
    /// from_node, us in to_node, and the channel it was posted into. The packet
    /// id is random there and distinct here, since the store drops a repeat of
    /// (packet id, from, port) as a retransmission.</summary>
    private static MessageRecord GeofenceNote(string channel, string text, long epoch) => new()
    {
        PacketId = (uint)epoch,
        FromNode = 0,
        ToNode = 0xdeadbeef,
        Channel = channel,
        PortNum = MessageStore.ConversationNotePort,
        Text = text,
        Decrypted = true,
        RxEpoch = epoch,
    };

    private static MessageRecord ChannelText(string channel, string text, long epoch) => new()
    {
        PacketId = (uint)epoch,
        FromNode = 0xcafebabe,
        ToNode = 0xFFFFFFFFu,
        Channel = channel,
        PortNum = 1,
        Text = text,
        Decrypted = true,
        RxEpoch = epoch,
    };

    [Fact]
    public void GeofenceNotesComeBackWithTheChannelHistory()
    {
        using var store = new MessageStore(_db);
        store.Add(ChannelText("LongFast", "hi", 1_000));
        store.Add(GeofenceNote("LongFast", "Alice entered geofence \"Home\"", 1_001));
        store.Add(GeofenceNote("LongFast", "Alice exited geofence \"Home\"", 1_002));

        var history = store.TextHistory();

        Assert.Equal(3, history.Count);
        Assert.Contains(history, m => m.Text.Contains("entered geofence"));
        Assert.Contains(history, m => m.Text.Contains("exited geofence"));
    }

    [Fact]
    public void HistoryStaysInOrderAcrossBothKinds()
    {
        using var store = new MessageStore(_db);
        store.Add(GeofenceNote("LongFast", "first", 1_000));
        store.Add(ChannelText("LongFast", "second", 1_001));
        store.Add(GeofenceNote("LongFast", "third", 1_002));

        Assert.Equal(["first", "second", "third"], store.TextHistory().Select(m => m.Text));
    }

    [Fact]
    public void ClearingAChannelTakesItsNotesToo()
    {
        using var store = new MessageStore(_db);
        store.Add(ChannelText("LongFast", "hi", 1_000));
        store.Add(GeofenceNote("LongFast", "Alice entered geofence \"Home\"", 1_001));
        store.Add(GeofenceNote("Secondary", "Alice entered geofence \"Work\"", 1_002));

        store.ClearChannel("LongFast");

        var left = store.TextHistory();
        Assert.Single(left);
        Assert.Equal("Secondary", left[0].Channel);
    }

    /// <summary>A note written against a conversation carries the peer in
    /// from_node, and belongs to that DM tab rather than to a channel room.
    /// Clearing a channel must not reach into one.</summary>
    [Fact]
    public void ConversationNotesAreNotChannelNotes()
    {
        using var store = new MessageStore(_db);
        store.Add(new MessageRecord
        {
            PacketId = 0x99,
            FromNode = 0xcafebabe,
            ToNode = 0xdeadbeef,
            Channel = "ACK",
            PortNum = MessageStore.ConversationNotePort,
            Text = "delivered",
            Decrypted = true,
            RxEpoch = 1_000,
        });

        store.ClearChannel("ACK");

        Assert.Single(store.TextHistory());
    }
}
