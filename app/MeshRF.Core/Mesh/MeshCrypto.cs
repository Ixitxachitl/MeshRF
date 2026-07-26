// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MeshRF.Mesh;

/// <summary>
/// Meshtastic packet payload encryption. Channels use AES-CTR (AES-128 for a
/// 16-byte PSK, AES-256 for a 32-byte PSK) with a per-packet nonce derived
/// from the packet id and sender node number, exactly as the firmware's
/// <c>CryptoEngine</c> does.
///
/// The 16-byte counter block is:
/// <code>
///   bytes  0..7  : packetId  (little-endian, 32-bit id zero-extended to 64)
///   bytes  8..11 : fromNode  (little-endian)
///   bytes 12..15 : block counter (starts at 0, big-endian increment)
/// </code>
/// .NET has no built-in CTR mode, so we generate the keystream by AES-ECB
/// encrypting successive counter blocks and XORing into the data. CTR is
/// symmetric, so the same routine both encrypts and decrypts.
/// </summary>
public static class MeshCrypto
{
    /// <summary>
    /// Decrypt (or encrypt) <paramref name="data"/> in place-style, returning a
    /// new buffer. <paramref name="key"/> must be 16 or 32 bytes.
    /// </summary>
    public static byte[] Ctr(ReadOnlySpan<byte> data, byte[] key,
                             uint fromNode, ulong packetId)
    {
        if (key.Length != 16 && key.Length != 32)
            throw new ArgumentException("AES key must be 16 or 32 bytes", nameof(key));

        Span<byte> counter = stackalloc byte[16];
        InitCounter(counter, fromNode, packetId);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();

        var outBuf = new byte[data.Length];
        Span<byte> keystream = stackalloc byte[16];
        var block = new byte[16];

        for (int offset = 0; offset < data.Length; offset += 16)
        {
            // keystream = AES_ECB(counter)
            counter.CopyTo(block);
            var ks = enc.TransformFinalBlock(block, 0, 16);
            ks.CopyTo(keystream);

            int n = Math.Min(16, data.Length - offset);
            for (int i = 0; i < n; i++)
                outBuf[offset + i] = (byte)(data[offset + i] ^ keystream[i]);

            IncrementCounter(counter);
        }
        return outBuf;
    }

    private static void InitCounter(Span<byte> counter, uint fromNode, ulong packetId)
    {
        counter.Clear();
        // packetId little-endian into bytes 0..7
        for (int i = 0; i < 8; i++)
            counter[i] = (byte)((packetId >> (8 * i)) & 0xFF);
        // fromNode little-endian into bytes 8..11
        for (int i = 0; i < 4; i++)
            counter[8 + i] = (byte)((fromNode >> (8 * i)) & 0xFF);
        // bytes 12..15 = block counter = 0
    }

    /// <summary>Big-endian increment from the last byte (matches the firmware's
    /// rweather/Crypto CTR implementation).</summary>
    private static void IncrementCounter(Span<byte> counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0) break; // no carry
        }
    }

    // -- PKC (public-key crypto) direct messages -----------------------------
    //
    // Modern Meshtastic firmware (2.5+) encrypts direct messages with PKC
    // instead of the channel PSK: an X25519 ECDH shared secret is hashed with
    // SHA-256 to form an AES-256 key, then the payload is sealed with AES-CCM
    // (13-byte nonce, 8-byte auth tag). Mirrors firmware
    // CryptoEngine::encryptCurve25519 / decryptCurve25519.

    /// <summary>Bytes added to the plaintext by a PKC seal: 8-byte auth tag +
    /// 4-byte extra nonce (firmware <c>MESHTASTIC_PKC_OVERHEAD</c>).</summary>
    public const int PkcOverhead = 12;

    private const int PkcNonceLen = 13;
    private const int PkcTagLen = 8;

    /// <summary>
    /// PKC-encrypt <paramref name="plain"/> for a peer. Returns
    /// <c>ciphertext || 8-byte tag || 4-byte extraNonce(LE)</c>
    /// (<paramref name="plain"/>.Length + 12 bytes). The AES-256 key is
    /// <c>SHA256(X25519(myPrivateKey, peerPublicKey))</c>.
    /// </summary>
    public static byte[] PkcEncrypt(ReadOnlySpan<byte> plain, byte[] myPrivateKey,
                                    byte[] peerPublicKey, uint fromNode, uint packetId)
    {
        byte[] key = DeriveSharedKey(myPrivateKey, peerPublicKey);

        // 32-bit random extra nonce, exactly as the firmware (random()).
        uint extraNonce = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));

        Span<byte> nonce = stackalloc byte[PkcNonceLen];
        BuildNonce(nonce, fromNode, packetId, extraNonce);

        var output = new byte[plain.Length + PkcOverhead];
        Span<byte> tag = stackalloc byte[PkcTagLen];
        using (var ccm = new AesCcm(key))
            ccm.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), tag);

        tag.CopyTo(output.AsSpan(plain.Length, PkcTagLen));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(plain.Length + PkcTagLen, 4), extraNonce);
        CryptographicOperations.ZeroMemory(key);
        return output;
    }

    /// <summary>
    /// PKC-decrypt a sealed buffer (<c>ciphertext || 8-byte tag || 4-byte
    /// extraNonce(LE)</c>). Returns the plaintext, or null if the authentication
    /// tag does not verify (wrong key pair / corrupt frame).
    /// </summary>
    public static byte[]? PkcDecrypt(ReadOnlySpan<byte> data, byte[] myPrivateKey,
                                     byte[] peerPublicKey, uint fromNode, uint packetId)
    {
        if (data.Length <= PkcOverhead) return null;
        int ctLen = data.Length - PkcOverhead;

        byte[] key = DeriveSharedKey(myPrivateKey, peerPublicKey);

        uint extraNonce = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(data.Length - 4, 4));
        var tag = data.Slice(ctLen, PkcTagLen);
        var cipher = data.Slice(0, ctLen);

        Span<byte> nonce = stackalloc byte[PkcNonceLen];
        BuildNonce(nonce, fromNode, packetId, extraNonce);

        var plain = new byte[ctLen];
        try
        {
            using var ccm = new AesCcm(key);
            ccm.Decrypt(nonce, cipher, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return plain;
    }

    // -- XEdDSA packet signing (firmware 2.8+) -------------------------------
    //
    // Broadcast packets are signed with an Ed25519 signature derived from the
    // node's existing X25519 PKI keypair (see XEdDSA.cs). A peer that verifies
    // one of these marks us as a signer and shows the "shield" icon next to
    // our name. Mirrors firmware CryptoEngine::xeddsa_sign/xeddsa_verify.

    /// <summary>Size in bytes of an XEdDSA signature (<c>Data.xeddsa_signature</c>).</summary>
    public const int XeddsaSignatureSize = XEdDSA.SignatureSize;

    /// <summary>
    /// Derive our Ed25519 signing keypair from our X25519 identity private key.
    /// Cache the result — this only needs to be recomputed when the identity
    /// key changes, not per packet.
    /// </summary>
    public static (byte[] edPrivateKey, byte[] edPublicKey) DeriveXeddsaKeys(byte[] curvePrivateKey)
        => XEdDSA.DeriveEdKeysFromCurvePrivateKey(curvePrivateKey);

    /// <summary>
    /// Sign a broadcast packet's payload, mirroring firmware
    /// <c>CryptoEngine::xeddsa_sign</c>: the signed buffer is
    /// <c>fromNode(4,LE) || packetId(4,LE) || portnum(4,LE) || payload</c>, and
    /// a fresh 32-byte random "hedge" is mixed into the nonce every call.
    /// </summary>
    public static byte[] XeddsaSign(uint fromNode, uint packetId, uint portnum,
                                    ReadOnlySpan<byte> payload,
                                    byte[] edPrivateKey, byte[] edPublicKey)
    {
        byte[] signingBuffer = BuildSigningBuffer(fromNode, packetId, portnum, payload);
        byte[] hedge = RandomNumberGenerator.GetBytes(32);
        return XEdDSA.Sign(edPrivateKey, edPublicKey, signingBuffer, hedge);
    }

    /// <summary>
    /// Verify a broadcast packet's XEdDSA signature against the sender's known
    /// X25519 (PKI) public key, mirroring firmware
    /// <c>CryptoEngine::xeddsa_verify</c>. Returns false for a missing/wrong-size
    /// signature, an unknown/malformed sender key, or a signature that doesn't
    /// verify (tampered, wrong sender, or wrong key).
    /// </summary>
    public static bool XeddsaVerify(uint fromNode, uint packetId, uint portnum,
                                    ReadOnlySpan<byte> payload,
                                    byte[]? signature, byte[]? senderCurvePublicKey)
    {
        if (signature is null || signature.Length != XeddsaSignatureSize) return false;
        if (senderCurvePublicKey is null || senderCurvePublicKey.Length != 32) return false;

        byte[] signingBuffer = BuildSigningBuffer(fromNode, packetId, portnum, payload);
        byte[] senderEdPublicKey = XEdDSA.CurveToEdPublic(senderCurvePublicKey);
        return XEdDSA.Verify(senderEdPublicKey, signingBuffer, signature);
    }

    private static byte[] BuildSigningBuffer(uint fromNode, uint packetId, uint portnum, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), fromNode);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), portnum);
        payload.CopyTo(buf.AsSpan(12));
        return buf;
    }

    // shared AES key = SHA256(X25519(ourPriv, theirPub)) — firmware setDHPublicKey + hash().
    private static byte[] DeriveSharedKey(byte[] myPrivateKey, byte[] peerPublicKey)
    {
        var secret = Curve25519.SharedSecret(myPrivateKey, peerPublicKey);
        var key = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
        return key;
    }

    // 13-byte AES-CCM nonce, byte-for-byte as firmware CryptoEngine::initNonce:
    //   [0..3]  packetId  (32-bit, little-endian)
    //   [4..7]  extraNonce(32-bit, little-endian) — overwrites the high half of
    //           the 64-bit packetId, which is always zero for a 32-bit id
    //   [8..11] fromNode  (32-bit, little-endian)
    //   [12]    0
    private static void BuildNonce(Span<byte> nonce, uint fromNode, uint packetId, uint extraNonce)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.Slice(0, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.Slice(4, 4), extraNonce);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.Slice(8, 4), fromNode);
        // nonce[12] stays 0
    }
}
