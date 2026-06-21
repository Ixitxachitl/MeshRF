// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Data.Sqlite;

namespace MeshRF.Nodes;

public sealed record NodeLocationHistoryRecord(
    long Id,
    uint NodeNum,
    DateTime TimestampUtc,
    double Latitude,
    double Longitude,
    int? AltitudeM);

public sealed record NodeTelemetryHistoryRecord(
    long Id,
    uint NodeNum,
    DateTime TimestampUtc,
    double? BatteryPct,
    double? VoltageV,
    double? ChannelUtilPct,
    double? AirUtilTxPct,
    double? UptimeSeconds,
    double? TemperatureC,
    double? RelativeHumidityPct,
    double? BarometricPressureHpa,
    double? GasResistanceMohm,
    double? IaqValue,
    string Signature);

/// <summary>
/// SQLite-backed persistent node database, modeled after the Meshtastic
/// firmware <c>NodeDB</c>. The schema mirrors the <c>NodeInfo</c> protobuf so
/// future MQTT / serial bridge implementations can populate it directly.
///
/// Database lives at <c>%APPDATA%\MeshRF\nodes.db</c>.
/// </summary>
public sealed class NodeStore : IDisposable
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
            return Path.Combine(dir, "nodes.db");
        }
    }

    public NodeStore() : this(DefaultPath) { }

    public NodeStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        // WAL mode: writes are sequential appends (no reader-writer conflicts);
        // synchronous=NORMAL is safe with WAL and avoids per-write fsync stalls.
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
            CREATE TABLE IF NOT EXISTS nodes (
                node_num         INTEGER PRIMARY KEY,
                user_id          TEXT    NOT NULL DEFAULT '',
                long_name        TEXT    NOT NULL DEFAULT '',
                short_name       TEXT    NOT NULL DEFAULT '',
                hw_model         TEXT    NOT NULL DEFAULT '',
                role             TEXT    NOT NULL DEFAULT '',
                last_heard_epoch INTEGER NOT NULL DEFAULT 0,
                snr_db           REAL,
                rssi_dbm         REAL,
                hops_away        INTEGER,
                latitude         REAL,
                longitude        REAL,
                altitude_m       INTEGER,
                battery_pct      INTEGER,
                voltage_v        REAL,
                channel_util_pct REAL,
                air_util_tx_pct  REAL
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_last_heard
                ON nodes(last_heard_epoch DESC);
            """;
        cmd.ExecuteNonQuery();

        // Additive migrations for telemetry columns introduced after the
        // original schema. ADD COLUMN is a no-op-safe way to upgrade existing
        // databases; ignore the error if the column already exists.
        AddColumnIfMissing("uptime_seconds", "INTEGER");
        AddColumnIfMissing("temperature_c", "REAL");
        AddColumnIfMissing("relative_humidity_pct", "REAL");
        AddColumnIfMissing("barometric_pressure_hpa", "REAL");
        AddColumnIfMissing("gas_resistance_mohm", "REAL");
        AddColumnIfMissing("iaq", "INTEGER");
        AddColumnIfMissing("public_key", "TEXT");
        AddColumnIfMissing("key_mismatch", "INTEGER");
        AddColumnIfMissing("mute_rtttl", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("ignored", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("favorite", "INTEGER NOT NULL DEFAULT 0");

        using var history = _conn.CreateCommand();
        history.CommandText = """
            CREATE TABLE IF NOT EXISTS node_location_history (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                node_num        INTEGER NOT NULL,
                timestamp_epoch INTEGER NOT NULL,
                latitude        REAL    NOT NULL,
                longitude       REAL    NOT NULL,
                altitude_m      INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_node_location_history_node_time
                ON node_location_history(node_num, timestamp_epoch ASC, id ASC);

            CREATE TABLE IF NOT EXISTS node_telemetry_history (
                id                         INTEGER PRIMARY KEY AUTOINCREMENT,
                node_num                   INTEGER NOT NULL,
                timestamp_epoch            INTEGER NOT NULL,
                battery_pct                REAL,
                voltage_v                  REAL,
                channel_util_pct           REAL,
                air_util_tx_pct            REAL,
                uptime_seconds             REAL,
                temperature_c              REAL,
                relative_humidity_pct      REAL,
                barometric_pressure_hpa    REAL,
                gas_resistance_mohm        REAL,
                iaq                        REAL,
                signature                  TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_node_telemetry_history_node_time
                ON node_telemetry_history(node_num, timestamp_epoch ASC, id ASC);
            """;
        history.ExecuteNonQuery();
    }

    private void AddColumnIfMissing(string name, string sqlType)
    {
        using (var check = _conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM pragma_table_info('nodes') WHERE name = $n";
            check.Parameters.AddWithValue("$n", name);
            if (check.ExecuteScalar() is not null) return;
        }
        using var alter = _conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE nodes ADD COLUMN {name} {sqlType}";
        alter.ExecuteNonQuery();
    }

    /// <summary>Insert or merge a node record. Non-null fields overwrite.</summary>
    public void Upsert(NodeRecord rec)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (node_num, user_id, long_name, short_name,
                               hw_model, role, last_heard_epoch,
                               snr_db, rssi_dbm, hops_away,
                               latitude, longitude, altitude_m,
                               battery_pct, voltage_v,
                               channel_util_pct, air_util_tx_pct,
                               uptime_seconds, temperature_c,
                                   relative_humidity_pct, barometric_pressure_hpa,
                                   gas_resistance_mohm, iaq, public_key, key_mismatch,
                                   mute_rtttl, ignored)
            VALUES ($node_num, $user_id, $long_name, $short_name,
                    $hw_model, $role, $last_heard,
                    $snr, $rssi, $hops,
                    $lat, $lon, $alt,
                    $batt, $volt,
                    $chan, $airx,
                    $uptime, $temp,
                    $hum, $pres,
                                $gas, $iaq, $pubkey, $mismatch,
                                $mute_rtttl, $ignored)
            ON CONFLICT(node_num) DO UPDATE SET
                user_id          = COALESCE(NULLIF(excluded.user_id, ''),    user_id),
                long_name        = COALESCE(NULLIF(excluded.long_name, ''),  long_name),
                short_name       = COALESCE(NULLIF(excluded.short_name, ''), short_name),
                hw_model         = COALESCE(NULLIF(excluded.hw_model, ''),   hw_model),
                role             = COALESCE(NULLIF(excluded.role, ''),       role),
                last_heard_epoch = MAX(excluded.last_heard_epoch, last_heard_epoch),
                snr_db           = COALESCE(excluded.snr_db, snr_db),
                rssi_dbm         = COALESCE(excluded.rssi_dbm, rssi_dbm),
                hops_away        = COALESCE(excluded.hops_away, hops_away),
                latitude         = COALESCE(excluded.latitude, latitude),
                longitude        = COALESCE(excluded.longitude, longitude),
                altitude_m       = COALESCE(excluded.altitude_m, altitude_m),
                battery_pct      = COALESCE(excluded.battery_pct, battery_pct),
                voltage_v        = COALESCE(excluded.voltage_v, voltage_v),
                channel_util_pct = COALESCE(excluded.channel_util_pct, channel_util_pct),
                air_util_tx_pct  = COALESCE(excluded.air_util_tx_pct,  air_util_tx_pct),
                uptime_seconds   = COALESCE(excluded.uptime_seconds, uptime_seconds),
                temperature_c    = COALESCE(excluded.temperature_c, temperature_c),
                relative_humidity_pct   = COALESCE(excluded.relative_humidity_pct, relative_humidity_pct),
                barometric_pressure_hpa = COALESCE(excluded.barometric_pressure_hpa, barometric_pressure_hpa),
                gas_resistance_mohm     = COALESCE(excluded.gas_resistance_mohm, gas_resistance_mohm),
                iaq              = COALESCE(excluded.iaq, iaq),
                public_key       = COALESCE(NULLIF(excluded.public_key, ''), public_key),
                key_mismatch     = COALESCE(excluded.key_mismatch, key_mismatch);
            """;
        cmd.Parameters.AddWithValue("$node_num", rec.NodeNum);
        cmd.Parameters.AddWithValue("$user_id", rec.UserId ?? string.Empty);
        cmd.Parameters.AddWithValue("$long_name", rec.LongName ?? string.Empty);
        cmd.Parameters.AddWithValue("$short_name", rec.ShortName ?? string.Empty);
        cmd.Parameters.AddWithValue("$hw_model", rec.HwModel ?? string.Empty);
        cmd.Parameters.AddWithValue("$role", rec.Role ?? string.Empty);
        cmd.Parameters.AddWithValue("$last_heard", rec.LastHeardEpoch);
        cmd.Parameters.AddWithValue("$snr",  (object?)rec.SnrDb       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rssi", (object?)rec.RssiDbm     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hops", (object?)rec.HopsAway    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lat",  (object?)rec.Latitude    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon",  (object?)rec.Longitude   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$alt",  (object?)rec.AltitudeM   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$batt", (object?)rec.BatteryPct  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$volt", (object?)rec.VoltageV    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$chan", (object?)rec.ChannelUtilPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$airx", (object?)rec.AirUtilTxPct   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uptime", (object?)rec.UptimeSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$temp", (object?)rec.TemperatureC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hum",  (object?)rec.RelativeHumidityPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pres", (object?)rec.BarometricPressureHpa ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gas",  (object?)rec.GasResistanceMohm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iaq",  (object?)rec.Iaq ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pubkey", rec.PublicKey ?? string.Empty);
        cmd.Parameters.AddWithValue("$mismatch",
            rec.KeyMismatch is bool km ? (km ? 1 : 0) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$mute_rtttl", rec.MuteRtttl ? 1 : 0);
        cmd.Parameters.AddWithValue("$ignored", rec.Ignored ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Persist the UI's per-node RTTTL ignore flag without affecting any other fields.</summary>
    public void SetMuteRtttl(uint nodeNum, bool muted)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET mute_rtttl = $mute WHERE node_num = $node_num";
        cmd.Parameters.AddWithValue("$node_num", nodeNum);
        cmd.Parameters.AddWithValue("$mute", muted ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Persist the UI's per-node ignore flag without affecting any other fields.</summary>
    public void SetIgnored(uint nodeNum, bool ignored)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET ignored = $ignored WHERE node_num = $node_num";
        cmd.Parameters.AddWithValue("$node_num", nodeNum);
        cmd.Parameters.AddWithValue("$ignored", ignored ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Persist the UI's per-node favorite flag without affecting any other fields.</summary>
    public void SetFavorite(uint nodeNum, bool favorite)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET favorite = $favorite WHERE node_num = $node_num";
        cmd.Parameters.AddWithValue("$node_num", nodeNum);
        cmd.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Touch last-heard / RSSI / SNR for an existing or new node.</summary>
    public void RecordSighting(uint nodeNum, float? rssiDbm = null,
                               float? snrDb = null, byte? hopsAway = null,
                               DateTimeOffset? when = null)
    {
        var ts = (when ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        Upsert(new NodeRecord
        {
            NodeNum = nodeNum,
            LastHeardEpoch = ts,
            RssiDbm = rssiDbm,
            SnrDb = snrDb,
            HopsAway = hopsAway,
        });
    }

    public NodeRecord? Get(uint nodeNum)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM nodes WHERE node_num = $n";
        cmd.Parameters.AddWithValue("$n", nodeNum);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? Read(rd) : null;
    }

    /// <summary>All nodes, newest-heard first.</summary>
    public IReadOnlyList<NodeRecord> All()
    {
        ThrowIfDisposed();
        var list = new List<NodeRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM nodes ORDER BY last_heard_epoch DESC, node_num ASC";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(Read(rd));
        return list;
    }

    public int Count()
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM nodes";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Forget(uint nodeNum)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM nodes WHERE node_num = $n;
            DELETE FROM node_location_history WHERE node_num = $n;
            DELETE FROM node_telemetry_history WHERE node_num = $n;
            """;
        cmd.Parameters.AddWithValue("$n", nodeNum);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<NodeLocationHistoryRecord> LocationHistory(uint nodeNum, int limit = 500)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, node_num, timestamp_epoch, latitude, longitude, altitude_m
            FROM node_location_history
            WHERE node_num = $n
            ORDER BY timestamp_epoch DESC, id DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$n", nodeNum);
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<NodeLocationHistoryRecord>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            rows.Add(new NodeLocationHistoryRecord(
                rd.GetInt64(rd.GetOrdinal("id")),
                (uint)rd.GetInt64(rd.GetOrdinal("node_num")),
                DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(rd.GetOrdinal("timestamp_epoch"))).UtcDateTime,
                rd.GetDouble(rd.GetOrdinal("latitude")),
                rd.GetDouble(rd.GetOrdinal("longitude")),
                Nullable<int>(rd, "altitude_m")));
        }
        rows.Reverse();
        return rows;
    }

    public long AddLocationHistory(uint nodeNum, DateTime timestampUtc,
                                   double latitude, double longitude, int? altitudeM)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO node_location_history
                (node_num, timestamp_epoch, latitude, longitude, altitude_m)
            VALUES ($node_num, $ts, $lat, $lon, $alt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$node_num", nodeNum);
        cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(timestampUtc).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$lat", latitude);
        cmd.Parameters.AddWithValue("$lon", longitude);
        cmd.Parameters.AddWithValue("$alt", (object?)altitudeM ?? DBNull.Value);
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        TrimLocationHistory(nodeNum, 500);
        return id;
    }

    public void DeleteLocationHistory(long id)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM node_location_history WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void ClearLocationHistory(uint nodeNum)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM node_location_history WHERE node_num = $n";
        cmd.Parameters.AddWithValue("$n", nodeNum);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<NodeTelemetryHistoryRecord> TelemetryHistory(uint nodeNum, int limit = 500)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM node_telemetry_history
            WHERE node_num = $n
            ORDER BY timestamp_epoch DESC, id DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$n", nodeNum);
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<NodeTelemetryHistoryRecord>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            rows.Add(new NodeTelemetryHistoryRecord(
                rd.GetInt64(rd.GetOrdinal("id")),
                (uint)rd.GetInt64(rd.GetOrdinal("node_num")),
                DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(rd.GetOrdinal("timestamp_epoch"))).UtcDateTime,
                Nullable<double>(rd, "battery_pct"),
                Nullable<double>(rd, "voltage_v"),
                Nullable<double>(rd, "channel_util_pct"),
                Nullable<double>(rd, "air_util_tx_pct"),
                Nullable<double>(rd, "uptime_seconds"),
                Nullable<double>(rd, "temperature_c"),
                Nullable<double>(rd, "relative_humidity_pct"),
                Nullable<double>(rd, "barometric_pressure_hpa"),
                Nullable<double>(rd, "gas_resistance_mohm"),
                Nullable<double>(rd, "iaq"),
                ReadStringOrEmpty(rd, "signature")));
        }
        rows.Reverse();
        return rows;
    }

    public long AddTelemetryHistory(NodeTelemetryHistoryRecord rec)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO node_telemetry_history
                (node_num, timestamp_epoch, battery_pct, voltage_v,
                 channel_util_pct, air_util_tx_pct, uptime_seconds,
                 temperature_c, relative_humidity_pct, barometric_pressure_hpa,
                 gas_resistance_mohm, iaq, signature)
            VALUES ($node_num, $ts, $batt, $volt,
                    $chan, $airx, $uptime,
                    $temp, $hum, $pres,
                    $gas, $iaq, $sig);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$node_num", rec.NodeNum);
        cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(rec.TimestampUtc).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$batt", (object?)rec.BatteryPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$volt", (object?)rec.VoltageV ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$chan", (object?)rec.ChannelUtilPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$airx", (object?)rec.AirUtilTxPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uptime", (object?)rec.UptimeSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$temp", (object?)rec.TemperatureC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hum", (object?)rec.RelativeHumidityPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pres", (object?)rec.BarometricPressureHpa ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gas", (object?)rec.GasResistanceMohm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iaq", (object?)rec.IaqValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sig", rec.Signature ?? string.Empty);
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        TrimTelemetryHistory(rec.NodeNum, 500);
        return id;
    }

    public void DeleteTelemetryHistory(long id)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM node_telemetry_history WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void ClearTelemetryHistory(uint nodeNum)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM node_telemetry_history WHERE node_num = $n";
        cmd.Parameters.AddWithValue("$n", nodeNum);
        cmd.ExecuteNonQuery();
    }

    private void TrimLocationHistory(uint nodeNum, int keep)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM node_location_history
            WHERE node_num = $n
              AND id NOT IN (
                  SELECT id FROM node_location_history
                  WHERE node_num = $n
                  ORDER BY timestamp_epoch DESC, id DESC
                  LIMIT $keep)
            """;
        cmd.Parameters.AddWithValue("$n", nodeNum);
        cmd.Parameters.AddWithValue("$keep", keep);
        cmd.ExecuteNonQuery();
    }

    private void TrimTelemetryHistory(uint nodeNum, int keep)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM node_telemetry_history
            WHERE node_num = $n
              AND id NOT IN (
                  SELECT id FROM node_telemetry_history
                  WHERE node_num = $n
                  ORDER BY timestamp_epoch DESC, id DESC
                  LIMIT $keep)
            """;
        cmd.Parameters.AddWithValue("$n", nodeNum);
        cmd.Parameters.AddWithValue("$keep", keep);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Forget a node's stored public key and clear any key-mismatch
    /// flag, so the next NodeInfo we hear is accepted as the new trusted key.
    /// Used by the UI's "Request new keys" action.</summary>
    public void ClearPublicKey(uint nodeNum)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "UPDATE nodes SET public_key = '', key_mismatch = 0 WHERE node_num = $n";
        cmd.Parameters.AddWithValue("$n", nodeNum);
        cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM nodes;
            DELETE FROM node_location_history;
            DELETE FROM node_telemetry_history;
            """;
        cmd.ExecuteNonQuery();
    }

    private static NodeRecord Read(SqliteDataReader r)
    {
        T? Nullable<T>(string col) where T : struct
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : (T)Convert.ChangeType(r.GetValue(i), typeof(T));
        }
        return new NodeRecord
        {
            NodeNum        = (uint)r.GetInt64(r.GetOrdinal("node_num")),
            UserId         = r.GetString(r.GetOrdinal("user_id")),
            LongName       = r.GetString(r.GetOrdinal("long_name")),
            ShortName      = r.GetString(r.GetOrdinal("short_name")),
            HwModel        = r.GetString(r.GetOrdinal("hw_model")),
            Role           = r.GetString(r.GetOrdinal("role")),
            LastHeardEpoch = r.GetInt64(r.GetOrdinal("last_heard_epoch")),
            SnrDb          = Nullable<float>("snr_db"),
            RssiDbm        = Nullable<float>("rssi_dbm"),
            HopsAway       = Nullable<byte>("hops_away"),
            Latitude       = Nullable<double>("latitude"),
            Longitude      = Nullable<double>("longitude"),
            AltitudeM      = Nullable<int>("altitude_m"),
            BatteryPct     = Nullable<byte>("battery_pct"),
            VoltageV       = Nullable<float>("voltage_v"),
            ChannelUtilPct = Nullable<float>("channel_util_pct"),
            AirUtilTxPct   = Nullable<float>("air_util_tx_pct"),
            UptimeSeconds  = Nullable<uint>("uptime_seconds"),
            TemperatureC          = Nullable<float>("temperature_c"),
            RelativeHumidityPct   = Nullable<float>("relative_humidity_pct"),
            BarometricPressureHpa = Nullable<float>("barometric_pressure_hpa"),
            GasResistanceMohm     = Nullable<float>("gas_resistance_mohm"),
            Iaq                   = Nullable<int>("iaq"),
            PublicKey             = ReadStringOrEmpty(r, "public_key"),
            KeyMismatch           = Nullable<bool>("key_mismatch"),
            MuteRtttl             = Nullable<bool>("mute_rtttl") == true,
            Ignored               = Nullable<bool>("ignored") == true,
            Favorite              = Nullable<bool>("favorite") == true,
        };
    }

    private static T? Nullable<T>(SqliteDataReader r, string col) where T : struct
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : (T)Convert.ChangeType(r.GetValue(i), typeof(T));
    }

    private static string ReadStringOrEmpty(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? string.Empty : r.GetString(i);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NodeStore));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }
}
