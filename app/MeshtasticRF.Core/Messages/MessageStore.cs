// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Data.Sqlite;

namespace MeshtasticRF.Messages;

/// <summary>
/// SQLite-backed persistent store of received mesh packets, modeled after
/// <see cref="MeshtasticRF.Nodes.NodeStore"/>. Lives at
/// <c>%APPDATA%\MeshtasticRF\messages.db</c>.
/// </summary>
public sealed class MessageStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public static string DefaultPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MeshtasticRF");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "messages.db");
        }
    }

    public MessageStore() : this(DefaultPath) { }

    public MessageStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS messages (
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
            CREATE INDEX IF NOT EXISTS idx_messages_rx ON messages(rx_epoch DESC);
            -- De-dup identical retransmissions of the same packet on a channel.
            CREATE UNIQUE INDEX IF NOT EXISTS idx_messages_unique
                ON messages(packet_id, from_node, portnum);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insert a received message. Duplicate (packet_id, from, port)
    /// retransmissions are ignored. Returns true if a new row was written.</summary>
    public bool Add(MessageRecord m)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO messages
                (packet_id, from_node, to_node, channel, portnum, text,
                 payload_hex, decrypted, rx_epoch, rssi_dbfs, snr_db)
            VALUES ($pid, $from, $to, $chan, $port, $text,
                    $hex, $dec, $rx, $rssi, $snr);
            """;
        cmd.Parameters.AddWithValue("$pid",  m.PacketId);
        cmd.Parameters.AddWithValue("$from", m.FromNode);
        cmd.Parameters.AddWithValue("$to",   m.ToNode);
        cmd.Parameters.AddWithValue("$chan", m.Channel ?? string.Empty);
        cmd.Parameters.AddWithValue("$port", m.PortNum);
        cmd.Parameters.AddWithValue("$text", m.Text ?? string.Empty);
        cmd.Parameters.AddWithValue("$hex",  m.PayloadHex ?? string.Empty);
        cmd.Parameters.AddWithValue("$dec",  m.Decrypted ? 1 : 0);
        cmd.Parameters.AddWithValue("$rx",   m.RxEpoch);
        cmd.Parameters.AddWithValue("$rssi", (object?)m.RssiDbfs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$snr",  (object?)m.SnrDb ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Most recent messages, newest first.</summary>
    public IReadOnlyList<MessageRecord> Recent(int limit = 500)
    {
        ThrowIfDisposed();
        var list = new List<MessageRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages ORDER BY rx_epoch DESC, id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(Read(rd));
        return list;
    }

    /// <summary>
    /// All decoded text messages (portnum 1), oldest first, for rebuilding the
    /// channel chat rooms and direct-message conversation tabs on startup.
    /// </summary>
    public IReadOnlyList<MessageRecord> TextHistory(int limit = 5000)
    {
        ThrowIfDisposed();
        var list = new List<MessageRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM (
                SELECT * FROM messages
                WHERE portnum = 1 AND decrypted = 1
                ORDER BY rx_epoch DESC, id DESC
                LIMIT $n
            ) ORDER BY rx_epoch ASC, id ASC;
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(Read(rd));
        return list;
    }

    /// <summary>Delete the broadcast text messages stored for one channel.</summary>
    public void ClearChannel(string channel)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM messages WHERE portnum = 1 AND channel = $c AND to_node = 4294967295";
        cmd.Parameters.AddWithValue("$c", channel ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete the direct messages exchanged with one peer node.</summary>
    public void ClearConversation(uint peerNode, uint myNode)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM messages
            WHERE portnum = 1
              AND ((from_node = $peer AND to_node = $me)
                OR (from_node = $me   AND to_node = $peer));
            """;
        cmd.Parameters.AddWithValue("$peer", peerNode);
        cmd.Parameters.AddWithValue("$me", myNode);
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Clear()
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages";
        cmd.ExecuteNonQuery();
    }

    private static MessageRecord Read(SqliteDataReader r)
    {
        float? NullableF(string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : (float)r.GetDouble(i);
        }
        return new MessageRecord
        {
            Id         = r.GetInt64(r.GetOrdinal("id")),
            PacketId   = (uint)r.GetInt64(r.GetOrdinal("packet_id")),
            FromNode   = (uint)r.GetInt64(r.GetOrdinal("from_node")),
            ToNode     = (uint)r.GetInt64(r.GetOrdinal("to_node")),
            Channel    = r.GetString(r.GetOrdinal("channel")),
            PortNum    = r.GetInt32(r.GetOrdinal("portnum")),
            Text       = r.GetString(r.GetOrdinal("text")),
            PayloadHex = r.GetString(r.GetOrdinal("payload_hex")),
            Decrypted  = r.GetInt64(r.GetOrdinal("decrypted")) != 0,
            RxEpoch    = r.GetInt64(r.GetOrdinal("rx_epoch")),
            RssiDbfs   = NullableF("rssi_dbfs"),
            SnrDb      = NullableF("snr_db"),
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MessageStore));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }
}
