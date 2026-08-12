// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The via-MQTT flag mirrors firmware's per-node <c>VIA_MQTT</c> bitfield, which
/// <c>NodeDB::updateFrom</c> overwrites from every packet. It is deliberately not
/// sticky: it answers "how did we last hear this node", not "have we ever heard it
/// over MQTT". MapReport.num_online_local_nodes depends on that distinction.
/// </summary>
public class NodeStoreViaMqttTests
{
    private static NodeStore NewStore() => new(":memory:");

    [Fact]
    public void NewNode_WithoutSighting_DefaultsToLocal()
    {
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = 1, LongName = "Node One" });

        Assert.False(store.Get(1)!.SeenViaMqtt);
    }

    [Fact]
    public void MqttSighting_SetsFlag()
    {
        using var store = NewStore();
        store.RecordSighting(1, seenViaMqtt: true);

        Assert.True(store.Get(1)!.SeenViaMqtt);
    }

    [Fact]
    public void LocalSighting_AfterMqtt_ClearsFlag()
    {
        using var store = NewStore();
        store.RecordSighting(1, seenViaMqtt: true);

        // Hearing the same node over the air must move it back to local, exactly
        // as firmware's nodeInfoLiteSetBit(..., mp.via_mqtt) does.
        store.RecordSighting(1, rssiDbm: -80, seenViaMqtt: false);

        Assert.False(store.Get(1)!.SeenViaMqtt);
    }

    [Fact]
    public void MqttSighting_AfterLocal_SetsFlag()
    {
        using var store = NewStore();
        store.RecordSighting(1, rssiDbm: -80, seenViaMqtt: false);
        store.RecordSighting(1, seenViaMqtt: true);

        Assert.True(store.Get(1)!.SeenViaMqtt);
    }

    [Fact]
    public void Upsert_WithNullSeenViaMqtt_LeavesFlagAlone()
    {
        using var store = NewStore();
        store.RecordSighting(1, seenViaMqtt: true);

        // A position-only or identity-only write carries no packet transport and
        // must not be read as "heard locally".
        store.Upsert(new NodeRecord { NodeNum = 1, Latitude = 45, Longitude = -93 });

        Assert.True(store.Get(1)!.SeenViaMqtt);
    }

    [Fact]
    public void Upsert_WithNullSeenViaMqtt_LeavesClearedFlagAlone()
    {
        using var store = NewStore();
        store.RecordSighting(1, seenViaMqtt: false);
        store.Upsert(new NodeRecord { NodeNum = 1, LongName = "Node One" });

        Assert.False(store.Get(1)!.SeenViaMqtt);
    }

    [Fact]
    public void FreshInsert_WithNullSeenViaMqtt_StoresLocalNotSentinel()
    {
        using var store = NewStore();
        // No prior row: the -1 "leave it alone" sentinel must be clamped to 0 on
        // the way in, not persisted as a truthy non-zero value.
        store.Upsert(new NodeRecord { NodeNum = 1, LongName = "Node One" });

        Assert.False(store.Get(1)!.SeenViaMqtt);
        Assert.False(store.All()[0].SeenViaMqtt);
    }
}
