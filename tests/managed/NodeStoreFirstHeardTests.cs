// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Nothing records when a node was first heard — the check that fires the
/// new_node trigger is a transient "does a row exist yet", not a timestamp — so
/// the answer is derived from the oldest history row we still hold. Those are
/// trimmed per node, so the derivation has to say when it is only a lower
/// bound instead of stating a date that is quietly wrong.
/// </summary>
public class NodeStoreFirstHeardTests
{
    private const uint Node = 0xa1b2c3d4;

    private static NodeStore NewStore() => new(":memory:");

    private static DateTime At(int day) => new(2026, 8, day, 12, 0, 0, DateTimeKind.Utc);

    // ----- the recorded sighting -----

    [Fact]
    public void ANewNodeRecordsWhenItWasFirstHeard()
    {
        using var store = NewStore();
        store.RecordSighting(Node, when: new DateTimeOffset(At(10)));

        Assert.Equal(new DateTimeOffset(At(10)).ToUnixTimeSeconds(),
                     store.Get(Node)!.FirstHeardEpoch);
    }

    [Fact]
    public void BeingHeardAgainDoesNotMoveIt()
    {
        // The whole point of the column: last-heard advances, first-heard does
        // not. If a later sighting could write it, it would just be a second
        // copy of last-heard.
        using var store = NewStore();
        store.RecordSighting(Node, when: new DateTimeOffset(At(10)));
        store.RecordSighting(Node, when: new DateTimeOffset(At(20)));

        var stored = store.Get(Node)!;
        Assert.Equal(new DateTimeOffset(At(10)).ToUnixTimeSeconds(), stored.FirstHeardEpoch);
        Assert.Equal(new DateTimeOffset(At(20)).ToUnixTimeSeconds(), stored.LastHeardEpoch);
    }

    [Fact]
    public void AnIdentityWriteDoesNotMoveItEither()
    {
        // A NodeInfo upsert carries names and keys and its own last-heard; it
        // must not be able to restate when the node was first seen.
        using var store = NewStore();
        store.RecordSighting(Node, when: new DateTimeOffset(At(10)));
        store.Upsert(new NodeRecord
        {
            NodeNum = Node,
            LongName = "Renamed",
            LastHeardEpoch = new DateTimeOffset(At(20)).ToUnixTimeSeconds(),
        });

        Assert.Equal(new DateTimeOffset(At(10)).ToUnixTimeSeconds(),
                     store.Get(Node)!.FirstHeardEpoch);
    }

    [Fact]
    public void ANodeFromBeforeTheColumnHasNoRecordedSighting()
    {
        // Rows created by a write carrying no timestamp read as unknown rather
        // than as the epoch, so the caller falls back to stored history.
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = Node, LongName = "Quiet" });

        Assert.Equal(0, store.Get(Node)!.FirstHeardEpoch);
    }

    // ----- the derived fallback, for nodes with no recorded sighting -----

    [Fact]
    public void ANodeWithNoHistoryHasNoFirstHeard()
    {
        using var store = NewStore();
        store.RecordSighting(Node);

        var (utc, capped) = store.FirstHeard(Node);

        // A sighting alone writes no history, so there is nothing to derive
        // from and the row is left out rather than guessed at.
        Assert.Null(utc);
        Assert.False(capped);
    }

    [Fact]
    public void TheOldestLocationRowIsTheAnswer()
    {
        using var store = NewStore();
        store.AddLocationHistory(Node, At(10), 1, 2, null);
        store.AddLocationHistory(Node, At(20), 3, 4, null);

        var (utc, capped) = store.FirstHeard(Node);

        Assert.Equal(At(10), utc);
        Assert.False(capped);
    }

    [Fact]
    public void TelemetryCountsTooAndTheEarlierOfTheTwoWins()
    {
        using var store = NewStore();
        store.AddLocationHistory(Node, At(20), 1, 2, null);
        store.AddTelemetryHistory(new NodeTelemetryHistoryRecord(
            Id: 0, NodeNum: Node, TimestampUtc: At(5), BatteryPct: 50,
            VoltageV: null, ChannelUtilPct: null, AirUtilTxPct: null, UptimeSeconds: null,
            TemperatureC: null, RelativeHumidityPct: null, BarometricPressureHpa: null,
            GasResistanceMohm: null, IaqValue: null,
            Pm10Standard: null, Pm25Standard: null, Pm100Standard: null,
            Pm10Environmental: null, Pm25Environmental: null, Pm100Environmental: null,
            Ch1VoltageV: null, Ch1CurrentMa: null, Ch2VoltageV: null, Ch2CurrentMa: null,
            Ch3VoltageV: null, Ch3CurrentMa: null, Signature: "sig"));

        var (utc, _) = store.FirstHeard(Node);

        Assert.Equal(At(5), utc);
    }

    [Fact]
    public void AnotherNodesHistoryIsNotCounted()
    {
        using var store = NewStore();
        store.AddLocationHistory(0x11111111, At(1), 1, 2, null);
        store.AddLocationHistory(Node, At(20), 3, 4, null);

        var (utc, _) = store.FirstHeard(Node);

        Assert.Equal(At(20), utc);
    }

    [Fact]
    public void AtTheTrimCapTheAnswerIsMarkedALowerBound()
    {
        using var store = NewStore();
        // One more than the cap: the oldest is deleted as the newest lands, so
        // the earliest surviving row is no longer the first one written.
        for (int i = 0; i <= NodeStore.HistoryRowsKeptPerNode; i++)
            store.AddLocationHistory(Node, At(1).AddMinutes(i), 1, 2, null);

        var (utc, capped) = store.FirstHeard(Node);

        Assert.True(capped, "a node sitting on the trim cap has lost older rows");
        // And the value really has walked forward off the first row written.
        Assert.NotEqual(At(1), utc);
    }

    [Fact]
    public void BelowTheCapTheAnswerIsExact()
    {
        using var store = NewStore();
        for (int i = 0; i < 10; i++)
            store.AddLocationHistory(Node, At(1).AddMinutes(i), 1, 2, null);

        var (utc, capped) = store.FirstHeard(Node);

        Assert.False(capped);
        Assert.Equal(At(1), utc);
    }
}
