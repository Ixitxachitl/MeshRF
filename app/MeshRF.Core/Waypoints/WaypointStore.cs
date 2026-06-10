// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Data.Sqlite;
using MeshRF.Nodes;

namespace MeshRF.Waypoints;

/// <summary>
/// SQLite-backed waypoint store. Uses the same DB file as <see cref="NodeStore"/>
/// but keeps waypoint rows in a dedicated table.
/// </summary>
public sealed class WaypointStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public WaypointStore() : this(NodeStore.DefaultPath) { }

    public WaypointStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using (var wal = _conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            wal.ExecuteNonQuery();
        }
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS waypoints (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                from_node     INTEGER NOT NULL,
                waypoint_id   INTEGER NOT NULL,
                packet_id     INTEGER NOT NULL DEFAULT 0,
                channel       TEXT    NOT NULL DEFAULT '',
                name          TEXT    NOT NULL DEFAULT '',
                description   TEXT    NOT NULL DEFAULT '',
                icon          INTEGER,
                latitude      REAL    NOT NULL,
                longitude     REAL    NOT NULL,
                altitude_m    INTEGER,
                expire_epoch  INTEGER NOT NULL DEFAULT 0,
                locked_to     INTEGER NOT NULL DEFAULT 0,
                rx_epoch      INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_waypoints_sender_id
                ON waypoints(from_node, waypoint_id);
            CREATE INDEX IF NOT EXISTS idx_waypoints_rx
                ON waypoints(rx_epoch DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insert or update a waypoint from the same sender/id pair.</summary>
    public void Upsert(WaypointRecord rec)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO waypoints
                (from_node, waypoint_id, packet_id, channel,
                 name, description, icon,
                 latitude, longitude, altitude_m,
                 expire_epoch, locked_to, rx_epoch)
            VALUES
                ($from, $wid, $pid, $chan,
                 $name, $desc, $icon,
                 $lat, $lon, $alt,
                 $exp, $lock, $rx)
            ON CONFLICT(from_node, waypoint_id) DO UPDATE SET
                packet_id    = excluded.packet_id,
                channel      = excluded.channel,
                name         = excluded.name,
                description  = excluded.description,
                icon         = excluded.icon,
                latitude     = excluded.latitude,
                longitude    = excluded.longitude,
                altitude_m   = excluded.altitude_m,
                expire_epoch = excluded.expire_epoch,
                locked_to    = excluded.locked_to,
                rx_epoch     = MAX(excluded.rx_epoch, waypoints.rx_epoch);
            """;
        cmd.Parameters.AddWithValue("$from", rec.FromNode);
        cmd.Parameters.AddWithValue("$wid", rec.WaypointId);
        cmd.Parameters.AddWithValue("$pid", rec.PacketId);
        cmd.Parameters.AddWithValue("$chan", rec.Channel ?? string.Empty);
        cmd.Parameters.AddWithValue("$name", rec.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("$desc", rec.Description ?? string.Empty);
        cmd.Parameters.AddWithValue("$icon", (object?)rec.Icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lat", rec.Latitude);
        cmd.Parameters.AddWithValue("$lon", rec.Longitude);
        cmd.Parameters.AddWithValue("$alt", (object?)rec.AltitudeM ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exp", rec.ExpireEpoch);
        cmd.Parameters.AddWithValue("$lock", rec.LockedTo);
        cmd.Parameters.AddWithValue("$rx", rec.RxEpoch);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<WaypointRecord> All()
    {
        ThrowIfDisposed();
        var list = new List<WaypointRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM waypoints ORDER BY rx_epoch DESC, id DESC";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(Read(rd));
        return list;
    }

    public void Forget(long id)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM waypoints WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void ForgetRange(IEnumerable<long> ids)
    {
        ThrowIfDisposed();
        using var tx = _conn.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM waypoints WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static WaypointRecord Read(SqliteDataReader r)
    {
        T? Nullable<T>(string col) where T : struct
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : (T)Convert.ChangeType(r.GetValue(i), typeof(T));
        }

        return new WaypointRecord
        {
            Id          = r.GetInt64(r.GetOrdinal("id")),
            FromNode    = (uint)r.GetInt64(r.GetOrdinal("from_node")),
            WaypointId  = (uint)r.GetInt64(r.GetOrdinal("waypoint_id")),
            PacketId    = (uint)r.GetInt64(r.GetOrdinal("packet_id")),
            Channel     = r.GetString(r.GetOrdinal("channel")),
            Name        = r.GetString(r.GetOrdinal("name")),
            Description = r.GetString(r.GetOrdinal("description")),
            Icon        = Nullable<uint>("icon"),
            Latitude    = r.GetDouble(r.GetOrdinal("latitude")),
            Longitude   = r.GetDouble(r.GetOrdinal("longitude")),
            AltitudeM   = Nullable<int>("altitude_m"),
            ExpireEpoch = (uint)r.GetInt64(r.GetOrdinal("expire_epoch")),
            LockedTo    = (uint)r.GetInt64(r.GetOrdinal("locked_to")),
            RxEpoch     = r.GetInt64(r.GetOrdinal("rx_epoch")),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _conn.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WaypointStore));
    }
}
