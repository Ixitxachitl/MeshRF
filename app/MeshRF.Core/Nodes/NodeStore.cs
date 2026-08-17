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
    double? Pm10Standard,
    double? Pm25Standard,
    double? Pm100Standard,
    double? Pm10Environmental,
    double? Pm25Environmental,
    double? Pm100Environmental,
    double? Ch1VoltageV,
    double? Ch1CurrentMa,
    double? Ch2VoltageV,
    double? Ch2CurrentMa,
    double? Ch3VoltageV,
    double? Ch3CurrentMa,
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
    // Microsoft.Data.Sqlite does not guarantee a single SqliteConnection is
    // safe for concurrent commands from multiple threads, and this store's
    // single connection is shared across whatever threads call into it (e.g.
    // a background RX/decode thread alongside the UI thread) — so every
    // public method below takes this lock for its full SqliteCommand/
    // SqliteDataReader lifetime. Monitor (which `lock` uses) is reentrant on
    // the same thread, so methods that call other locking methods (e.g.
    // RecordSighting -> Upsert, AddLocationHistory -> TrimLocationHistory)
    // nest safely.
    private readonly object _gate = new();
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
                seen_via_mqtt    INTEGER NOT NULL DEFAULT 0,
                snr_db           REAL,
                rssi_dbm         REAL,
                hops_away        INTEGER,
                latitude         REAL,
                longitude        REAL,
                altitude_m       INTEGER,
                battery_pct      INTEGER,
                voltage_v        REAL,
                channel_util_pct REAL,
                air_util_tx_pct  REAL,
                node_status      TEXT    NOT NULL DEFAULT ''
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
        AddColumnIfMissing("has_xeddsa_signed", "INTEGER");
        AddColumnIfMissing("is_unmessagable", "INTEGER");
        AddColumnIfMissing("is_licensed", "INTEGER");
        AddColumnIfMissing("mute_rtttl", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("ignored", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("favorite", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("seen_via_mqtt", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("node_status", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("pm10_std",  "INTEGER");
        AddColumnIfMissing("pm25_std",  "INTEGER");
        AddColumnIfMissing("pm100_std", "INTEGER");
        AddColumnIfMissing("pm10_env",  "INTEGER");
        AddColumnIfMissing("pm25_env",  "INTEGER");
        AddColumnIfMissing("pm100_env", "INTEGER");
        AddColumnIfMissing("ch1_voltage_v",  "REAL");
        AddColumnIfMissing("ch1_current_ma", "REAL");
        AddColumnIfMissing("ch2_voltage_v",  "REAL");
        AddColumnIfMissing("ch2_current_ma", "REAL");
        AddColumnIfMissing("ch3_voltage_v",  "REAL");
        AddColumnIfMissing("ch3_current_ma", "REAL");

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
                pm10_std                   REAL,
                pm25_std                   REAL,
                pm100_std                  REAL,
                pm10_env                   REAL,
                pm25_env                   REAL,
                pm100_env                  REAL,
                signature                  TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_node_telemetry_history_node_time
                ON node_telemetry_history(node_num, timestamp_epoch ASC, id ASC);
            """;
        history.ExecuteNonQuery();

        // Additive migrations for air quality columns in node_telemetry_history.
        const string hist = "node_telemetry_history";
        AddColumnIfMissing("pm10_std",  "REAL", hist);
        AddColumnIfMissing("pm25_std",  "REAL", hist);
        AddColumnIfMissing("pm100_std", "REAL", hist);
        AddColumnIfMissing("pm10_env",  "REAL", hist);
        AddColumnIfMissing("pm25_env",  "REAL", hist);
        AddColumnIfMissing("pm100_env", "REAL", hist);
        AddColumnIfMissing("ch1_voltage_v",  "REAL", hist);
        AddColumnIfMissing("ch1_current_ma", "REAL", hist);
        AddColumnIfMissing("ch2_voltage_v",  "REAL", hist);
        AddColumnIfMissing("ch2_current_ma", "REAL", hist);
        AddColumnIfMissing("ch3_voltage_v",  "REAL", hist);
        AddColumnIfMissing("ch3_current_ma", "REAL", hist);
    }

    private void AddColumnIfMissing(string name, string sqlType, string table = "nodes")
    {
        using (var check = _conn.CreateCommand())
        {
            check.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $n";
            check.Parameters.AddWithValue("$n", name);
            if (check.ExecuteScalar() is not null) return;
        }
        using var alter = _conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {sqlType}";
        alter.ExecuteNonQuery();
    }

    /// <summary>Insert or merge a node record. Non-null fields overwrite.</summary>
    public void Upsert(NodeRecord rec)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO nodes (node_num, user_id, long_name, short_name,
                                   hw_model, role, last_heard_epoch, seen_via_mqtt,
                                   snr_db, rssi_dbm, hops_away,
                                   latitude, longitude, altitude_m,
                                   battery_pct, voltage_v,
                                   channel_util_pct, air_util_tx_pct,
                                   uptime_seconds, temperature_c,
                                       relative_humidity_pct, barometric_pressure_hpa,
                                       gas_resistance_mohm, iaq, public_key, key_mismatch,
                                       is_unmessagable, is_licensed, has_xeddsa_signed,
                                       mute_rtttl, ignored, node_status,
                                       pm10_std, pm25_std, pm100_std,
                                       pm10_env, pm25_env, pm100_env,
                                       ch1_voltage_v, ch1_current_ma,
                                       ch2_voltage_v, ch2_current_ma,
                                       ch3_voltage_v, ch3_current_ma)
                VALUES ($node_num, $user_id, $long_name, $short_name,
                        $hw_model, $role, $last_heard, MAX($seen_via_mqtt, 0),
                        $snr, $rssi, $hops,
                        $lat, $lon, $alt,
                        $batt, $volt,
                        $chan, $airx,
                        $uptime, $temp,
                        $hum, $pres,
                                    $gas, $iaq, $pubkey, $mismatch,
                                    $isunmessagable, $islicensed, $xeddsasigned,
                                    $mute_rtttl, $ignored, $node_status,
                                    $pm10std, $pm25std, $pm100std,
                                    $pm10env, $pm25env, $pm100env,
                                    $ch1v, $ch1i, $ch2v, $ch2i, $ch3v, $ch3i)
                ON CONFLICT(node_num) DO UPDATE SET
                    user_id          = COALESCE(NULLIF(excluded.user_id, ''),    user_id),
                    long_name        = COALESCE(NULLIF(excluded.long_name, ''),  long_name),
                    short_name       = COALESCE(NULLIF(excluded.short_name, ''), short_name),
                    hw_model         = COALESCE(NULLIF(excluded.hw_model, ''),   hw_model),
                    role             = COALESCE(NULLIF(excluded.role, ''),       role),
                    last_heard_epoch = MAX(excluded.last_heard_epoch, last_heard_epoch),
                    seen_via_mqtt    = COALESCE(NULLIF($seen_via_mqtt, -1), seen_via_mqtt),
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
                    key_mismatch     = COALESCE(excluded.key_mismatch, key_mismatch),
                    is_unmessagable  = COALESCE(excluded.is_unmessagable, is_unmessagable),
                    is_licensed      = COALESCE(excluded.is_licensed, is_licensed),
                    has_xeddsa_signed = COALESCE(excluded.has_xeddsa_signed, has_xeddsa_signed),
                    node_status      = COALESCE(NULLIF(excluded.node_status, ''), node_status),
                    pm10_std         = COALESCE(excluded.pm10_std,  pm10_std),
                    pm25_std         = COALESCE(excluded.pm25_std,  pm25_std),
                    pm100_std        = COALESCE(excluded.pm100_std, pm100_std),
                    pm10_env         = COALESCE(excluded.pm10_env,  pm10_env),
                    pm25_env         = COALESCE(excluded.pm25_env,  pm25_env),
                    pm100_env        = COALESCE(excluded.pm100_env, pm100_env),
                    ch1_voltage_v    = COALESCE(excluded.ch1_voltage_v,  ch1_voltage_v),
                    ch1_current_ma   = COALESCE(excluded.ch1_current_ma, ch1_current_ma),
                    ch2_voltage_v    = COALESCE(excluded.ch2_voltage_v,  ch2_voltage_v),
                    ch2_current_ma   = COALESCE(excluded.ch2_current_ma, ch2_current_ma),
                    ch3_voltage_v    = COALESCE(excluded.ch3_voltage_v,  ch3_voltage_v),
                    ch3_current_ma   = COALESCE(excluded.ch3_current_ma, ch3_current_ma);
                """;
            cmd.Parameters.AddWithValue("$node_num", rec.NodeNum);
            cmd.Parameters.AddWithValue("$user_id", rec.UserId ?? string.Empty);
            cmd.Parameters.AddWithValue("$long_name", rec.LongName ?? string.Empty);
            cmd.Parameters.AddWithValue("$short_name", rec.ShortName ?? string.Empty);
            cmd.Parameters.AddWithValue("$hw_model", rec.HwModel ?? string.Empty);
            cmd.Parameters.AddWithValue("$role", rec.Role ?? string.Empty);
            cmd.Parameters.AddWithValue("$last_heard", rec.LastHeardEpoch);
            // -1 is the "this write carries no packet, leave the flag alone" sentinel.
            // The column is NOT NULL, so the null case can't ride in as SQL NULL:
            // the VALUES clause clamps it to 0 for a fresh row and the DO UPDATE
            // maps it back to "keep existing" (see the NULLIF above).
            cmd.Parameters.AddWithValue("$seen_via_mqtt",
                rec.SeenViaMqtt is bool via ? (via ? 1 : 0) : -1);
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
            cmd.Parameters.AddWithValue("$isunmessagable",
                rec.IsUnmessagable is bool iu ? (iu ? 1 : 0) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$islicensed",
                rec.IsLicensed is bool il ? (il ? 1 : 0) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$xeddsasigned",
                rec.HasXeddsaSigned is bool xs ? (xs ? 1 : 0) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$mute_rtttl", rec.MuteRtttl ? 1 : 0);
            cmd.Parameters.AddWithValue("$ignored", rec.Ignored ? 1 : 0);
            cmd.Parameters.AddWithValue("$node_status", rec.NodeStatus ?? string.Empty);
            cmd.Parameters.AddWithValue("$pm10std",  (object?)rec.Pm10Standard       ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm25std",  (object?)rec.Pm25Standard       ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm100std", (object?)rec.Pm100Standard      ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm10env",  (object?)rec.Pm10Environmental  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm25env",  (object?)rec.Pm25Environmental  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm100env", (object?)rec.Pm100Environmental ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch1v", (object?)rec.Ch1VoltageV  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch1i", (object?)rec.Ch1CurrentMa ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch2v", (object?)rec.Ch2VoltageV  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch2i", (object?)rec.Ch2CurrentMa ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch3v", (object?)rec.Ch3VoltageV  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch3i", (object?)rec.Ch3CurrentMa ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Persist the UI's per-node RTTTL ignore flag without affecting any other fields.</summary>
    public void SetMuteRtttl(uint nodeNum, bool muted)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE nodes SET mute_rtttl = $mute WHERE node_num = $node_num";
            cmd.Parameters.AddWithValue("$node_num", nodeNum);
            cmd.Parameters.AddWithValue("$mute", muted ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Persist the UI's per-node ignore flag without affecting any other fields.</summary>
    public void SetIgnored(uint nodeNum, bool ignored)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE nodes SET ignored = $ignored WHERE node_num = $node_num";
            cmd.Parameters.AddWithValue("$node_num", nodeNum);
            cmd.Parameters.AddWithValue("$ignored", ignored ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Persist the UI's per-node favorite flag without affecting any other fields.</summary>
    public void SetFavorite(uint nodeNum, bool favorite)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE nodes SET favorite = $favorite WHERE node_num = $node_num";
            cmd.Parameters.AddWithValue("$node_num", nodeNum);
            cmd.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Record that we've verified an XEdDSA-signed broadcast from this
    /// node (mirrors firmware's <c>HAS_XEDDSA_SIGNED</c> signer bit), without
    /// affecting any other field. Monotonic: only ever called with true — the
    /// bit is reset only via <see cref="ClearPublicKey"/>, since trust is
    /// scoped to the key that was verified.</summary>
    public void SetXeddsaSigned(uint nodeNum, bool signed)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE nodes SET has_xeddsa_signed = $signed WHERE node_num = $node_num";
            cmd.Parameters.AddWithValue("$node_num", nodeNum);
            cmd.Parameters.AddWithValue("$signed", signed ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Touch last-heard / RSSI / SNR for an existing or new node.
    /// <paramref name="seenViaMqtt"/> is the transport of this sighting and
    /// overwrites the stored flag either way — callers always know it.</summary>
    public void RecordSighting(uint nodeNum, float? rssiDbm = null,
                               float? snrDb = null, byte? hopsAway = null,
                               DateTimeOffset? when = null,
                               bool seenViaMqtt = false)
    {
        var ts = (when ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        Upsert(new NodeRecord
        {
            NodeNum = nodeNum,
            LastHeardEpoch = ts,
            SeenViaMqtt = seenViaMqtt,
            RssiDbm = rssiDbm,
            SnrDb = snrDb,
            HopsAway = hopsAway,
        });
    }

    public NodeRecord? Get(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM nodes WHERE node_num = $n";
            cmd.Parameters.AddWithValue("$n", nodeNum);
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? Read(rd) : null;
        }
    }

    /// <summary>All nodes, newest-heard first.</summary>
    public IReadOnlyList<NodeRecord> All()
    {
        ThrowIfDisposed();
        var list = new List<NodeRecord>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM nodes ORDER BY node_num ASC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(Read(rd));
        }
        return list;
    }

    public int Count()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM nodes";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Forget(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM nodes WHERE node_num = $n;
                DELETE FROM node_location_history WHERE node_num = $n;
                DELETE FROM node_telemetry_history WHERE node_num = $n;
                """;
            cmd.Parameters.AddWithValue("$n", nodeNum);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<NodeLocationHistoryRecord> LocationHistory(uint nodeNum, int limit = 500)
    {
        ThrowIfDisposed();
        var rows = new List<NodeLocationHistoryRecord>();
        lock (_gate)
        {
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
        }
        rows.Reverse();
        return rows;
    }

    public int LocationHistoryCount(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM node_location_history WHERE node_num = $n";
            cmd.Parameters.AddWithValue("$n", nodeNum);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public IReadOnlyDictionary<uint, int> LocationHistoryCounts()
    {
        ThrowIfDisposed();
        var counts = new Dictionary<uint, int>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT node_num, COUNT(*) AS cnt FROM node_location_history GROUP BY node_num";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                counts[(uint)rd.GetInt64(0)] = rd.GetInt32(1);
        }
        return counts;
    }

    public long AddLocationHistory(uint nodeNum, DateTime timestampUtc,
                                   double latitude, double longitude, int? altitudeM)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
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
    }

    public void DeleteLocationHistory(long id)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM node_location_history WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes several rows in one transaction. A selection of a few
    /// hundred points is one commit rather than one per row.</summary>
    public void DeleteLocationHistory(IReadOnlyCollection<long> ids) =>
        DeleteHistoryRows("node_location_history", ids);

    /// <summary>Deletes several rows in one transaction, as above.</summary>
    public void DeleteTelemetryHistory(IReadOnlyCollection<long> ids) =>
        DeleteHistoryRows("node_telemetry_history", ids);

    /// <summary>
    /// Shared row-delete for the two history tables. The table name is a
    /// compile-time literal from the two callers above, never caller input, and
    /// the ids are bound as parameters.
    /// </summary>
    private void DeleteHistoryRows(string table, IReadOnlyCollection<long> ids)
    {
        ThrowIfDisposed();
        if (ids.Count == 0) return;
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE id = $id";
            var p = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var id in ids)
            {
                p.Value = id;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void ClearLocationHistory(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM node_location_history WHERE node_num = $n";
            cmd.Parameters.AddWithValue("$n", nodeNum);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Wipe the stored position (lat/lon/alt) from the node row itself.</summary>
    public void ClearNodeLocation(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE nodes
                SET latitude = NULL, longitude = NULL, altitude_m = NULL
                WHERE node_num = $n
                """;
            cmd.Parameters.AddWithValue("$n", nodeNum);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<NodeTelemetryHistoryRecord> TelemetryHistory(uint nodeNum, int limit = 500)
    {
        ThrowIfDisposed();
        var rows = new List<NodeTelemetryHistoryRecord>();
        lock (_gate)
        {
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
                    Nullable<double>(rd, "pm10_std"),
                    Nullable<double>(rd, "pm25_std"),
                    Nullable<double>(rd, "pm100_std"),
                    Nullable<double>(rd, "pm10_env"),
                    Nullable<double>(rd, "pm25_env"),
                    Nullable<double>(rd, "pm100_env"),
                    Nullable<double>(rd, "ch1_voltage_v"),
                    Nullable<double>(rd, "ch1_current_ma"),
                    Nullable<double>(rd, "ch2_voltage_v"),
                    Nullable<double>(rd, "ch2_current_ma"),
                    Nullable<double>(rd, "ch3_voltage_v"),
                    Nullable<double>(rd, "ch3_current_ma"),
                    ReadStringOrEmpty(rd, "signature")));
            }
        }
        rows.Reverse();
        return rows;
    }

    public string? LatestTelemetrySignature(uint nodeNum, string kindPrefix)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(kindPrefix))
            return null;

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature
                FROM node_telemetry_history
                WHERE node_num = $n
                  AND signature LIKE ($kind || '|%')
                ORDER BY timestamp_epoch DESC, id DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$n", nodeNum);
            cmd.Parameters.AddWithValue("$kind", kindPrefix);
            var scalar = cmd.ExecuteScalar();
            return scalar is string s && !string.IsNullOrWhiteSpace(s)
                ? s
                : null;
        }
    }

    public long AddTelemetryHistory(NodeTelemetryHistoryRecord rec)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO node_telemetry_history
                    (node_num, timestamp_epoch, battery_pct, voltage_v,
                     channel_util_pct, air_util_tx_pct, uptime_seconds,
                     temperature_c, relative_humidity_pct, barometric_pressure_hpa,
                     gas_resistance_mohm, iaq,
                     pm10_std, pm25_std, pm100_std, pm10_env, pm25_env, pm100_env,
                     ch1_voltage_v, ch1_current_ma, ch2_voltage_v, ch2_current_ma,
                     ch3_voltage_v, ch3_current_ma,
                     signature)
                VALUES ($node_num, $ts, $batt, $volt,
                        $chan, $airx, $uptime,
                        $temp, $hum, $pres,
                        $gas, $iaq,
                        $pm10std, $pm25std, $pm100std, $pm10env, $pm25env, $pm100env,
                        $ch1v, $ch1i, $ch2v, $ch2i, $ch3v, $ch3i,
                        $sig);
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
            cmd.Parameters.AddWithValue("$pm10std",  (object?)rec.Pm10Standard       ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm25std",  (object?)rec.Pm25Standard       ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm100std", (object?)rec.Pm100Standard      ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm10env",  (object?)rec.Pm10Environmental  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm25env",  (object?)rec.Pm25Environmental  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm100env", (object?)rec.Pm100Environmental ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch1v", (object?)rec.Ch1VoltageV  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch1i", (object?)rec.Ch1CurrentMa ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch2v", (object?)rec.Ch2VoltageV  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch2i", (object?)rec.Ch2CurrentMa ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch3v", (object?)rec.Ch3VoltageV  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch3i", (object?)rec.Ch3CurrentMa ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sig", rec.Signature ?? string.Empty);
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            TrimTelemetryHistory(rec.NodeNum, 500);
            return id;
        }
    }

    public void DeleteTelemetryHistory(long id)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM node_telemetry_history WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearTelemetryHistory(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM node_telemetry_history WHERE node_num = $n";
            cmd.Parameters.AddWithValue("$n", nodeNum);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Wipe all telemetry fields from the node row itself.</summary>
    public void ClearNodeTelemetry(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE nodes
                SET battery_pct = NULL, voltage_v = NULL,
                    channel_util_pct = NULL, air_util_tx_pct = NULL,
                    uptime_seconds = NULL,
                    temperature_c = NULL, relative_humidity_pct = NULL,
                    barometric_pressure_hpa = NULL, gas_resistance_mohm = NULL,
                    iaq = NULL,
                    pm10_std = NULL, pm25_std = NULL, pm100_std = NULL,
                    pm10_env = NULL, pm25_env = NULL, pm100_env = NULL,
                    ch1_voltage_v = NULL, ch1_current_ma = NULL,
                    ch2_voltage_v = NULL, ch2_current_ma = NULL,
                    ch3_voltage_v = NULL, ch3_current_ma = NULL
                WHERE node_num = $n
                """;
            cmd.Parameters.AddWithValue("$n", nodeNum);
            cmd.ExecuteNonQuery();
        }
    }

    // Callers already hold _gate (Monitor is reentrant on the same thread).
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
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "UPDATE nodes SET public_key = '', key_mismatch = 0, has_xeddsa_signed = 0 WHERE node_num = $n";
            cmd.Parameters.AddWithValue("$n", nodeNum);
            cmd.ExecuteNonQuery();
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM nodes;
                DELETE FROM node_location_history;
                DELETE FROM node_telemetry_history;
                """;
            cmd.ExecuteNonQuery();
        }
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
            SeenViaMqtt    = Nullable<bool>("seen_via_mqtt"),
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
            NodeStatus            = ReadStringOrEmpty(r, "node_status"),
            PublicKey             = ReadStringOrEmpty(r, "public_key"),
            KeyMismatch           = Nullable<bool>("key_mismatch"),
            IsUnmessagable        = Nullable<bool>("is_unmessagable"),
            IsLicensed            = Nullable<bool>("is_licensed"),
            HasXeddsaSigned       = Nullable<bool>("has_xeddsa_signed"),
            MuteRtttl             = Nullable<bool>("mute_rtttl") == true,
            Ignored               = Nullable<bool>("ignored") == true,
            Favorite              = Nullable<bool>("favorite") == true,
            Pm10Standard          = Nullable<uint>("pm10_std"),
            Pm25Standard          = Nullable<uint>("pm25_std"),
            Pm100Standard         = Nullable<uint>("pm100_std"),
            Pm10Environmental     = Nullable<uint>("pm10_env"),
            Pm25Environmental     = Nullable<uint>("pm25_env"),
            Pm100Environmental    = Nullable<uint>("pm100_env"),
            Ch1VoltageV           = Nullable<float>("ch1_voltage_v"),
            Ch1CurrentMa          = Nullable<float>("ch1_current_ma"),
            Ch2VoltageV           = Nullable<float>("ch2_voltage_v"),
            Ch2CurrentMa          = Nullable<float>("ch2_current_ma"),
            Ch3VoltageV           = Nullable<float>("ch3_voltage_v"),
            Ch3CurrentMa          = Nullable<float>("ch3_current_ma"),
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }
    }
}
