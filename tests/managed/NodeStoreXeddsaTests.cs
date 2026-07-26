// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

public class NodeStoreXeddsaTests
{
    private static NodeStore NewStore() => new(":memory:");

    [Fact]
    public void SetXeddsaSigned_PersistsAndDoesNotAffectOtherFields()
    {
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = 1, LongName = "Node One" });

        store.SetXeddsaSigned(1, true);

        var node = store.Get(1);
        Assert.NotNull(node);
        Assert.True(node!.IsXeddsaVerified);
        Assert.Equal("Node One", node.LongName); // untouched
    }

    [Fact]
    public void UnverifiedNode_IsNotVerified()
    {
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = 1 });

        var node = store.Get(1);
        Assert.NotNull(node);
        Assert.False(node!.IsXeddsaVerified);
    }

    [Fact]
    public void Upsert_WithNullHasXeddsaSigned_DoesNotClearExistingFlag()
    {
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = 1 });
        store.SetXeddsaSigned(1, true);

        // A routine sighting/telemetry upsert (HasXeddsaSigned left null/default)
        // must not silently un-verify the node.
        store.Upsert(new NodeRecord { NodeNum = 1, RssiDbm = -80 });

        var node = store.Get(1);
        Assert.True(node!.IsXeddsaVerified);
    }

    [Fact]
    public void ClearPublicKey_ResetsVerifiedFlag()
    {
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = 1, PublicKey = new string('a', 64) });
        store.SetXeddsaSigned(1, true);

        store.ClearPublicKey(1);

        var node = store.Get(1);
        Assert.False(node!.IsXeddsaVerified);
    }
}
