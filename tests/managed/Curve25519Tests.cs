// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class Curve25519Tests
{
    private static byte[] FromHex(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    [Fact]
    public void DerivesPublicKey_Rfc7748Vector()
    {
        // RFC 7748 §6.1 test vector: Alice's private and resulting public key.
        var priv = FromHex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var expectedPub = FromHex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");

        var pub = Curve25519.GetPublicKey(priv);

        Assert.Equal(expectedPub, pub);
    }

    [Fact]
    public void GeneratedPrivateKey_IsClampedAnd32Bytes()
    {
        var priv = Curve25519.GeneratePrivateKey();
        Assert.Equal(32, priv.Length);
        Assert.Equal(0, priv[0] & 7);        // low 3 bits cleared
        Assert.Equal(0, priv[31] & 0x80);    // top bit cleared
        Assert.Equal(0x40, priv[31] & 0x40); // bit 254 set
    }

    [Fact]
    public void SharedSecret_Rfc7748Vector()
    {
        // RFC 7748 §6.1: Alice's private + Bob's public => the shared K.
        var alicePriv = FromHex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPub = FromHex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        var expectedK = FromHex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        var k = Curve25519.SharedSecret(alicePriv, bobPub);

        Assert.Equal(expectedK, k);
    }

    [Fact]
    public void SharedSecret_IsSymmetric()
    {
        var aPriv = Curve25519.GeneratePrivateKey();
        var bPriv = Curve25519.GeneratePrivateKey();
        var aPub = Curve25519.GetPublicKey(aPriv);
        var bPub = Curve25519.GetPublicKey(bPriv);

        // Both sides must derive the identical secret (Alice×Bob == Bob×Alice).
        Assert.Equal(Curve25519.SharedSecret(aPriv, bPub),
                     Curve25519.SharedSecret(bPriv, aPub));
    }

    [Fact]
    public void TryGetPublicKeyBase64_MatchesRfc7748Vector()
    {
        // Same vector as above, at the base64 layer the settings file stores.
        // Note this private key is not clamped as written (0x77 & 7 != 0), so
        // this also covers a key imported from another implementation: the
        // derivation clamps and still lands on the documented public key.
        var priv = Convert.ToBase64String(
            FromHex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"));
        var expected = Convert.ToBase64String(
            FromHex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"));

        Assert.True(Curve25519.TryGetPublicKeyBase64(priv, out var pub));
        Assert.Equal(expected, pub);
    }

    [Fact]
    public void TryGetPublicKeyBase64_AgreesWithTheByteLevelDerivation()
    {
        var priv = Curve25519.GeneratePrivateKey();
        var expected = Convert.ToBase64String(Curve25519.GetPublicKey(priv));

        Assert.True(Curve25519.TryGetPublicKeyBase64(Convert.ToBase64String(priv), out var pub));
        Assert.Equal(expected, pub);
    }

    [Fact]
    public void TryGetPublicKeyBase64_ToleratesSurroundingWhitespace()
    {
        var priv = Curve25519.GeneratePrivateKey();
        var b64 = Convert.ToBase64String(priv);

        Assert.True(Curve25519.TryGetPublicKeyBase64("  " + b64 + "\r\n", out var pub));
        Assert.Equal(Convert.ToBase64String(Curve25519.GetPublicKey(priv)), pub);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!")]
    public void TryGetPublicKeyBase64_RefusesUnusableInput(string? input)
    {
        // False rather than throwing, and no derived value: the caller's
        // contract is to leave an existing public key alone for a field that is
        // mid-edit rather than blank it.
        Assert.False(Curve25519.TryGetPublicKeyBase64(input, out var pub));
        Assert.Equal(string.Empty, pub);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(16)]
    public void TryGetPublicKeyBase64_RefusesWrongLengthKeys(int length)
    {
        // Valid base64, wrong size — a truncated paste. X25519 has exactly one
        // scalar width, so there is nothing to derive from this.
        var bytes = new byte[length];
        Assert.False(Curve25519.TryGetPublicKeyBase64(Convert.ToBase64String(bytes), out var pub));
        Assert.Equal(string.Empty, pub);
    }
}
