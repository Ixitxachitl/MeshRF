// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Forgetting a node takes its position and telemetry history with it. The
/// three deletes are one command of three statements, which is the kind of
/// thing that half-runs without saying so — and history left behind is
/// invisible until the node comes back and the old points reappear under it.
/// </summary>
public class NodeStoreForgetTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public NodeStoreForgetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "nodes.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static NodeRecord Node(uint num, string longName) => new()
    {
        NodeNum = num,
        LongName = longName,
        ShortName = longName.Length > 0 ? longName[..1] : string.Empty,
    };

    private static void Populate(NodeStore store, uint num, string name)
    {
        store.Upsert(Node(num, name));
        store.AddLocationHistory(num, new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
                                 39.05, -121.07, altitudeM: 400);
        store.AddTelemetryHistory(new NodeTelemetryHistoryRecord(
            Id: 0, NodeNum: num, TimestampUtc: new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
            BatteryPct: 80, VoltageV: 4.0,
            ChannelUtilPct: null, AirUtilTxPct: null, UptimeSeconds: null,
            TemperatureC: null, RelativeHumidityPct: null, BarometricPressureHpa: null,
            GasResistanceMohm: null, IaqValue: null,
            Pm10Standard: null, Pm25Standard: null, Pm100Standard: null,
            Pm10Environmental: null, Pm25Environmental: null, Pm100Environmental: null,
            Ch1VoltageV: null, Ch1CurrentMa: null, Ch2VoltageV: null, Ch2CurrentMa: null,
            Ch3VoltageV: null, Ch3CurrentMa: null, Signature: $"sig-{num}"));
    }

    [Fact]
    public void ForgetTakesTheNodeAndBothHistories()
    {
        using var store = new NodeStore(_db);
        Populate(store, 0xcafebabe, "Ridge Repeater");

        store.Forget(0xcafebabe);

        Assert.Null(store.Get(0xcafebabe));
        Assert.Empty(store.LocationHistory(0xcafebabe));
        Assert.Empty(store.TelemetryHistory(0xcafebabe));
    }

    [Fact]
    public void ForgetLeavesEveryOtherNodeAlone()
    {
        using var store = new NodeStore(_db);
        Populate(store, 0xcafebabe, "Ridge Repeater");
        Populate(store, 0xdeadbeef, "Valley Base");

        store.Forget(0xcafebabe);

        Assert.NotNull(store.Get(0xdeadbeef));
        Assert.Single(store.LocationHistory(0xdeadbeef));
        Assert.Single(store.TelemetryHistory(0xdeadbeef));
        Assert.Equal(0xdeadbeef, Assert.Single(store.All()).NodeNum);
    }

    [Fact]
    public void ForgettingSomethingNotThereChangesNothing()
    {
        using var store = new NodeStore(_db);
        Populate(store, 0xcafebabe, "Ridge Repeater");

        store.Forget(0x00000001);

        Assert.NotNull(store.Get(0xcafebabe));
        Assert.Single(store.LocationHistory(0xcafebabe));
        Assert.Single(store.TelemetryHistory(0xcafebabe));
    }

    [Fact]
    public void AForgottenNodeComesBackWithNoHistory()
    {
        using var store = new NodeStore(_db);
        Populate(store, 0xcafebabe, "Ridge Repeater");
        store.Forget(0xcafebabe);

        // What happens on its next packet: the record returns, the history
        // does not. The confirmation says so, so it had better be true.
        store.Upsert(Node(0xcafebabe, "Ridge Repeater"));

        Assert.NotNull(store.Get(0xcafebabe));
        Assert.Empty(store.LocationHistory(0xcafebabe));
        Assert.Empty(store.TelemetryHistory(0xcafebabe));
    }
}
