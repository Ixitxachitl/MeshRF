// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A NODE_STATUS_APP packet is the node's whole status, so it is stored as
/// received — including an empty one, which clears it — the way firmware's
/// <c>NodeDB::setNodeStatus</c> does. Every other write path leaves the status
/// alone, so a NodeInfo or telemetry packet cannot blank it.
/// </summary>
public class NodeStoreNodeStatusTests
{
    private static NodeStore NewStore() => new(":memory:");

    [Fact]
    public void SetNodeStatus_StoresStatus()
    {
        using var store = NewStore();
        store.RecordSighting(1);
        store.SetNodeStatus(1, "On the trail");

        Assert.Equal("On the trail", store.Get(1)!.NodeStatus);
    }

    [Fact]
    public void SetNodeStatus_ReplacesEarlierStatus()
    {
        using var store = NewStore();
        store.RecordSighting(1);
        store.SetNodeStatus(1, "On the trail");
        store.SetNodeStatus(1, "Back at camp");

        Assert.Equal("Back at camp", store.Get(1)!.NodeStatus);
    }

    [Fact]
    public void SetNodeStatus_Empty_ClearsStatus()
    {
        using var store = NewStore();
        store.RecordSighting(1);
        store.SetNodeStatus(1, "On the trail");
        store.SetNodeStatus(1, string.Empty);

        Assert.Equal(string.Empty, store.Get(1)!.NodeStatus);
    }

    [Fact]
    public void Upsert_WithEmptyStatus_LeavesStatusAlone()
    {
        using var store = NewStore();
        store.RecordSighting(1);
        store.SetNodeStatus(1, "On the trail");

        store.Upsert(new NodeRecord { NodeNum = 1, LongName = "Node One" });
        store.RecordSighting(1, rssiDbm: -80);

        Assert.Equal("On the trail", store.Get(1)!.NodeStatus);
    }
}
