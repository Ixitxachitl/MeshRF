// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The identity chain the app now enforces: private key → public key → node
/// number, each step a pure function of the one before. The node id is no
/// longer typed, so these are the properties that make it well-defined.
/// </summary>
public class DerivedNodeIdTests
{
    [Fact]
    public void A_Key_Pair_Always_Yields_The_Same_Node_Number()
    {
        var priv = Curve25519.GeneratePrivateKey();
        var pub = Curve25519.GetPublicKey(priv);

        Assert.True(PkiNodeNumber.TryFromPublicKey(pub, out var first));
        Assert.True(PkiNodeNumber.TryFromPublicKey(Curve25519.GetPublicKey(priv), out var again));

        Assert.Equal(first, again);
    }

    [Fact]
    public void The_Hex_And_Byte_Forms_Agree()
    {
        // The self record stores the key as hex and the view model keeps it as
        // base64; both have to derive the same number or the Nodes table would
        // report our own node as a key mismatch.
        var pub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());

        Assert.True(PkiNodeNumber.TryFromPublicKey(pub, out var fromBytes));
        Assert.True(PkiNodeNumber.TryFromHexPublicKey(Convert.ToHexString(pub), out var fromHex));

        Assert.Equal(fromBytes, fromHex);
    }

    [Fact]
    public void A_New_Key_Pair_Is_A_New_Identity()
    {
        // Generating keys is now the only way to change the node id, so the two
        // had better not coincide.
        var a = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        var b = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());

        Assert.True(PkiNodeNumber.TryFromPublicKey(a, out var first));
        Assert.True(PkiNodeNumber.TryFromPublicKey(b, out var second));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_Derived_Number_Satisfies_The_Node_Tables_Own_Check()
    {
        // HasDerivedNodeNumMatch is what draws the green dot; a node id derived
        // this way has to pass it, or the app would flag itself.
        var pub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        Assert.True(PkiNodeNumber.TryFromPublicKey(pub, out var derived));

        var record = new NodeRecord { NodeNum = derived, PublicKey = Convert.ToHexString(pub) };

        Assert.True(record.HasDerivedNodeNumMatch);
    }

    [Fact]
    public void A_Hand_Picked_Number_Does_Not()
    {
        // The state that was reachable by typing an id in, and is what this
        // change exists to make unreachable.
        var pub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        Assert.True(PkiNodeNumber.TryFromPublicKey(pub, out var derived));

        var record = new NodeRecord { NodeNum = derived ^ 1u, PublicKey = Convert.ToHexString(pub) };

        Assert.False(record.HasDerivedNodeNumMatch);
    }

    [Fact]
    public void Importing_A_Private_Key_Carries_The_Whole_Identity_With_It()
    {
        // Pasting a private key is still allowed, and is now the only way to
        // land on a chosen node id: the public key is re-derived from it and
        // the number from that, so importing a node's key adopts its identity
        // whole rather than half of it.
        var theirPrivate = Curve25519.GeneratePrivateKey();
        var theirPublic = Curve25519.GetPublicKey(theirPrivate);
        Assert.True(PkiNodeNumber.TryFromPublicKey(theirPublic, out var theirNodeNum));

        // What the app does on a paste: re-derive the public key from the
        // private one, then the number from that.
        Assert.True(Curve25519.TryGetPublicKeyBase64(Convert.ToBase64String(theirPrivate), out var importedPublic));
        Assert.True(PkiNodeNumber.TryFromPublicKey(Convert.FromBase64String(importedPublic), out var importedNodeNum));

        Assert.Equal(Convert.ToBase64String(theirPublic), importedPublic);
        Assert.Equal(theirNodeNum, importedNodeNum);
    }

    [Fact]
    public void A_Key_That_Is_Not_Thirty_Two_Bytes_Derives_Nothing()
    {
        // Which is what leaves the node number alone while a key field is
        // mid-edit, rather than moving the identity to something unusable.
        Assert.False(PkiNodeNumber.TryFromPublicKey(new byte[31], out _));
        Assert.False(PkiNodeNumber.TryFromPublicKey([], out _));
        Assert.False(PkiNodeNumber.TryFromHexPublicKey("", out _));
        Assert.False(PkiNodeNumber.TryFromHexPublicKey("not hex", out _));
    }
}
