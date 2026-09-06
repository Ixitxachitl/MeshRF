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
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS channels (
                    preset              TEXT    NOT NULL DEFAULT '',
                    idx                 INTEGER NOT NULL,
                    name                TEXT    NOT NULL DEFAULT '',
                    psk                 BLOB    NOT NULL,
                    role                INTEGER NOT NULL DEFAULT 0,
                    position_precision  INTEGER NOT NULL DEFAULT 13,
                    uplink_enabled      INTEGER NOT NULL DEFAULT 0,
                    downlink_enabled    INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (preset, idx)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // A database from before there was more than one list is keyed by
        // idx alone. SQLite cannot change a primary key in place, so the
        // table is rebuilt around the new key with every existing row in the
        // primary's list, which is the list they were.
        bool hasPreset;
        using (var check = _conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM pragma_table_info('channels') WHERE name = 'preset'";
            hasPreset = check.ExecuteScalar() is not null;
        }
        if (hasPreset) return;

        using var tx = _conn.BeginTransaction();
        using (var rebuild = _conn.CreateCommand())
        {
            rebuild.Transaction = tx;
            rebuild.CommandText = """
                CREATE TABLE channels_by_preset (
                    preset              TEXT    NOT NULL DEFAULT '',
                    idx                 INTEGER NOT NULL,
                    name                TEXT    NOT NULL DEFAULT '',
                    psk                 BLOB    NOT NULL,
                    role                INTEGER NOT NULL DEFAULT 0,
                    position_precision  INTEGER NOT NULL DEFAULT 13,
                    uplink_enabled      INTEGER NOT NULL DEFAULT 0,
                    downlink_enabled    INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (preset, idx)
                );
                INSERT INTO channels_by_preset (preset, idx, name, psk, role, position_precision,
                                                uplink_enabled, downlink_enabled)
                    SELECT '', idx, name, psk, role, position_precision,
                           uplink_enabled, downlink_enabled
                      FROM channels;
                DROP TABLE channels;
                ALTER TABLE channels_by_preset RENAME TO channels;
                """;
            rebuild.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Writes a channel into the list its <see cref="ChannelConfig.Preset"/>
    /// names, replacing the row at its index there.</summary>
    public void Upsert(ChannelConfig c)
    {
        ThrowIfDisposed();
        if (c.Index < 0)
            throw new ArgumentOutOfRangeException(nameof(c.Index), "channel index must be non-negative");
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO channels (preset, idx, name, psk, role, position_precision,
                                      uplink_enabled, downlink_enabled)
                VALUES ($preset, $idx, $name, $psk, $role, $pp, $up, $dn)
                ON CONFLICT(preset, idx) DO UPDATE SET
                    name               = excluded.name,
                    psk                = excluded.psk,
                    role               = excluded.role,
                    position_precision = excluded.position_precision,
                    uplink_enabled     = excluded.uplink_enabled,
                    downlink_enabled   = excluded.downlink_enabled;
                """;
            cmd.Parameters.AddWithValue("$preset", c.Preset ?? string.Empty);
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

    /// <summary>Removes the row at <paramref name="index"/> in the primary's
    /// list.</summary>
    public void Delete(int index) => Delete(string.Empty, index);

    /// <summary>Removes the row at <paramref name="index"/> in one list.</summary>
    public void Delete(string preset, int index)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM channels WHERE preset = $p AND idx = $i";
            cmd.Parameters.AddWithValue("$p", preset ?? string.Empty);
            cmd.Parameters.AddWithValue("$i", index);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every channel in every list, the primary's list first and
    /// each list in index order.</summary>
    public IReadOnlyList<ChannelConfig> All() => Query(null);

    /// <summary>One list: the primary's for an empty name.</summary>
    public IReadOnlyList<ChannelConfig> ForPreset(string preset) => Query(preset ?? string.Empty);

    /// <summary>The names of the lists that hold at least one channel, the
    /// primary's (empty) first.</summary>
    public IReadOnlyList<string> Presets()
    {
        ThrowIfDisposed();
        var list = new List<string>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT preset FROM channels ORDER BY preset = '' DESC, preset ASC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(rd.GetString(0));
        }
        return list;
    }

    private IReadOnlyList<ChannelConfig> Query(string? preset)
    {
        ThrowIfDisposed();
        var list = new List<ChannelConfig>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT idx, name, psk, role, position_precision,
                       uplink_enabled, downlink_enabled, preset
                  FROM channels
                 WHERE $p IS NULL OR preset = $p
                 ORDER BY preset = '' DESC, preset ASC, idx ASC
                """;
            cmd.Parameters.AddWithValue("$p", (object?)preset ?? DBNull.Value);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                bool recovered = TryUnprotectPsk((byte[])rd.GetValue(2), out var psk);
                list.Add(new ChannelConfig
                {
                    Preset            = rd.GetString(7),
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
        List<(string Preset, int Index, byte[] Psk)> plain = new();
        lock (_gate)
        {
            using var read = _conn.CreateCommand();
            read.CommandText = "SELECT idx, psk, preset FROM channels";
            using var rd = read.ExecuteReader();
            while (rd.Read())
            {
                var stored = (byte[])rd.GetValue(1);
                if (stored.Length > 0 && !SecretProtection.IsProtected(stored))
                    plain.Add((rd.GetString(2), rd.GetInt32(0), stored));
            }
        }

        if (plain.Count == 0) return;

        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (preset, index, psk) in plain)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE channels SET psk = $psk WHERE preset = $p AND idx = $idx";
                cmd.Parameters.AddWithValue("$psk", ProtectPsk(psk));
                cmd.Parameters.AddWithValue("$p", preset);
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
