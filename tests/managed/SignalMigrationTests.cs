// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Readings taken before the receiver measured properly are thrown away when
/// the database is first opened by a version that does.
///
/// They cannot be corrected. The stored level was the receiver's level at the
/// moment the app got round to the packet — for a frame already over, the
/// noise that followed it. The stored SNR carried the spreading gain, so it
/// read tens of dB high by an amount that depended on the preset and was never
/// written down. The path loss fit reads both, and mixed with readings that do
/// mean something they are worse than nothing.
/// </summary>
public sealed class SignalMigrationTests : IDisposable
{
    private readonly string _dir;

    public SignalMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-sigmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// A nodes database shaped the way the old version left one.
    ///
    /// Built by making a real one and putting the signal columns back how they
    /// were, rather than by hand: a hand-written table is missing every column
    /// the migration does not mention, which is not the shape anybody actually
    /// has on disk.
    /// </summary>
    private string OldNodesDb()
    {
        var path = Path.Combine(_dir, "nodes.db");
        using (var store = new NodeStore(path))
            store.Upsert(new NodeRecord
            {
                NodeNum = 7,
                LongName = "Alta Repeater",
                LastHeardEpoch = 1000,
                HopsAway = 2,
            });
        SqliteConnection.ClearAllPools();

        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE nodes DROP COLUMN rssi_is_dbm;
                ALTER TABLE nodes RENAME COLUMN rssi TO rssi_dbm;
                UPDATE nodes SET snr_db = 17.5, rssi_dbm = -46.0,
                                 best_hops_snr = 19.0, best_hops_rssi = -44.0;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public void OldNodeReadingsGoAndTheNodeStays()
    {
        var path = OldNodesDb();

        using var store = new NodeStore(path);
        var node = store.Get(7);

        Assert.NotNull(node);
        // Everything that was actually observed survives.
        Assert.Equal("Alta Repeater", node!.LongName);
        Assert.Equal(1000, node.LastHeardEpoch);
        Assert.Equal((byte)2, node.HopsAway);
        // The two that were never what they claimed do not.
        Assert.Null(node.Rssi);
        Assert.Null(node.SnrDb);
        Assert.Equal(string.Empty, node.RssiText);
        // Including the best path's copy of them, which the link budget reads.
        Assert.Null(node.BestHopsSnrDb);
        Assert.Null(node.BestHopsRssi);
    }

    /// <summary>And it happens once. A reading taken after the upgrade is not
    /// wiped by the next start.</summary>
    [Fact]
    public void ReadingsTakenAfterwardsSurvive()
    {
        var path = OldNodesDb();

        using (var store = new NodeStore(path))
            store.RecordSighting(7, new SignalReading(-11.5f, -104f, true));
        SqliteConnection.ClearAllPools();

        using (var reopened = new NodeStore(path))
        {
            var node = reopened.Get(7);
            Assert.Equal(-104f, node!.Rssi);
            Assert.Equal(-11.5f, node.SnrDb);
            Assert.True(node.RssiIsDbm);
            Assert.Equal("-104 dBm", node.RssiText);
        }
    }

    /// <summary>A level off an SDR comes back knowing it is not dBm, so
    /// nothing downstream prints it as power or publishes it as power.
    /// </summary>
    [Fact]
    public void AnSdrLevelComesBackKnowingItIsNotPower()
    {
        var path = Path.Combine(_dir, "nodes-sdr.db");
        using var store = new NodeStore(path);
        store.RecordSighting(9, new SignalReading(-9.0f, -47.5f, false));

        var node = store.Get(9);
        Assert.Equal(-47.5f, node!.Rssi);
        Assert.False(node.RssiIsDbm);
        Assert.Equal("-48 dBFS", node.RssiText);
    }

    [Fact]
    public void OldMessageReadingsGoAndTheMessageStays()
    {
        var path = Path.Combine(_dir, "messages.db");
        using (var store = new MessageStore(path))
            store.Add(new MessageRecord
            {
                PacketId = 1, FromNode = 7, ToNode = 0xFFFFFFFFu,
                Text = "hello", RxEpoch = 1000, Decrypted = true,
            });
        SqliteConnection.ClearAllPools();

        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE messages DROP COLUMN rssi_is_dbm;
                ALTER TABLE messages RENAME COLUMN rssi TO rssi_dbfs;
                UPDATE messages SET rssi_dbfs = -46.0, snr_db = 17.5;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var reopened = new MessageStore(path);
        var stored = Assert.Single(reopened.Recent());

        Assert.Equal("hello", stored.Text);
        Assert.Equal(1000, stored.RxEpoch);
        Assert.Null(stored.Rssi);
        Assert.Null(stored.SnrDb);
    }
}
