// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshtasticRF.Mesh;
using Xunit;

namespace MeshtasticRF.Tests;

/// <summary>
/// Round-trip tests for PKC (public-key) direct messages: X25519 ECDH +
/// SHA-256 key derivation + AES-CCM sealing, mirroring the Meshtastic firmware
/// <c>CryptoEngine::encryptCurve25519</c> / <c>decryptCurve25519</c>.
/// </summary>
public class PkcCryptoTests
{
    [Fact]
    public void PkcEncryptDecrypt_RoundTrips()
    {
        var alicePriv = Curve25519.GeneratePrivateKey();
        var alicePub = Curve25519.GetPublicKey(alicePriv);
        var bobPriv = Curve25519.GeneratePrivateKey();
        var bobPub = Curve25519.GetPublicKey(bobPriv);

        uint from = 0x11223344;
        uint packetId = 0xDEADBEEF;
        var plain = Encoding.UTF8.GetBytes("hello over PKC");

        // Alice seals for Bob (her private + his public).
        var sealedBuf = MeshCrypto.PkcEncrypt(plain, alicePriv, bobPub, from, packetId);

        // Overhead is exactly tag(8) + extra nonce(4) on top of the plaintext.
        Assert.Equal(plain.Length + MeshCrypto.PkcOverhead, sealedBuf.Length);

        // Bob opens it (his private + her public, same from/id).
        var opened = MeshCrypto.PkcDecrypt(sealedBuf, bobPriv, alicePub, from, packetId);

        Assert.NotNull(opened);
        Assert.Equal(plain, opened);
    }

    [Fact]
    public void PkcDecrypt_WrongKey_ReturnsNull()
    {
        var alicePriv = Curve25519.GeneratePrivateKey();
        var bobPriv = Curve25519.GeneratePrivateKey();
        var bobPub = Curve25519.GetPublicKey(bobPriv);
        var eve = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());

        var sealedBuf = MeshCrypto.PkcEncrypt(
            Encoding.UTF8.GetBytes("secret"), alicePriv, bobPub, 1, 2);

        // Bob tries to open with the wrong sender public key — tag must fail.
        var opened = MeshCrypto.PkcDecrypt(sealedBuf, bobPriv, eve, 1, 2);

        Assert.Null(opened);
    }

    [Fact]
    public void PkcDecrypt_TamperedCiphertext_ReturnsNull()
    {
        var alicePriv = Curve25519.GeneratePrivateKey();
        var alicePub = Curve25519.GetPublicKey(alicePriv);
        var bobPriv = Curve25519.GeneratePrivateKey();
        var bobPub = Curve25519.GetPublicKey(bobPriv);

        var sealedBuf = MeshCrypto.PkcEncrypt(
            Encoding.UTF8.GetBytes("tamper me"), alicePriv, bobPub, 7, 9);
        sealedBuf[0] ^= 0xFF; // flip a ciphertext bit

        var opened = MeshCrypto.PkcDecrypt(sealedBuf, bobPriv, alicePub, 7, 9);

        Assert.Null(opened);
    }

    [Fact]
    public void EncodePkc_DecodePkc_RoundTripsTextMessage()
    {
        var alicePriv = Curve25519.GeneratePrivateKey();
        var alicePub = Curve25519.GetPublicKey(alicePriv);
        var bobPriv = Curve25519.GeneratePrivateKey();
        var bobPub = Curve25519.GetPublicKey(bobPriv);

        uint alice = 0xA11CE000;
        uint bob = 0xB0B00000;
        uint packetId = 0x0BADF00D;
        const string message = "PKC direct message round-trip 🛰";

        // Alice encodes a full on-air frame addressed to Bob.
        var frame = MeshEncoder.EncodePkcTextMessage(
            alice, bob, packetId, message, alicePriv, bobPub, hopLimit: 5);

        // The L1 header must carry channel hash 0x00 (PKC signal) and the
        // correct addressing for Bob to attempt a PKC decrypt.
        Assert.True(MeshHeader.TryParse(frame, out var header));
        Assert.Equal(0x00, header.ChannelHash);
        Assert.Equal(alice, header.From);
        Assert.Equal(bob, header.To);
        Assert.Equal(5, header.HopLimit);
        Assert.Equal(5, header.HopStart);
        Assert.False(header.IsBroadcast);

        // Bob decodes it with his private key and Alice's public key.
        var result = MeshDecoder.DecodePkc(frame, bobPriv, alicePub);

        Assert.NotNull(result);
        Assert.Equal(PortNum.TextMessage, result!.Port);
        Assert.Equal(message, result.Text);
    }

    [Fact]
    public void DecodePkc_WrongRecipient_ReturnsNull()
    {
        var alicePriv = Curve25519.GeneratePrivateKey();
        var alicePub = Curve25519.GetPublicKey(alicePriv);
        var bobPub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        var evePriv = Curve25519.GeneratePrivateKey();

        var frame = MeshEncoder.EncodePkcTextMessage(
            0xA11CE000, 0xB0B00000, 0x1234, "not for eve", alicePriv, bobPub);

        // Eve has the sender's public key but not the right private key.
        var result = MeshDecoder.DecodePkc(frame, evePriv, alicePub);

        Assert.Null(result);
    }

    [Fact]
    public void EncodePkcRouting_DecodesAsAck_WithRequestId()
    {
        var alicePriv = Curve25519.GeneratePrivateKey();
        var alicePub = Curve25519.GetPublicKey(alicePriv);
        var bobPriv = Curve25519.GeneratePrivateKey();
        var bobPub = Curve25519.GetPublicKey(bobPriv);

        uint bob = 0xB0B00000, alice = 0xA11CE000;
        uint origPacketId = 0xCAFEF00D;
        uint ackPacketId = 0x00C0FFEE;

        // Bob acks a packet (origPacketId) he received from Alice.
        var frame = MeshEncoder.EncodePkcRouting(
            bob, alice, ackPacketId, origPacketId, bobPriv, alicePub, errorReason: 0);

        Assert.True(MeshHeader.TryParse(frame, out var header));
        Assert.Equal(0x00, header.ChannelHash);
        Assert.Equal(bob, header.From);
        Assert.Equal(alice, header.To);

        // Alice opens the ack with her private key + Bob's public key.
        var result = MeshDecoder.DecodePkc(frame, alicePriv, bobPub);

        Assert.NotNull(result);
        Assert.Equal(PortNum.Routing, result!.Port);
        Assert.Equal(0, result.RoutingError);          // 0 = ACK
        Assert.Equal(origPacketId, result.RequestId);  // references the original
    }

    [Fact]
    public void EncodePkcRouting_NonZeroError_DecodesAsNak()
    {
        var alicePriv = Curve25519.GeneratePrivateKey();
        var alicePub = Curve25519.GetPublicKey(alicePriv);
        var bobPriv = Curve25519.GeneratePrivateKey();
        var bobPub = Curve25519.GetPublicKey(bobPriv);

        var frame = MeshEncoder.EncodePkcRouting(
            0xB0B00000, 0xA11CE000, 0x2222, 0x3333, bobPriv, alicePub, errorReason: 3);

        var result = MeshDecoder.DecodePkc(frame, alicePriv, bobPub);

        Assert.NotNull(result);
        Assert.Equal(PortNum.Routing, result!.Port);
        Assert.Equal(3, result.RoutingError);          // non-zero = NAK
        Assert.Equal(0x3333u, result.RequestId);
    }

    [Fact]
    public void EncodePkc_WantResponse_IsDecoded()
    {
        var alicePriv = Curve25519.GeneratePrivateKey();
        var alicePub = Curve25519.GetPublicKey(alicePriv);
        var bobPriv = Curve25519.GeneratePrivateKey();
        var bobPub = Curve25519.GetPublicKey(bobPriv);

        var frame = MeshEncoder.EncodePkc(
            0xA11CE000, 0xB0B00000, 0x4444, PortNum.NodeInfo,
            new byte[] { 0x01 }, alicePriv, bobPub, wantResponse: true);

        var result = MeshDecoder.DecodePkc(frame, bobPriv, alicePub);

        Assert.NotNull(result);
        Assert.True(result!.WantResponse);
    }
}
