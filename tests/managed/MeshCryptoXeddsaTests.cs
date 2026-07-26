// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class MeshCryptoXeddsaTests
{
    [Fact]
    public void SignThenVerify_RoundTrips()
    {
        var curvePriv = Curve25519.GeneratePrivateKey();
        var curvePub = Curve25519.GetPublicKey(curvePriv);
        var (edPriv, edPub) = MeshCrypto.DeriveXeddsaKeys(curvePriv);

        var payload = System.Text.Encoding.UTF8.GetBytes("hello mesh");
        var sig = MeshCrypto.XeddsaSign(fromNode: 0x12345678, packetId: 42, portnum: 1, payload, edPriv, edPub);

        Assert.Equal(64, sig.Length);
        Assert.True(MeshCrypto.XeddsaVerify(0x12345678, 42, 1, payload, sig, curvePub));
    }

    [Theory]
    [InlineData(true, false, false, false)]  // wrong fromNode
    [InlineData(false, true, false, false)]  // wrong packetId
    [InlineData(false, false, true, false)]  // wrong portnum
    [InlineData(false, false, false, true)]  // wrong payload
    public void Verify_FailsWhenBoundMetadataDiffers(bool tamperFrom, bool tamperPacketId, bool tamperPort, bool tamperPayload)
    {
        var curvePriv = Curve25519.GeneratePrivateKey();
        var curvePub = Curve25519.GetPublicKey(curvePriv);
        var (edPriv, edPub) = MeshCrypto.DeriveXeddsaKeys(curvePriv);

        uint from = 0x12345678, packetId = 42, port = 1;
        var payload = System.Text.Encoding.UTF8.GetBytes("hello mesh");
        var sig = MeshCrypto.XeddsaSign(from, packetId, port, payload, edPriv, edPub);

        uint checkFrom = tamperFrom ? from + 1 : from;
        uint checkPacketId = tamperPacketId ? packetId + 1 : packetId;
        uint checkPort = tamperPort ? port + 1 : port;
        var checkPayload = tamperPayload ? System.Text.Encoding.UTF8.GetBytes("hello MESH") : payload;

        Assert.False(MeshCrypto.XeddsaVerify(checkFrom, checkPacketId, checkPort, checkPayload, sig, curvePub));
    }

    [Fact]
    public void Verify_FailsForWrongSenderKey()
    {
        var (edPriv, edPub) = MeshCrypto.DeriveXeddsaKeys(Curve25519.GeneratePrivateKey());
        var otherCurvePub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        var payload = System.Text.Encoding.UTF8.GetBytes("hello mesh");
        var sig = MeshCrypto.XeddsaSign(1, 2, 3, payload, edPriv, edPub);

        Assert.False(MeshCrypto.XeddsaVerify(1, 2, 3, payload, sig, otherCurvePub));
    }

    [Fact]
    public void Verify_FailsForMissingOrMalformedSignature()
    {
        var curvePub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        var payload = System.Text.Encoding.UTF8.GetBytes("hello mesh");

        Assert.False(MeshCrypto.XeddsaVerify(1, 2, 3, payload, null, curvePub));
        Assert.False(MeshCrypto.XeddsaVerify(1, 2, 3, payload, Array.Empty<byte>(), curvePub));
        Assert.False(MeshCrypto.XeddsaVerify(1, 2, 3, payload, new byte[10], curvePub));
    }
}
