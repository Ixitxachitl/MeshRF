// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class XEdDSATests
{
    private static byte[] FromHex(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    // RFC 8032 §7.1 Ed25519 TEST 1 (empty message). This exercises the same
    // point decode / scalar mult / point add / encode pipeline XEdDSA.Verify
    // uses, independent of how the signing key was derived (stock Ed25519
    // seed-based derivation here, XEdDSA's direct-scalar derivation elsewhere)
    // — Verify() doesn't care how the key was made, only that it's a valid
    // Ed25519 public key.
    [Fact]
    public void Verify_Rfc8032Vector1_EmptyMessage()
    {
        var pub = FromHex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig = FromHex(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901" +
            "555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a1" +
            "00b");

        bool ok = XEdDSA.Verify(pub, ReadOnlySpan<byte>.Empty, sig);

        Assert.True(ok, "If this fails while all other XEdDSA tests pass, " +
            "suspect a transcription error in the hardcoded RFC 8032 vector, " +
            "not the curve arithmetic — the self-consistency tests below " +
            "exercise the same code paths without relying on a memorized vector.");
    }

    [Fact]
    public void SignThenVerify_RoundTrips()
    {
        var curvePriv = Curve25519.GeneratePrivateKey();
        var (edPriv, edPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(curvePriv);

        var message = System.Text.Encoding.UTF8.GetBytes("MeshRF XEdDSA round trip");
        var sig = XEdDSA.Sign(edPriv, edPub, message, RandomBytes(32));

        Assert.True(XEdDSA.Verify(edPub, message, sig));
    }

    [Fact]
    public void DerivedPublicKey_AlwaysHasSignBitZero()
    {
        // priv_curve_to_ed_keys must always normalize to sign-bit 0, regardless
        // of which raw X25519 scalar produced it (about half will need the
        // negate-and-recompute branch).
        for (int i = 0; i < 25; i++)
        {
            var curvePriv = Curve25519.GeneratePrivateKey();
            var (_, edPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(curvePriv);
            Assert.Equal(0, edPub[31] & 0x80);
        }
    }

    [Fact]
    public void CurveToEdPublic_MatchesOwnDerivedPublicKey()
    {
        // The interop-critical property: a peer who only has our X25519 PUBLIC
        // key (via CurveToEdPublic, mirroring firmware curve_to_ed_pub) must
        // derive the exact same Ed25519 public key we computed for ourselves
        // from the private key (DeriveEdKeysFromCurvePrivateKey). If these
        // disagree, real firmware could never verify our signatures.
        var curvePriv = Curve25519.GeneratePrivateKey();
        var curvePub = Curve25519.GetPublicKey(curvePriv);

        var (_, edPubFromPriv) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(curvePriv);
        var edPubFromPub = XEdDSA.CurveToEdPublic(curvePub);

        Assert.Equal(edPubFromPriv, edPubFromPub);
    }

    [Fact]
    public void Verify_UsingPeerDerivedKey_AcceptsOurSignature()
    {
        // End-to-end: sign with our derived ed keys, verify using only the
        // birationally-mapped X25519 public key — the path a real firmware
        // receiver takes.
        var curvePriv = Curve25519.GeneratePrivateKey();
        var curvePub = Curve25519.GetPublicKey(curvePriv);
        var (edPriv, edPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(curvePriv);

        var message = RandomBytes(40);
        var sig = XEdDSA.Sign(edPriv, edPub, message, RandomBytes(32));

        var receiverSideEdPub = XEdDSA.CurveToEdPublic(curvePub);
        Assert.True(XEdDSA.Verify(receiverSideEdPub, message, sig));
    }

    [Fact]
    public void Verify_RejectsTamperedMessage()
    {
        var curvePriv = Curve25519.GeneratePrivateKey();
        var (edPriv, edPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(curvePriv);
        var message = RandomBytes(20);
        var sig = XEdDSA.Sign(edPriv, edPub, message, RandomBytes(32));

        message[0] ^= 0xFF;

        Assert.False(XEdDSA.Verify(edPub, message, sig));
    }

    [Fact]
    public void Verify_RejectsTamperedSignature()
    {
        var curvePriv = Curve25519.GeneratePrivateKey();
        var (edPriv, edPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(curvePriv);
        var message = RandomBytes(20);
        var sig = XEdDSA.Sign(edPriv, edPub, message, RandomBytes(32));

        sig[63] ^= 0xFF;

        Assert.False(XEdDSA.Verify(edPub, message, sig));
    }

    [Fact]
    public void Verify_RejectsWrongPublicKey()
    {
        var (edPriv, edPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(Curve25519.GeneratePrivateKey());
        var (_, otherEdPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(Curve25519.GeneratePrivateKey());
        var message = RandomBytes(20);
        var sig = XEdDSA.Sign(edPriv, edPub, message, RandomBytes(32));

        Assert.False(XEdDSA.Verify(otherEdPub, message, sig));
    }

    [Fact]
    public void Sign_IsHedged_DifferentSignaturesForSameInput()
    {
        var (edPriv, edPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(Curve25519.GeneratePrivateKey());
        var message = RandomBytes(20);

        var sig1 = XEdDSA.Sign(edPriv, edPub, message, RandomBytes(32));
        var sig2 = XEdDSA.Sign(edPriv, edPub, message, RandomBytes(32));

        Assert.NotEqual(sig1, sig2);
        Assert.True(XEdDSA.Verify(edPub, message, sig1));
        Assert.True(XEdDSA.Verify(edPub, message, sig2));
    }

    [Fact]
    public void Verify_RejectsMalformedInputLengths()
    {
        var (edPriv, edPub) = XEdDSA.DeriveEdKeysFromCurvePrivateKey(Curve25519.GeneratePrivateKey());
        var sig = XEdDSA.Sign(edPriv, edPub, RandomBytes(10), RandomBytes(32));

        Assert.False(XEdDSA.Verify(edPub, RandomBytes(10), FromHex("00")));
        Assert.False(XEdDSA.Verify(FromHex("00"), RandomBytes(10), sig));
    }
}
