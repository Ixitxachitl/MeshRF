// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Deleting selected history rows, as the history windows do. Clearing a whole
/// node's history was always possible; removing a handful of bad readings —
/// a GPS fix that jumped, a sensor spike — is what these cover.
/// </summary>
public class NodeStoreHistoryDeleteTests
{
    private static NodeStore NewStore() => new(":memory:");

    private static long AddLocation(NodeStore store, uint node, int minute) =>
        store.AddLocationHistory(node, new DateTime(2026, 8, 17, 12, minute, 0, DateTimeKind.Utc),
                                 37.5 + minute * 0.001, -122.0, 10);

    private static long AddTelemetry(NodeStore store, uint node, int minute, double battery) =>
        store.AddTelemetryHistory(new NodeTelemetryHistoryRecord(
            0, node, new DateTime(2026, 8, 17, 12, minute, 0, DateTimeKind.Utc),
            battery, null, null, null, null,
            null, null, null, null, null,
            null, null, null, null, null, null,
            null, null, null, null, null, null,
            $"D|{battery}|||||||||||||"));

    [Fact]
    public void DeletingSeveralPositions_LeavesTheRest()
    {
        using var store = NewStore();
        var a = AddLocation(store, 7, 1);
        var b = AddLocation(store, 7, 2);
        var c = AddLocation(store, 7, 3);

        store.DeleteLocationHistory(new[] { a, c });

        var left = store.LocationHistory(7);
        Assert.Equal(b, Assert.Single(left).Id);
    }

    [Fact]
    public void DeletingSeveralTelemetrySamples_LeavesTheRest()
    {
        using var store = NewStore();
        var a = AddTelemetry(store, 7, 1, 90);
        var b = AddTelemetry(store, 7, 2, 80);
        var c = AddTelemetry(store, 7, 3, 70);

        store.DeleteTelemetryHistory(new[] { a, b });

        var left = store.TelemetryHistory(7);
        Assert.Equal(c, Assert.Single(left).Id);
    }

    [Fact]
    public void DeletingTouchesOnlyTheNamedRows()
    {
        // Two nodes' histories share a table, so an id list must never take a
        // neighbour's rows with it.
        using var store = NewStore();
        var mine = AddLocation(store, 7, 1);
        AddLocation(store, 8, 1);

        store.DeleteLocationHistory(new[] { mine });

        Assert.Empty(store.LocationHistory(7));
        Assert.Single(store.LocationHistory(8));
    }

    [Fact]
    public void DeletingAnEmptySelectionIsANoOp()
    {
        using var store = NewStore();
        AddLocation(store, 7, 1);
        AddTelemetry(store, 7, 1, 90);

        store.DeleteLocationHistory(Array.Empty<long>());
        store.DeleteTelemetryHistory(Array.Empty<long>());

        Assert.Single(store.LocationHistory(7));
        Assert.Single(store.TelemetryHistory(7));
    }

    [Fact]
    public void DeletingAnUnknownIdIsHarmless()
    {
        // A stale selection — a row already gone — must not throw or take
        // anything else with it.
        using var store = NewStore();
        var a = AddLocation(store, 7, 1);

        store.DeleteLocationHistory(new[] { a, 999_999L });
        store.DeleteLocationHistory(new[] { a });

        Assert.Empty(store.LocationHistory(7));
    }
}
