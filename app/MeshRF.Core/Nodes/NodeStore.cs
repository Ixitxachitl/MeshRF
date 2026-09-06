// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using MeshRF.Mesh;
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

/// <summary>One node number folded into another as a single radio that
/// renumbered itself, and the evidence that said so.</summary>
public sealed record NodeMerge(uint Survivor, uint Retired, NodeIdentityMatch Match);

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

    // Retired node number -> the row it was folded into. Held in memory because
    // every packet-driven write consults it: a neighbour that has not caught up
    // goes on relaying a renumbered node under its old number, and without this
    // each of those would insert the ghost row straight back. Merge keeps every
    // value pointing at a live row, so a lookup is never a chain.
    private readonly Dictionary<uint, uint> _aliases = new();

    public static string DefaultPath => AppData.PathFor("nodes.db");

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
        LoadAliases();
    }

    private void LoadAliases()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT alias_num, node_num FROM node_aliases";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) _aliases[(uint)rd.GetInt64(0)] = (uint)rd.GetInt64(1);
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
        AddColumnIfMissing("mac_address", "TEXT");
        // Written once, when the row is created, and never updated after --
        // see the Upsert below, which leaves it out of the DO UPDATE list.
        AddColumnIfMissing("first_heard_epoch", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("key_mismatch", "INTEGER");
        AddColumnIfMissing("has_xeddsa_signed", "INTEGER");
        AddColumnIfMissing("is_unmessagable", "INTEGER");
        AddColumnIfMissing("is_licensed", "INTEGER");
        AddColumnIfMissing("mute_rtttl", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("ignored", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("favorite", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("seen_via_mqtt", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("node_status", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("heard_on_preset", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("heard_on_freq_mhz", "REAL");
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

        // The best path a node has been heard over, and the geometry it was
        // heard at, so a move can invalidate it. Kept beside hops_away rather
        // than replacing it -- see MeshRF.Mesh.Directness.
        AddColumnIfMissing("best_hops",        "INTEGER");
        AddColumnIfMissing("best_hops_epoch",  "INTEGER");
        AddColumnIfMissing("best_hops_snr",    "REAL");
        AddColumnIfMissing("best_hops_rssi",   "REAL");
        AddColumnIfMissing("best_hops_my_lat", "REAL");
        AddColumnIfMissing("best_hops_my_lon", "REAL");
        AddColumnIfMissing("best_hops_pr_lat", "REAL");
        AddColumnIfMissing("best_hops_pr_lon", "REAL");

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

            -- Node numbers this database has retired, and the row each one was
            -- folded into. See MeshRF.Nodes.NodeIdentity for what makes two
            -- rows one radio; Merge keeps these pointing at a live node_num,
            -- so a lookup never has to follow a chain.
            CREATE TABLE IF NOT EXISTS node_aliases (
                alias_num    INTEGER PRIMARY KEY,
                node_num     INTEGER NOT NULL,
                merged_epoch INTEGER NOT NULL DEFAULT 0,
                reason       TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_node_aliases_node
                ON node_aliases(node_num);
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
                                       gas_resistance_mohm, iaq, public_key, mac_address, key_mismatch,
                                       first_heard_epoch,
                                       is_unmessagable, is_licensed, has_xeddsa_signed,
                                       mute_rtttl, ignored, node_status,
                                       pm10_std, pm25_std, pm100_std,
                                       pm10_env, pm25_env, pm100_env,
                                       ch1_voltage_v, ch1_current_ma,
                                       ch2_voltage_v, ch2_current_ma,
                                       ch3_voltage_v, ch3_current_ma,
                                       heard_on_preset, heard_on_freq_mhz)
                VALUES ($node_num, $user_id, $long_name, $short_name,
                        $hw_model, $role, $last_heard, MAX($seen_via_mqtt, 0),
                        $snr, $rssi, $hops,
                        $lat, $lon, $alt,
                        $batt, $volt,
                        $chan, $airx,
                        $uptime, $temp,
                        $hum, $pres,
                                    $gas, $iaq, $pubkey, $mac, $mismatch,
                                    $last_heard,
                                    $isunmessagable, $islicensed, $xeddsasigned,
                                    $mute_rtttl, $ignored, $node_status,
                                    $pm10std, $pm25std, $pm100std,
                                    $pm10env, $pm25env, $pm100env,
                                    $ch1v, $ch1i, $ch2v, $ch2i, $ch3v, $ch3i,
                                    $heard_on_preset, $heard_on_freq)
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
                    mac_address      = COALESCE(NULLIF(excluded.mac_address, ''), mac_address),
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
                    ch3_current_ma   = COALESCE(excluded.ch3_current_ma, ch3_current_ma),
                    heard_on_preset  = COALESCE(NULLIF(excluded.heard_on_preset, ''), heard_on_preset),
                    heard_on_freq_mhz = COALESCE(excluded.heard_on_freq_mhz, heard_on_freq_mhz);
                """;
            // A relayed packet can still carry a number this database has
            // retired; resolving here is what keeps the ghost row from
            // being inserted all over again.
            cmd.Parameters.AddWithValue("$node_num", TargetOf(rec));
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
            cmd.Parameters.AddWithValue("$mac", rec.MacAddress ?? string.Empty);
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
            cmd.Parameters.AddWithValue("$heard_on_preset", rec.HeardOnPreset ?? string.Empty);
            cmd.Parameters.AddWithValue("$heard_on_freq", (object?)rec.HeardOnFreqMHz ?? DBNull.Value);
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

    /// <summary>Store the status a NODE_STATUS_APP packet carried, verbatim.
    /// The packet is the node's whole status, so an empty one clears it —
    /// firmware's <c>NodeDB::setNodeStatus</c> overwrites the same way.
    /// <see cref="Upsert"/> cannot do this, since it reads an empty status as
    /// "unchanged" so that NodeInfo and telemetry writes leave it alone.</summary>
    public void SetNodeStatus(uint nodeNum, string status)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE nodes SET node_status = $status WHERE node_num = $node_num";
            cmd.Parameters.AddWithValue("$node_num", nodeNum);
            cmd.Parameters.AddWithValue("$status", status ?? string.Empty);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Touch last-heard / RSSI / SNR for an existing or new node.
    /// <paramref name="seenViaMqtt"/> is the transport of this sighting and
    /// overwrites the stored flag either way — callers always know it.</summary>
    /// <param name="heardOnPreset">What the node was heard on, a preset name
    /// or <see cref="Mesh.HeardOn.Custom"/>; null or empty leaves the stored
    /// value alone, for a sighting that did not come over the air.</param>
    public void RecordSighting(uint nodeNum, float? rssiDbm = null,
                               float? snrDb = null, byte? hopsAway = null,
                               DateTimeOffset? when = null,
                               bool seenViaMqtt = false,
                               string? heardOnPreset = null,
                               double? heardOnFreqMHz = null)
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
            HeardOnPreset = heardOnPreset ?? string.Empty,
            HeardOnFreqMHz = heardOnFreqMHz,
        });
    }

    /// <summary>
    /// Folds one hearing into the node's best-known path.
    /// </summary>
    /// <remarks>
    /// <para>Its own statement rather than part of <c>Upsert</c> on purpose.
    /// That one COALESCEs every column, so it can only ever fill a value in,
    /// and the case this exists for is clearing one: a node that has moved
    /// must lose its old path outright.</para>
    /// <para>Does nothing without both positions. Whether the old hearing
    /// still applies is a question about geometry, and a sighting that cannot
    /// answer it must not overwrite one that could.</para>
    /// </remarks>
    public void RecordDirectness(
        uint nodeNum, byte hopsAway, float? snrDb, float? rssiDbm,
        GeoPoint? mine, GeoPoint? theirs, DateTimeOffset? when = null)
    {
        ThrowIfDisposed();
        nodeNum = Resolve(nodeNum);
        if (mine is not { } myPos || theirs is not { } peerPos) return;

        var fresh = new DirectSighting(
            hopsAway, when ?? DateTimeOffset.UtcNow, snrDb, rssiDbm, myPos, peerPos);

        lock (_gate)
        {
            var stored = Get(nodeNum)?.BestPath;
            var keep = Directness.Reconcile(stored, fresh);

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE nodes SET
                    best_hops        = $hops,
                    best_hops_epoch  = $epoch,
                    best_hops_snr    = $snr,
                    best_hops_rssi   = $rssi,
                    best_hops_my_lat = $mylat,
                    best_hops_my_lon = $mylon,
                    best_hops_pr_lat = $prlat,
                    best_hops_pr_lon = $prlon
                WHERE node_num = $node_num
                """;
            cmd.Parameters.AddWithValue("$node_num", nodeNum);
            cmd.Parameters.AddWithValue("$hops", keep.HopsAway);
            cmd.Parameters.AddWithValue("$epoch", keep.When.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$snr", (object?)keep.SnrDb ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rssi", (object?)keep.RssiDbm ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mylat", keep.Mine.Lat);
            cmd.Parameters.AddWithValue("$mylon", keep.Mine.Lon);
            cmd.Parameters.AddWithValue("$prlat", keep.Theirs.Lat);
            cmd.Parameters.AddWithValue("$prlon", keep.Theirs.Lon);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>One node, by any number it has answered to: a number retired by
    /// a merge finds the row it was folded into.</summary>
    public NodeRecord? Get(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate) return ReadRow(Resolve(nodeNum));
    }

    // Caller holds _gate. The row exactly as stored, with no alias applied --
    // only Merge, which is about to retire one of these numbers, wants this.
    private NodeRecord? ReadRow(uint nodeNum)
    {
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

    /// <summary>
    /// The number a node is known by now. A node number that has been merged
    /// away resolves to the row it was folded into; anything else comes back
    /// unchanged. <see cref="Merge"/> repoints existing aliases as it goes, so
    /// this never has to follow a chain.
    /// </summary>
    public uint Resolve(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate) return _aliases.TryGetValue(nodeNum, out var found) ? found : nodeNum;
    }

    // Caller holds _gate. Which row an upsert should land on: the surviving one
    // for a retired number, unless what it carries says the number is no longer
    // the radio it was folded into.
    private uint TargetOf(NodeRecord rec)
    {
        uint target = Resolve(rec.NodeNum);
        if (target == rec.NodeNum || ReadRow(target) is not { } kept) return target;

        // A merge can be wrong — a MAC is only what a node claims, and two of
        // them claiming one is all it takes. A number answering with identity
        // that contradicts the row it was folded into gets released, so a bad
        // merge costs one wrong attribution rather than being permanent. What
        // already moved across stays moved; this only governs what comes next.
        if (!Contradicts(rec.MacAddress, kept.MacAddress)
            && !Contradicts(rec.PublicKey, kept.PublicKey))
            return target;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM node_aliases WHERE alias_num = $n";
        cmd.Parameters.AddWithValue("$n", rec.NodeNum);
        cmd.ExecuteNonQuery();
        _aliases.Remove(rec.NodeNum);
        return rec.NodeNum;
    }

    /// <summary>Two identity values that disagree. Either being unknown is not
    /// a disagreement — most writes carry neither.</summary>
    private static bool Contradicts(string? claimed, string? stored) =>
        !string.IsNullOrEmpty(claimed) && !string.IsNullOrEmpty(stored)
        && !string.Equals(claimed, stored, StringComparison.OrdinalIgnoreCase);

    /// <summary>Numbers this node used to answer to, oldest merge first.</summary>
    public IReadOnlyList<uint> AliasesOf(uint nodeNum)
    {
        ThrowIfDisposed();
        var list = new List<uint>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT alias_num FROM node_aliases
                WHERE node_num = $n
                ORDER BY merged_epoch ASC, alias_num ASC
                """;
            cmd.Parameters.AddWithValue("$n", nodeNum);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add((uint)rd.GetInt64(0));
        }
        return list;
    }

    /// <summary>
    /// Folds <paramref name="retired"/> into <paramref name="survivor"/> as one
    /// radio that changed its node number, and records the old number as an
    /// alias so stored history still resolves to the surviving row.
    /// </summary>
    /// <remarks>
    /// What carries over is deliberately narrow: the history rows, when the
    /// radio was first heard, the choices the user made about it, and any
    /// identity field the surviving row is missing. Position, telemetry, signal
    /// and path are left alone — they describe the last packet, and the
    /// surviving row is the identity currently on air, so a reading from a
    /// ghost that a neighbour is still relaying must not overwrite it.
    /// </remarks>
    /// <returns>False when there was nothing to fold, which is what stops the
    /// callers that merge until nothing matches from looping forever.</returns>
    public bool Merge(uint survivor, uint retired, NodeIdentityMatch match)
    {
        ThrowIfDisposed();
        if (survivor == retired) return false;
        lock (_gate)
        {
            // Read straight through, not via Get: the point is the row that is
            // about to stop existing, and Get would resolve past it.
            var old = ReadRow(retired);
            if (old is null || ReadRow(survivor) is null) return false;

            using var tx = _conn.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE node_location_history  SET node_num = $s WHERE node_num = $r;
                    UPDATE node_telemetry_history SET node_num = $s WHERE node_num = $r;

                    UPDATE nodes SET
                        user_id     = CASE WHEN user_id    = '' THEN $uid   ELSE user_id    END,
                        long_name   = CASE WHEN long_name  = '' THEN $long  ELSE long_name  END,
                        short_name  = CASE WHEN short_name = '' THEN $short ELSE short_name END,
                        hw_model    = CASE WHEN hw_model   = '' THEN $hw    ELSE hw_model   END,
                        role        = CASE WHEN role       = '' THEN $role  ELSE role       END,
                        public_key  = COALESCE(NULLIF(public_key,  ''), NULLIF($pubkey, '')),
                        mac_address = COALESCE(NULLIF(mac_address, ''), NULLIF($mac,    '')),
                        first_heard_epoch = CASE
                            WHEN $first > 0 AND (first_heard_epoch = 0 OR $first < first_heard_epoch)
                            THEN $first ELSE first_heard_epoch END,
                        favorite   = MAX(favorite,   $favorite),
                        ignored    = MAX(ignored,    $ignored),
                        mute_rtttl = MAX(mute_rtttl, $mute)
                    WHERE node_num = $s;

                    DELETE FROM nodes WHERE node_num = $r;

                    -- Anything already pointing at the number being retired
                    -- follows it, which is what keeps Resolve a single lookup.
                    UPDATE node_aliases SET node_num = $s WHERE node_num = $r;
                    -- The survivor is a live number again if it was ever retired.
                    DELETE FROM node_aliases WHERE alias_num = $s;
                    INSERT OR REPLACE INTO node_aliases (alias_num, node_num, merged_epoch, reason)
                    VALUES ($r, $s, $now, $reason);
                    """;
                cmd.Parameters.AddWithValue("$s", survivor);
                cmd.Parameters.AddWithValue("$r", retired);
                cmd.Parameters.AddWithValue("$uid", old.UserId);
                cmd.Parameters.AddWithValue("$long", old.LongName);
                cmd.Parameters.AddWithValue("$short", old.ShortName);
                cmd.Parameters.AddWithValue("$hw", old.HwModel);
                cmd.Parameters.AddWithValue("$role", old.Role);
                cmd.Parameters.AddWithValue("$pubkey", old.PublicKey);
                cmd.Parameters.AddWithValue("$mac", old.MacAddress);
                cmd.Parameters.AddWithValue("$first", old.FirstHeardEpoch);
                cmd.Parameters.AddWithValue("$favorite", old.Favorite ? 1 : 0);
                cmd.Parameters.AddWithValue("$ignored", old.Ignored ? 1 : 0);
                cmd.Parameters.AddWithValue("$mute", old.MuteRtttl ? 1 : 0);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$reason", match.ToString());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();

            // Same three moves the SQL above made, kept in step in memory.
            foreach (var alias in _aliases.Where(a => a.Value == retired).Select(a => a.Key).ToList())
                _aliases[alias] = survivor;
            _aliases.Remove(survivor);
            _aliases[retired] = survivor;

            // Two nodes' worth of history landed on one row, so the cap has to
            // be reapplied — otherwise the survivor keeps up to twice its share.
            TrimLocationHistory(survivor, HistoryRowsKeptPerNode);
            TrimTelemetryHistory(survivor, HistoryRowsKeptPerNode);
            return true;
        }
    }

    /// <summary>Every pair of rows the identity rules call one radio. Reports
    /// without changing anything.</summary>
    public IReadOnlyList<NodeMerge> FindDuplicates()
    {
        var all = All();
        var found = new List<NodeMerge>();
        // Every pair, which for a node database of this size is a few hundred
        // thousand string compares — cheap enough for the one startup pass, and
        // the live path uses MergeDuplicatesOf instead.
        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                var match = NodeIdentity.Compare(all[i], all[j]);
                if (match == NodeIdentityMatch.None) continue;
                var keep = NodeIdentity.Survivor(all[i], all[j]);
                var drop = ReferenceEquals(keep, all[i]) ? all[j] : all[i];
                found.Add(new NodeMerge(keep.NodeNum, drop.NodeNum, match));
            }
        }
        return found;
    }

    /// <summary>Applies every duplicate <see cref="FindDuplicates"/> can see.
    /// The one-shot pass over a database written before the MAC was stored.
    /// </summary>
    public IReadOnlyList<NodeMerge> MergeDuplicates()
    {
        ThrowIfDisposed();
        var applied = new List<NodeMerge>();
        lock (_gate)
        {
            // Re-found after each merge rather than applied as a batch: folding
            // one pair can fill a blank that makes another pair match, and a
            // stale list would name rows that no longer exist.
            while (FindDuplicates().FirstOrDefault() is { } merge
                   && Merge(merge.Survivor, merge.Retired, merge.Match))
                applied.Add(merge);
        }
        return applied;
    }

    /// <summary>Retires any duplicate of one node. Called when a NodeInfo
    /// arrives, which is where the MAC and key that can identify a renumbered
    /// radio come from.</summary>
    public IReadOnlyList<NodeMerge> MergeDuplicatesOf(uint nodeNum)
    {
        ThrowIfDisposed();
        var applied = new List<NodeMerge>();
        lock (_gate)
        {
            // Merging can expose another duplicate — three rows for one radio
            // that upgraded twice — so follow the survivor until nothing matches.
            while (Get(nodeNum) is { } node && FirstDuplicateOf(node) is { } merge
                   && Merge(merge.Survivor, merge.Retired, merge.Match))
            {
                applied.Add(merge);
                nodeNum = merge.Survivor;
            }
        }
        return applied;
    }

    // Caller holds _gate (Monitor is reentrant on the same thread).
    private NodeMerge? FirstDuplicateOf(NodeRecord node)
    {
        foreach (var other in All())
        {
            var match = NodeIdentity.Compare(node, other);
            if (match == NodeIdentityMatch.None) continue;
            var keep = NodeIdentity.Survivor(node, other);
            return ReferenceEquals(keep, node)
                ? new NodeMerge(node.NodeNum, other.NodeNum, match)
                : new NodeMerge(other.NodeNum, node.NodeNum, match);
        }
        return null;
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
        nodeNum = Resolve(nodeNum);
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
            TrimLocationHistory(nodeNum, HistoryRowsKeptPerNode);
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
        rec = rec with { NodeNum = Resolve(rec.NodeNum) };
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
            TrimTelemetryHistory(rec.NodeNum, HistoryRowsKeptPerNode);
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

    /// <summary>How many history rows each node keeps, per table. Named
    /// because <see cref="FirstHeard"/> has to know it: a node sitting exactly
    /// on the cap has had older rows deleted, so its earliest surviving row is
    /// a lower bound rather than the first time it was heard.</summary>
    public const int HistoryRowsKeptPerNode = 500;

    /// <summary>
    /// The earliest moment we still hold any record of this node, across both
    /// history tables, and whether that is only a lower bound.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored. Nothing records a first sighting -- the
    /// check that fires the new_node trigger is a transient "does a row exist
    /// yet", not a timestamp -- so the earliest history row is the closest
    /// thing we have. It is trimmed to <see cref="HistoryRowsKeptPerNode"/> per
    /// table, so a node that reports often loses its oldest rows and the answer
    /// walks forward over time. <c>Capped</c> says when that has happened, so
    /// the UI can show the value as "or earlier" rather than claim a date that
    /// is quietly wrong for exactly the nodes we know best.
    /// </remarks>
    public (DateTime? Utc, bool Capped) FirstHeard(uint nodeNum)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            long? earliest = null;
            bool capped = false;

            foreach (var table in new[] { "node_location_history", "node_telemetry_history" })
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText =
                    $"SELECT MIN(timestamp_epoch), COUNT(*) FROM {table} WHERE node_num = $n";
                cmd.Parameters.AddWithValue("$n", nodeNum);
                using var r = cmd.ExecuteReader();
                if (!r.Read() || r.IsDBNull(0)) continue;

                long min = r.GetInt64(0);
                if (earliest is null || min < earliest) earliest = min;
                if (r.GetInt64(1) >= HistoryRowsKeptPerNode) capped = true;
            }

            return earliest is long epoch
                ? (DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime, capped)
                : (null, false);
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
                DELETE FROM node_aliases;
                """;
            cmd.ExecuteNonQuery();
            _aliases.Clear();
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
            BestHops        = Nullable<byte>("best_hops"),
            BestHopsEpoch   = Nullable<long>("best_hops_epoch"),
            BestHopsSnrDb   = Nullable<float>("best_hops_snr"),
            BestHopsRssiDbm = Nullable<float>("best_hops_rssi"),
            BestHopsMyLat   = Nullable<double>("best_hops_my_lat"),
            BestHopsMyLon   = Nullable<double>("best_hops_my_lon"),
            BestHopsPeerLat = Nullable<double>("best_hops_pr_lat"),
            BestHopsPeerLon = Nullable<double>("best_hops_pr_lon"),
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
            FirstHeardEpoch       = r.GetInt64(r.GetOrdinal("first_heard_epoch")),
            MacAddress            = ReadStringOrEmpty(r, "mac_address"),
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
            HeardOnPreset         = ReadStringOrEmpty(r, "heard_on_preset"),
            HeardOnFreqMHz        = Nullable<double>("heard_on_freq_mhz"),
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
