// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The store contract the NodeInfo receive path leans on to keep a trusted key
/// when a node advertises a different one: an empty key means "leave what is on
/// file", a null flag means "leave the flag alone", and forgetting the key
/// clears the mismatch with it.
/// </summary>
public class NodeStoreKeyMismatchTests
{
    private const string KeyA = "aa";
    private const string KeyB = "bb";

    private static NodeStore NewStore() => new(":memory:");
    private static string Key(string pair) => string.Concat(Enumerable.Repeat(pair, 32));

    [Fact]
    public void Upsert_WithEmptyPublicKey_KeepsTheStoredKey()
    {
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = 1, PublicKey = Key(KeyA) });

        // What the receive path writes on a mismatch: the substituted key is
        // dropped and the node flagged instead.
        store.Upsert(new NodeRecord { NodeNum = 1, PublicKey = string.Empty, KeyMismatch = true });

        var node = store.Get(1);
        Assert.Equal(Key(KeyA), node!.PublicKey);
        Assert.True(node.HasKeyMismatch);
    }

    [Fact]
    public void Upsert_WithNullKeyMismatch_LeavesTheFlagAlone()
    {
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = 1, PublicKey = Key(KeyA) });
        store.Upsert(new NodeRecord { NodeNum = 1, PublicKey = string.Empty, KeyMismatch = true });

        // A routine sighting says nothing about keys and must not clear the flag.
        store.Upsert(new NodeRecord { NodeNum = 1, RssiDbm = -80 });

        Assert.True(store.Get(1)!.HasKeyMismatch);
    }

    [Fact]
    public void ClearPublicKey_ThenNewKey_IsAcceptedWithoutMismatch()
    {
        using var store = NewStore();
        store.Upsert(new NodeRecord { NodeNum = 1, PublicKey = Key(KeyA) });
        store.Upsert(new NodeRecord { NodeNum = 1, PublicKey = string.Empty, KeyMismatch = true });

        // "Request new keys": forget the old key, then hear a fresh one.
        store.ClearPublicKey(1);
        Assert.False(store.Get(1)!.HasKeyMismatch);

        store.Upsert(new NodeRecord { NodeNum = 1, PublicKey = Key(KeyB), KeyMismatch = false });

        var node = store.Get(1);
        Assert.Equal(Key(KeyB), node!.PublicKey);
        Assert.False(node.HasKeyMismatch);
    }
}
