// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Security;
using Microsoft.Data.Sqlite;

namespace MeshRF.Channels;

/// <summary>
/// SQLite-backed channel configuration store. Lives alongside the node DB at
/// <c>%APPDATA%\MeshRF\channels.db</c>.
/// </summary>
public sealed class ChannelStore : IDisposable
{
    // Microsoft.Data.Sqlite does not guarantee a single SqliteConnection is
    // safe for concurrent commands from multiple threads, and this store's
    // single connection is shared across whatever threads call into it (e.g.
    // a background RX/decode thread alongside the UI thread) — so every
    // public method below takes this lock for its full SqliteCommand/
    // SqliteDataReader lifetime.
    private readonly object _gate = new();
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public static string DefaultPath => AppData.PathFor("channels.db");

    public ChannelStore() : this(DefaultPath) { }

    public ChannelStore(string dbPath)
    {
        _secretKeyDir = Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".";
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using (var wal = _conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            wal.ExecuteNonQuery();
        }
        EnsureSchema();
        ProtectStoredKeys();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS channels (
                idx                 INTEGER PRIMARY KEY,
                name                TEXT    NOT NULL DEFAULT '',
                psk                 BLOB    NOT NULL,
                role                INTEGER NOT NULL DEFAULT 0,
                position_precision  INTEGER NOT NULL DEFAULT 13,
                uplink_enabled      INTEGER NOT NULL DEFAULT 0,
                downlink_enabled    INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Upsert(ChannelConfig c)
    {
        ThrowIfDisposed();
        if (c.Index < 0)
            throw new ArgumentOutOfRangeException(nameof(c.Index), "channel index must be non-negative");
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO channels (idx, name, psk, role, position_precision,
                                      uplink_enabled, downlink_enabled)
                VALUES ($idx, $name, $psk, $role, $pp, $up, $dn)
                ON CONFLICT(idx) DO UPDATE SET
                    name               = excluded.name,
                    psk                = excluded.psk,
                    role               = excluded.role,
                    position_precision = excluded.position_precision,
                    uplink_enabled     = excluded.uplink_enabled,
                    downlink_enabled   = excluded.downlink_enabled;
                """;
            cmd.Parameters.AddWithValue("$idx",  c.Index);
            cmd.Parameters.AddWithValue("$name", c.Name);
            cmd.Parameters.AddWithValue("$psk",  ProtectPsk(c.Psk));
            cmd.Parameters.AddWithValue("$role", (int)c.Role);
            cmd.Parameters.AddWithValue("$pp",   c.PositionPrecision);
            cmd.Parameters.AddWithValue("$up",   c.UplinkEnabled   ? 1 : 0);
            cmd.Parameters.AddWithValue("$dn",   c.DownlinkEnabled ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(int index)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM channels WHERE idx = $i";
            cmd.Parameters.AddWithValue("$i", index);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ChannelConfig> All()
    {
        ThrowIfDisposed();
        var list = new List<ChannelConfig>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT idx, name, psk, role, position_precision,
                       uplink_enabled, downlink_enabled
                  FROM channels
                 ORDER BY idx ASC
                """;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                bool recovered = TryUnprotectPsk((byte[])rd.GetValue(2), out var psk);
                list.Add(new ChannelConfig
                {
                    Index             = rd.GetInt32(0),
                    Name              = rd.GetString(1),
                    Psk               = psk,
                    // A key we cannot decrypt is a key we do not have. Disabled
                    // rather than empty: an empty PSK on a primary means "no
                    // encryption", so falling back to it would quietly put this
                    // channel's traffic on the air in the clear. Disabled
                    // matches nothing and carries nothing, and the channel
                    // dialog says so, which is the state to be in until the key
                    // is entered again.
                    Role              = recovered ? (ChannelRole)rd.GetInt32(3) : ChannelRole.Disabled,
                    PositionPrecision = (byte)rd.GetInt32(4),
                    UplinkEnabled     = rd.GetInt32(5) != 0,
                    DownlinkEnabled   = rd.GetInt32(6) != 0,
                });
            }
        }
        return list;
    }

    // Channel keys at rest. A PSK is what makes a channel private, so it gets
    // the same treatment as the private key and the MQTT password: DPAPI on
    // Windows, MachineBoundSecret elsewhere. The entropy scopes the blob to
    // this one kind of secret.
    private static readonly byte[] PskEntropy = Encoding.UTF8.GetBytes("MeshRF.ChannelPsk.v1");

    // The salt file lives beside the database it protects, so a store opened
    // on a test path does not reach into the user's real data directory.
    private readonly string _secretKeyDir;

    private byte[] ProtectPsk(byte[] psk) =>
        SecretProtection.ProtectBytes(psk, PskEntropy, _secretKeyDir);

    private bool TryUnprotectPsk(byte[] stored, out byte[] psk) =>
        SecretProtection.TryUnprotectBytes(stored, PskEntropy, _secretKeyDir, out psk);

    /// <summary>
    /// Re-writes any channel still holding a plaintext key, once, at startup.
    /// </summary>
    /// <remarks>
    /// Without this a key written before protection existed stays readable
    /// until someone happens to edit that channel — which for a channel that
    /// works is never.
    /// </remarks>
    private void ProtectStoredKeys()
    {
        List<(int Index, byte[] Psk)> plain = new();
        lock (_gate)
        {
            using var read = _conn.CreateCommand();
            read.CommandText = "SELECT idx, psk FROM channels";
            using var rd = read.ExecuteReader();
            while (rd.Read())
            {
                var stored = (byte[])rd.GetValue(1);
                if (stored.Length > 0 && !SecretProtection.IsProtected(stored))
                    plain.Add((rd.GetInt32(0), stored));
            }
        }

        if (plain.Count == 0) return;

        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (index, psk) in plain)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE channels SET psk = $psk WHERE idx = $idx";
                cmd.Parameters.AddWithValue("$psk", ProtectPsk(psk));
                cmd.Parameters.AddWithValue("$idx", index);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChannelStore));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }
    }
}
