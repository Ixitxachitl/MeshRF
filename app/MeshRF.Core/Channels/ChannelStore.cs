// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Data.Sqlite;

namespace MeshRF.Channels;

/// <summary>
/// SQLite-backed channel configuration store. Lives alongside the node DB at
/// <c>%APPDATA%\MeshRF\channels.db</c>.
/// </summary>
public sealed class ChannelStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public static string DefaultPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MeshRF");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "channels.db");
        }
    }

    public ChannelStore() : this(DefaultPath) { }

    public ChannelStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        EnsureSchema();
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
        if (c.Index < 0 || c.Index > 7)
            throw new ArgumentOutOfRangeException(nameof(c.Index), "channel index must be 0..7");
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
        cmd.Parameters.AddWithValue("$psk",  c.Psk);
        cmd.Parameters.AddWithValue("$role", (int)c.Role);
        cmd.Parameters.AddWithValue("$pp",   c.PositionPrecision);
        cmd.Parameters.AddWithValue("$up",   c.UplinkEnabled   ? 1 : 0);
        cmd.Parameters.AddWithValue("$dn",   c.DownlinkEnabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void Delete(int index)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM channels WHERE idx = $i";
        cmd.Parameters.AddWithValue("$i", index);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ChannelConfig> All()
    {
        ThrowIfDisposed();
        var list = new List<ChannelConfig>();
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
            list.Add(new ChannelConfig
            {
                Index             = rd.GetInt32(0),
                Name              = rd.GetString(1),
                Psk               = (byte[])rd.GetValue(2),
                Role              = (ChannelRole)rd.GetInt32(3),
                PositionPrecision = (byte)rd.GetInt32(4),
                UplinkEnabled     = rd.GetInt32(5) != 0,
                DownlinkEnabled   = rd.GetInt32(6) != 0,
            });
        }
        return list;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChannelStore));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }
}
