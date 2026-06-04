// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace MeshtasticRF.Mesh;

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
}
