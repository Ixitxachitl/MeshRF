// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Security.Cryptography;

namespace MeshRF.Mesh;

/// <summary>
/// XEdDSA packet signing (Meshtastic firmware 2.8+): signs broadcast packets
/// with an Ed25519 signature derived from the node's existing X25519 (PKI)
/// identity keypair via the birational map in RFC 7748 §4.1, exactly as
/// firmware's <c>CryptoEngine</c> + the vendored <c>meshtastic/Crypto</c>
/// <c>XEdDSA</c> class do (itself a port of the SUPERCOP/ref10 Ed25519 code by
/// D. J. Bernstein). A peer that verifies one of our signed broadcasts marks
/// us as a signer (<c>HAS_XEDDSA_SIGNED</c>) and shows the shield icon next to
/// our name on the BaseUI favorites screen.
///
/// Implemented with <see cref="BigInteger"/> affine curve arithmetic (like
/// <see cref="Curve25519"/>) rather than a constant-time limb-based port:
/// correctness over speed, since keys/signing happen on a desktop host, not
/// embedded hardware. Matches <c>meshtastic/Crypto</c>'s <c>XEdDSA.cpp</c> bit
/// for bit; see that source for the byte-level design this mirrors.
/// </summary>
public static class XEdDSA
{
    /// <summary>Size of an XEdDSA/Ed25519 signature (R || s).</summary>
    public const int SignatureSize = 64;

    // Ed25519 field prime: p = 2^255 - 19.
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    // Ed25519 group (base-point) order: L = 2^252 + 27742317777372353535851937790883648493.
    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    // Twisted Edwards curve parameter: d = -121665/121666 mod p.
    private static readonly BigInteger D = Mod(-121665 * ModInverse(121666, P), P);

    // A square root of -1 mod p (p ≡ 5 mod 8), used by point decompression.
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);

    private readonly record struct Point(BigInteger X, BigInteger Y);

    private static readonly Point Identity = new(BigInteger.Zero, BigInteger.One);

    // Standard Ed25519 base point: y = 4/5 mod p, sign bit 0.
    private static readonly Point BasePoint =
        RecoverPoint(Mod(4 * ModInverse(5, P), P), signBit: 0)
        ?? throw new InvalidOperationException("Ed25519 base point failed to decode.");

    // ---- Key derivation -----------------------------------------------

    /// <summary>
    /// Derive an Ed25519 signing keypair from our existing X25519 (PKI)
    /// private key, mirroring firmware <c>CryptoEngine::generateKeypair</c> /
    /// <c>XEdDSA::priv_curve_to_ed_keys</c>: clamp the scalar exactly like
    /// X25519, compute the public point, and — if its sign bit is 1 — negate
    /// the scalar mod L and recompute so the public key always normalizes to
    /// sign bit 0 (the convention XEdDSA verifiers assume).
    /// </summary>
    public static (byte[] edPrivateKey, byte[] edPublicKey) DeriveEdKeysFromCurvePrivateKey(byte[] curvePrivateKey)
    {
        if (curvePrivateKey is null || curvePrivateKey.Length != 32)
            throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(curvePrivateKey));

        var edPriv = (byte[])curvePrivateKey.Clone();
        edPriv[0] &= 0xF8;
        edPriv[31] &= 0x7F;
        edPriv[31] |= 0x40;

        BigInteger a = DecodeScalarLE(edPriv);
        var pub = ScalarMult(a, BasePoint);
        byte[] edPub = EncodePoint(pub);

        if ((edPub[31] & 0x80) != 0)
        {
            a = Mod(L - a, L);
            // The pre-negation scalar bytes are about to be discarded in
            // favor of the negated form below; clear them rather than
            // leaving raw key-derived material to linger until GC.
            CryptographicOperations.ZeroMemory(edPriv);
            edPriv = EncodeScalarLE(a);
            pub = ScalarMult(a, BasePoint);
            edPub = EncodePoint(pub);
            edPub[31] &= 0x7F; // guaranteed by negation; defensive only.
        }

        return (edPriv, edPub);
    }

    /// <summary>
    /// Birational map for a peer's X25519 <b>public</b> key (no private key
    /// available) to the Ed25519 public key XEdDSA verification needs.
    /// Mirrors firmware <c>CryptoEngine::curve_to_ed_pub</c>: the sign bit is
    /// always forced to 0, matching how <see cref="DeriveEdKeysFromCurvePrivateKey"/>
    /// normalizes the signer's own key.
    /// </summary>
    public static byte[] CurveToEdPublic(byte[] curvePublicKey)
    {
        if (curvePublicKey is null || curvePublicKey.Length != 32)
            throw new ArgumentException("X25519 public key must be 32 bytes.", nameof(curvePublicKey));

        var uBytes = (byte[])curvePublicKey.Clone();
        uBytes[31] &= 0x7F; // RFC 7748 §5: MSB of the u-coordinate is masked.
        BigInteger u = DecodeLE(uBytes);

        BigInteger y = Mod((u - 1) * ModInverse(Mod(u + 1, P), P), P);

        var outp = new byte[32];
        var yBytes = y.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(yBytes, outp, Math.Min(yBytes.Length, 32));
        outp[31] &= 0x7F;
        return outp;
    }

    // ---- Sign / verify --------------------------------------------------

    /// <summary>
    /// Sign <paramref name="message"/>, mirroring <c>XEdDSA::sign</c>.
    /// <paramref name="hedge"/> must be 32 fresh random bytes (firmware pulls
    /// these from its HW RNG) mixed into the nonce for hedged/randomized
    /// signatures per the XEdDSA spec.
    /// </summary>
    public static byte[] Sign(byte[] edPrivateKey, byte[] edPublicKey, ReadOnlySpan<byte> message, byte[] hedge)
    {
        if (edPrivateKey is null || edPrivateKey.Length != 32)
            throw new ArgumentException("Ed25519 private scalar must be 32 bytes.", nameof(edPrivateKey));
        if (edPublicKey is null || edPublicKey.Length != 32)
            throw new ArgumentException("Ed25519 public key must be 32 bytes.", nameof(edPublicKey));
        if (hedge is null || hedge.Length != 32)
            throw new ArgumentException("Hedge must be 32 random bytes.", nameof(hedge));

        BigInteger a = DecodeScalarLE(edPrivateKey);

        // prefix = SHA512(privateKey)[32..63] — mixed into the nonce for hedging.
        byte[] hashedPriv = SHA512.HashData(edPrivateKey);
        var messageBytes = message.ToArray();

        byte[] rHash;
        try
        {
            rHash = SHA512.HashData(Concat(hashedPriv.AsSpan(32, 32).ToArray(), messageBytes, hedge));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hashedPriv);
        }
        BigInteger r = Mod(DecodeLE(rHash), L);

        var rPoint = ScalarMult(r, BasePoint);
        byte[] rEncoded = EncodePoint(rPoint);

        byte[] kHash = SHA512.HashData(Concat(rEncoded, edPublicKey, messageBytes));
        BigInteger k = Mod(DecodeLE(kHash), L);

        BigInteger s = Mod(r + k * a, L);

        var sig = new byte[SignatureSize];
        rEncoded.CopyTo(sig, 0);
        EncodeScalarLE(s).CopyTo(sig, 32);
        return sig;
    }

    /// <summary>
    /// Verify a 64-byte XEdDSA/Ed25519 signature over <paramref name="message"/>
    /// against <paramref name="edPublicKey"/> (as produced by
    /// <see cref="DeriveEdKeysFromCurvePrivateKey"/> or <see cref="CurveToEdPublic"/>).
    /// Mirrors <c>XEdDSA::verify</c> (the base <c>Ed25519::verify</c> check).
    /// </summary>
    public static bool Verify(byte[] edPublicKey, ReadOnlySpan<byte> message, byte[] signature)
    {
        if (edPublicKey is null || edPublicKey.Length != 32) return false;
        if (signature is null || signature.Length != SignatureSize) return false;

        var a = DecodePoint(edPublicKey);
        if (a is null) return false;
        var r = DecodePoint(signature.AsSpan(0, 32));
        if (r is null) return false;

        BigInteger s = DecodeLE(signature.AsSpan(32, 32).ToArray());
        if (s.Sign < 0 || s >= L) return false; // reject non-canonical s.

        byte[] kHash = SHA512.HashData(Concat(signature.AsSpan(0, 32).ToArray(), edPublicKey, message.ToArray()));
        BigInteger k = Mod(DecodeLE(kHash), L);

        var left = ScalarMult(s, BasePoint);
        var right = PointAdd(r.Value, ScalarMult(k, a.Value));

        return Mod(left.X, P) == Mod(right.X, P) && Mod(left.Y, P) == Mod(right.Y, P);
    }

    // ---- Curve arithmetic (affine, unified twisted-Edwards addition law) ----

    private static Point PointAdd(Point p1, Point p2)
    {
        BigInteger x1 = p1.X, y1 = p1.Y, x2 = p2.X, y2 = p2.Y;
        BigInteger numX = Mod(x1 * y2 + x2 * y1, P);
        BigInteger numY = Mod(y1 * y2 + x1 * x2, P);
        BigInteger dxy = Mod(D * x1 * x2 * y1 * y2, P);
        BigInteger x3 = Mod(numX * ModInverse(Mod(1 + dxy, P), P), P);
        BigInteger y3 = Mod(numY * ModInverse(Mod(1 - dxy, P), P), P);
        return new Point(x3, y3);
    }

    private static Point ScalarMult(BigInteger k, Point p)
    {
        if (k.Sign < 0) throw new ArgumentOutOfRangeException(nameof(k));
        var acc = Identity;
        var cur = p;
        while (k > 0)
        {
            if (!k.IsEven) acc = PointAdd(acc, cur);
            cur = PointAdd(cur, cur);
            k >>= 1;
        }
        return acc;
    }

    private static byte[] EncodePoint(Point p)
    {
        var bytes = new byte[32];
        var yBytes = Mod(p.Y, P).ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(yBytes, bytes, Math.Min(yBytes.Length, 32));
        if (!Mod(p.X, P).IsEven) bytes[31] |= 0x80;
        return bytes;
    }

    private static Point? DecodePoint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) return null;
        int signBit = (bytes[31] & 0x80) != 0 ? 1 : 0;
        var yBytes = bytes.ToArray();
        yBytes[31] &= 0x7F;
        BigInteger y = DecodeLE(yBytes);
        if (y >= P) return null;
        return RecoverPoint(y, signBit);
    }

    // Recover x from y on -x^2 + y^2 = 1 + d*x^2*y^2 (mod p), per RFC 8032 §5.1.3.
    private static Point? RecoverPoint(BigInteger y, int signBit)
    {
        BigInteger y2 = Mod(y * y, P);
        BigInteger u = Mod(y2 - 1, P);
        BigInteger v = Mod(D * y2 + 1, P);
        BigInteger x2 = Mod(u * ModInverse(v, P), P);

        BigInteger x = ModSqrt(x2);
        if (x.Sign < 0) return null; // no square root exists — invalid point.
        if (x.IsZero && signBit == 1) return null; // can't produce -0.
        if (x.IsEven == (signBit == 1)) x = Mod(P - x, P);

        return new Point(x, y);
    }

    // Modular square root mod p ≡ 5 (mod 8) (RFC 8032 §5.1.3). Returns -1 if
    // `a` is not a quadratic residue.
    private static BigInteger ModSqrt(BigInteger a)
    {
        a = Mod(a, P);
        if (a.IsZero) return BigInteger.Zero;
        BigInteger cand = BigInteger.ModPow(a, (P + 3) / 8, P);
        if (Mod(cand * cand, P) == a) return cand;
        BigInteger cand2 = Mod(cand * SqrtM1, P);
        if (Mod(cand2 * cand2, P) == a) return cand2;
        return BigInteger.MinusOne;
    }

    // ---- Byte <-> BigInteger helpers ------------------------------------

    private static BigInteger DecodeScalarLE(byte[] k) => DecodeLE(k);

    private static byte[] EncodeScalarLE(BigInteger s)
    {
        var outp = new byte[32];
        var bytes = Mod(s, L).ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(bytes, outp, Math.Min(bytes.Length, 32));
        return outp;
    }

    // Little-endian unsigned bytes -> non-negative BigInteger (any length).
    private static BigInteger DecodeLE(byte[] b)
    {
        var le = new byte[b.Length + 1];
        Array.Copy(b, le, b.Length);
        return new BigInteger(le);
    }

    private static BigInteger Mod(BigInteger x, BigInteger m)
    {
        x %= m;
        return x.Sign < 0 ? x + m : x;
    }

    // Fermat's little theorem — valid because both P and L are prime.
    private static BigInteger ModInverse(BigInteger a, BigInteger m) => BigInteger.ModPow(Mod(a, m), m - 2, m);

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var outp = new byte[total];
        int off = 0;
        foreach (var p in parts)
        {
            p.CopyTo(outp, off);
            off += p.Length;
        }
        return outp;
    }
}
