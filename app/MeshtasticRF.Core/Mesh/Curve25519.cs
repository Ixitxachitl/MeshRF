// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Security.Cryptography;

namespace MeshtasticRF.Mesh;

/// <summary>
/// Minimal X25519 (Curve25519, RFC 7748) implementation used to derive the
/// public key from a private key for Meshtastic PKI direct messages. Uses a
/// <see cref="BigInteger"/> Montgomery ladder: this is NOT constant-time, but
/// the keys are generated/stored locally on a receive-only host, so timing
/// side-channels are not part of the threat model.
/// </summary>
public static class Curve25519
{
    // p = 2^255 - 19
    private static readonly BigInteger P =
        BigInteger.Pow(2, 255) - 19;
    // a24 = (486662 - 2) / 4
    private static readonly BigInteger A24 = 121665;

    /// <summary>Standard X25519 base point (u = 9).</summary>
    private static readonly byte[] BasePoint =
        { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
          0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>Generate a new clamped 32-byte X25519 private key.</summary>
    public static byte[] GeneratePrivateKey()
    {
        var k = new byte[32];
        RandomNumberGenerator.Fill(k);
        Clamp(k);
        return k;
    }

    /// <summary>Derive the 32-byte public key for a 32-byte private key.</summary>
    public static byte[] GetPublicKey(byte[] privateKey)
    {
        if (privateKey is null || privateKey.Length != 32)
            throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateKey));
        var clamped = (byte[])privateKey.Clone();
        Clamp(clamped);
        return ScalarMult(clamped, BasePoint);
    }

    /// <summary>
    /// Compute the raw X25519 shared secret between our <paramref name="privateKey"/>
    /// and a peer's <paramref name="peerPublicKey"/> (both 32 bytes) — the
    /// <c>Curve25519::dh2</c> step the Meshtastic firmware performs. The caller
    /// must still hash the result (firmware uses SHA-256) before using it as an
    /// AES key. Throws if the result is the all-zero weak point.
    /// </summary>
    public static byte[] SharedSecret(byte[] privateKey, byte[] peerPublicKey)
    {
        if (privateKey is null || privateKey.Length != 32)
            throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateKey));
        if (peerPublicKey is null || peerPublicKey.Length != 32)
            throw new ArgumentException("X25519 public key must be 32 bytes.", nameof(peerPublicKey));

        var clamped = (byte[])privateKey.Clone();
        Clamp(clamped);
        var secret = ScalarMult(clamped, peerPublicKey);

        // Weak-key check: an all-zero shared secret means a low-order public key
        // was supplied. The firmware (Curve25519::dh2) rejects this too.
        bool allZero = true;
        foreach (var b in secret)
            if (b != 0) { allZero = false; break; }
        if (allZero)
            throw new CryptographicException("X25519 produced a weak (all-zero) shared secret.");

        return secret;
    }

    private static void Clamp(byte[] k)
    {
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
    }

    private static byte[] ScalarMult(byte[] scalar, byte[] uCoord)
    {
        BigInteger k = DecodeScalar(scalar);
        BigInteger u = DecodeUCoordinate(uCoord);

        BigInteger x1 = u;
        BigInteger x2 = BigInteger.One, z2 = BigInteger.Zero;
        BigInteger x3 = u, z3 = BigInteger.One;
        int swap = 0;

        for (int t = 254; t >= 0; t--)
        {
            int kt = (int)((k >> t) & 1);
            swap ^= kt;
            CondSwap(swap, ref x2, ref x3);
            CondSwap(swap, ref z2, ref z3);
            swap = kt;

            BigInteger a = Mod(x2 + z2);
            BigInteger aa = Mod(a * a);
            BigInteger b = Mod(x2 - z2);
            BigInteger bb = Mod(b * b);
            BigInteger e = Mod(aa - bb);
            BigInteger c = Mod(x3 + z3);
            BigInteger d = Mod(x3 - z3);
            BigInteger da = Mod(d * a);
            BigInteger cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x1 * Mod((da - cb) * (da - cb)));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + Mod(A24 * e)));
        }

        CondSwap(swap, ref x2, ref x3);
        CondSwap(swap, ref z2, ref z3);

        BigInteger result = Mod(x2 * ModInverse(z2, P));
        return EncodeUCoordinate(result);
    }

    private static void CondSwap(int swap, ref BigInteger a, ref BigInteger b)
    {
        if (swap == 1) (a, b) = (b, a);
    }

    private static BigInteger Mod(BigInteger x)
    {
        x %= P;
        return x.Sign < 0 ? x + P : x;
    }

    private static BigInteger DecodeScalar(byte[] k)
    {
        var c = (byte[])k.Clone();
        Clamp(c);
        return DecodeLittleEndian(c);
    }

    private static BigInteger DecodeUCoordinate(byte[] u)
    {
        var c = (byte[])u.Clone();
        c[31] &= 0x7f; // mask the most-significant bit
        return DecodeLittleEndian(c);
    }

    private static BigInteger DecodeLittleEndian(byte[] b)
    {
        // BigInteger expects little-endian unsigned; append 0 to force positive.
        var le = new byte[b.Length + 1];
        Array.Copy(b, le, b.Length);
        return new BigInteger(le);
    }

    private static byte[] EncodeUCoordinate(BigInteger u)
    {
        u = Mod(u);
        var bytes = u.ToByteArray(isUnsigned: true, isBigEndian: false);
        var outp = new byte[32];
        Array.Copy(bytes, outp, Math.Min(bytes.Length, 32));
        return outp;
    }

    private static BigInteger ModInverse(BigInteger a, BigInteger m) =>
        BigInteger.ModPow(a, m - 2, m); // Fermat's little theorem (m is prime)
}
