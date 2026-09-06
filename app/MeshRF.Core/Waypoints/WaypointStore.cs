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
    // Microsoft.Data.Sqlite does not guarantee a single SqliteConnection is
    // safe for concurrent commands from multiple threads, and this store's
    // single connection is shared across whatever threads call into it (e.g.
    // a background RX/decode thread alongside the UI thread) — so every
    // public method below takes this lock for its full SqliteCommand/
    // SqliteDataReader/transaction lifetime.
    private readonly object _gate = new();
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
        using (var cmd = _conn.CreateCommand())
        {
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
                CREATE INDEX IF NOT EXISTS idx_waypoints_rx
                    ON waypoints(rx_epoch DESC);
                """;
            cmd.ExecuteNonQuery();
        }

        EnsureWaypointIdIsUnique();

        // Geofence columns were added after the original schema; add them to
        // any pre-existing DB file rather than requiring a fresh one.
        EnsureColumn("geofence_radius", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("bbox_west", "REAL");
        EnsureColumn("bbox_south", "REAL");
        EnsureColumn("bbox_east", "REAL");
        EnsureColumn("bbox_north", "REAL");
        EnsureColumn("notify_on_enter", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("notify_on_exit", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("notify_favorites_only", "INTEGER NOT NULL DEFAULT 0");
        // Later still. A row written before this defaults to 0, which reads as
        // a broadcast — right for every marker that predates addressing being
        // recorded, since nothing else on those rows could say otherwise.
        EnsureColumn("to_node", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("preset", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("heard_on_preset", "TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>
    /// Makes <c>waypoint_id</c> alone the row key.
    /// </summary>
    /// <remarks>
    /// A waypoint's identity on the mesh is its id: <c>locked_to</c>, not the
    /// sender, is what says who may change it, so an unlocked marker may be
    /// edited or retired by anybody. Keying rows by sender and id together
    /// meant a retirement broadcast by a node other than the one that placed
    /// the marker landed on no row at all and was thrown away.
    /// </remarks>
    private void EnsureWaypointIdIsUnique()
    {
        using (var check = _conn.CreateCommand())
        {
            check.CommandText =
                "SELECT COUNT(*) FROM pragma_index_list('waypoints') WHERE name = 'idx_waypoints_waypoint_id'";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        using var cmd = _conn.CreateCommand();
        // The sender-scoped key let one id sit in the table once per sender.
        // Those rows are the same marker as different nodes last described it,
        // so the freshest is the one to keep.
        cmd.CommandText = """
            DELETE FROM waypoints WHERE id IN (
                SELECT id FROM (
                    SELECT id, ROW_NUMBER() OVER (
                        PARTITION BY waypoint_id ORDER BY rx_epoch DESC, id DESC) AS rn
                    FROM waypoints)
                WHERE rn > 1);
            DROP INDEX IF EXISTS idx_waypoints_sender_id;
            CREATE UNIQUE INDEX idx_waypoints_waypoint_id ON waypoints(waypoint_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private void EnsureColumn(string name, string columnDef)
    {
        using (var check = _conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('waypoints') WHERE name = $name";
            check.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }
        using var alter = _conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE waypoints ADD COLUMN {name} {columnDef}";
        alter.ExecuteNonQuery();
    }

    /// <summary>Insert a waypoint, or update the one already carrying its id,
    /// stamping <see cref="WaypointRecord.Id"/> with the row it landed on.</summary>
    /// <remarks>
    /// The row id is read back rather than discarded because it is the record's
    /// identity everywhere else: the list deletes and edits by it, and the map
    /// hit-tests by it. A record built from a packet has none until its row
    /// exists, and a whole session's worth sharing a default 0 makes every one
    /// of those match the first arrival instead of itself.
    ///
    /// <c>from_node</c> is the one column an update leaves alone: it records
    /// who put the marker on the mesh, and an edit or a retirement from
    /// somebody else does not change that.
    /// </remarks>
    public void Upsert(WaypointRecord rec)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO waypoints
                    (from_node, waypoint_id, packet_id, channel,
                     name, description, icon,
                     latitude, longitude, altitude_m,
                     expire_epoch, locked_to, to_node, rx_epoch,
                     geofence_radius, bbox_west, bbox_south, bbox_east, bbox_north,
                     notify_on_enter, notify_on_exit, notify_favorites_only, preset, heard_on_preset)
                VALUES
                    ($from, $wid, $pid, $chan,
                     $name, $desc, $icon,
                     $lat, $lon, $alt,
                     $exp, $lock, $to, $rx,
                     $geor, $bw, $bs, $be, $bn,
                     $nen, $nex, $nfav, $preset, $heard_on)
                ON CONFLICT(waypoint_id) DO UPDATE SET
                    packet_id             = excluded.packet_id,
                    channel               = excluded.channel,
                    name                  = excluded.name,
                    description           = excluded.description,
                    icon                  = excluded.icon,
                    latitude              = excluded.latitude,
                    longitude             = excluded.longitude,
                    altitude_m            = excluded.altitude_m,
                    expire_epoch          = excluded.expire_epoch,
                    locked_to             = excluded.locked_to,
                    to_node               = excluded.to_node,
                    rx_epoch              = MAX(excluded.rx_epoch, waypoints.rx_epoch),
                    geofence_radius       = excluded.geofence_radius,
                    bbox_west             = excluded.bbox_west,
                    bbox_south            = excluded.bbox_south,
                    bbox_east             = excluded.bbox_east,
                    bbox_north            = excluded.bbox_north,
                    notify_on_enter       = excluded.notify_on_enter,
                    notify_on_exit        = excluded.notify_on_exit,
                    notify_favorites_only = excluded.notify_favorites_only,
                    preset                = excluded.preset,
                    heard_on_preset       = COALESCE(NULLIF(excluded.heard_on_preset, ''), heard_on_preset)
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$from", rec.FromNode);
            cmd.Parameters.AddWithValue("$wid", rec.WaypointId);
            cmd.Parameters.AddWithValue("$pid", rec.PacketId);
            cmd.Parameters.AddWithValue("$chan", rec.Channel ?? string.Empty);
            cmd.Parameters.AddWithValue("$preset", rec.Preset ?? string.Empty);
            cmd.Parameters.AddWithValue("$heard_on", rec.HeardOnPreset ?? string.Empty);
            cmd.Parameters.AddWithValue("$name", rec.Name ?? string.Empty);
            cmd.Parameters.AddWithValue("$desc", rec.Description ?? string.Empty);
            cmd.Parameters.AddWithValue("$icon", (object?)rec.Icon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lat", rec.Latitude);
            cmd.Parameters.AddWithValue("$lon", rec.Longitude);
            cmd.Parameters.AddWithValue("$alt", (object?)rec.AltitudeM ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$exp", rec.ExpireEpoch);
            cmd.Parameters.AddWithValue("$lock", rec.LockedTo);
            cmd.Parameters.AddWithValue("$to", rec.ToNode);
            cmd.Parameters.AddWithValue("$rx", rec.RxEpoch);
            cmd.Parameters.AddWithValue("$geor", rec.GeofenceRadius);
            cmd.Parameters.AddWithValue("$bw", (object?)rec.BboxWest ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bs", (object?)rec.BboxSouth ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$be", (object?)rec.BboxEast ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bn", (object?)rec.BboxNorth ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nen", rec.NotifyOnEnter);
            cmd.Parameters.AddWithValue("$nex", rec.NotifyOnExit);
            cmd.Parameters.AddWithValue("$nfav", rec.NotifyFavoritesOnly);
            if (cmd.ExecuteScalar() is { } id) rec.Id = Convert.ToInt64(id);
        }
    }

    public IReadOnlyList<WaypointRecord> All()
    {
        ThrowIfDisposed();
        var list = new List<WaypointRecord>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM waypoints ORDER BY rx_epoch DESC, id DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(Read(rd));
        }
        return list;
    }

    public void Forget(long id)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM waypoints WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ForgetRange(IEnumerable<long> ids)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
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
            Preset      = r.GetString(r.GetOrdinal("preset")),
            HeardOnPreset = r.GetString(r.GetOrdinal("heard_on_preset")),
            Name        = r.GetString(r.GetOrdinal("name")),
            Description = r.GetString(r.GetOrdinal("description")),
            Icon        = Nullable<uint>("icon"),
            Latitude    = r.GetDouble(r.GetOrdinal("latitude")),
            Longitude   = r.GetDouble(r.GetOrdinal("longitude")),
            AltitudeM   = Nullable<int>("altitude_m"),
            ExpireEpoch = (uint)r.GetInt64(r.GetOrdinal("expire_epoch")),
            LockedTo    = (uint)r.GetInt64(r.GetOrdinal("locked_to")),
            ToNode      = (uint)r.GetInt64(r.GetOrdinal("to_node")),
            RxEpoch     = r.GetInt64(r.GetOrdinal("rx_epoch")),
            GeofenceRadius = (uint)r.GetInt64(r.GetOrdinal("geofence_radius")),
            BboxWest    = Nullable<double>("bbox_west"),
            BboxSouth   = Nullable<double>("bbox_south"),
            BboxEast    = Nullable<double>("bbox_east"),
            BboxNorth   = Nullable<double>("bbox_north"),
            NotifyOnEnter = r.GetInt64(r.GetOrdinal("notify_on_enter")) != 0,
            NotifyOnExit  = r.GetInt64(r.GetOrdinal("notify_on_exit")) != 0,
            NotifyFavoritesOnly = r.GetInt64(r.GetOrdinal("notify_favorites_only")) != 0,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _conn.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WaypointStore));
    }
}
