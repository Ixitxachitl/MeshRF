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
    /// Sign a packet, mirroring firmware <c>CryptoEngine::xeddsa_sign</c>. The
    /// signature covers the whole Data envelope (see <see cref="BuildSigningBuffer"/>),
    /// and a fresh 32-byte random "hedge" is mixed into the nonce every call.
    /// </summary>
    public static byte[] XeddsaSign(uint fromNode, uint packetId, uint toNode,
                                    in XeddsaEnvelope envelope, ReadOnlySpan<byte> payload,
                                    byte[] edPrivateKey, byte[] edPublicKey)
    {
        byte[] signingBuffer = BuildSigningBuffer(fromNode, packetId, toNode, envelope, payload);
        byte[] hedge = RandomNumberGenerator.GetBytes(32);
        return XEdDSA.Sign(edPrivateKey, edPublicKey, signingBuffer, hedge);
    }

    /// <summary>
    /// Verify a packet's XEdDSA signature against the sender's known X25519
    /// (PKI) public key, mirroring firmware <c>CryptoEngine::xeddsa_verify</c>.
    /// Returns false for a missing/wrong-size signature, an unknown/malformed
    /// sender key, or a signature that doesn't verify (tampered, wrong sender,
    /// or wrong key).
    /// </summary>
    public static bool XeddsaVerify(uint fromNode, uint packetId, uint toNode,
                                    in XeddsaEnvelope envelope, ReadOnlySpan<byte> payload,
                                    byte[]? signature, byte[]? senderCurvePublicKey)
    {
        if (signature is null || signature.Length != XeddsaSignatureSize) return false;
        if (senderCurvePublicKey is null || senderCurvePublicKey.Length != 32) return false;

        byte[] signingBuffer = BuildSigningBuffer(fromNode, packetId, toNode, envelope, payload);
        byte[] senderEdPublicKey = XEdDSA.CurveToEdPublic(senderCurvePublicKey);
        return XEdDSA.Verify(senderEdPublicKey, signingBuffer, signature);
    }

    // Firmware XEDDSA_SIGNING_VERSION and the flags-byte bits.
    private const byte XeddsaSigningVersion = 0x01;
    private const byte XeddsaFlagWantResponse = 0x01;
    private const byte XeddsaFlagHasBitfield = 0x02;
    // version(1) + from, id, to, portnum, request_id, reply_id, emoji, bitfield (4 each) + flags(1)
    public const int XeddsaSignedHeaderLength = 1 + 8 * 4 + 1;

    /// <summary>
    /// The bytes a signature covers, byte-for-byte as firmware
    /// <c>buildSigningBuffer</c>, all integers little-endian:
    /// <c>version(1) | from | id | to | portnum | request_id | reply_id | emoji
    /// | bitfield | flags(1) | payload</c>. The header is fixed-length so the
    /// payload boundary never depends on content. An absent bitfield signs as
    /// zero with its presence in the flags byte, so stripping the field is not
    /// the same as sending it empty.
    /// </summary>
    public static byte[] BuildSigningBuffer(uint fromNode, uint packetId, uint toNode,
                                              in XeddsaEnvelope envelope, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[XeddsaSignedHeaderLength + payload.Length];
        var w = buf.AsSpan();
        w[0] = XeddsaSigningVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(1, 4), fromNode);
        BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(5, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(9, 4), toNode);
        BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(13, 4), envelope.Portnum);
        BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(17, 4), envelope.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(21, 4), envelope.ReplyId);
        BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(25, 4), envelope.Emoji);
        BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(29, 4), envelope.Bitfield ?? 0);
        w[33] = (byte)((envelope.WantResponse ? XeddsaFlagWantResponse : 0)
                       | (envelope.Bitfield.HasValue ? XeddsaFlagHasBitfield : 0));
        payload.CopyTo(w.Slice(XeddsaSignedHeaderLength));
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

/// <summary>
/// The <c>Data</c> envelope fields an XEdDSA signature covers besides the
/// payload. Mirrors what firmware reads from the decoded <c>meshtastic_Data</c>:
/// <paramref name="WantResponse"/> is the raw field 3, not merged with
/// bitfield bit 1 (firmware verifies before merging them), and
/// <paramref name="Bitfield"/> is null when field 9 was absent.
/// </summary>
public readonly record struct XeddsaEnvelope(
    uint Portnum,
    uint RequestId = 0,
    uint ReplyId = 0,
    uint Emoji = 0,
    uint? Bitfield = null,
    bool WantResponse = false);
