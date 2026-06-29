// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

public class PkiNodeNumberTests
{
    [Fact]
    public void PublicKeyDerivesNodeNumberAndMatchesNodeRecord()
    {
        var privateKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var publicKey = Curve25519.GetPublicKey(privateKey);
        var publicKeyHex = Convert.ToHexString(publicKey);

        Assert.True(PkiNodeNumber.TryFromPublicKey(publicKey, out var nodeNum));
        Assert.True(PkiNodeNumber.TryFromHexPublicKey(publicKeyHex, out var nodeNumFromHex));
        Assert.Equal(nodeNum, nodeNumFromHex);

        var node = new NodeRecord
        {
            NodeNum = nodeNum,
            PublicKey = publicKeyHex,
        };

        Assert.True(node.HasDerivedNodeNumMatch);
    }

    [Fact]
    public void InvalidKeyDoesNotDeriveNodeNumber()
    {
        Assert.False(PkiNodeNumber.TryFromPublicKey([1, 2, 3], out _));
        Assert.False(PkiNodeNumber.TryFromHexPublicKey("001122", out _));
    }
}