// SPDX-License-Identifier: GPL-3.0-or-later
using MeshtasticRF.Mesh;
using Xunit;

namespace MeshtasticRF.Tests;

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
}
