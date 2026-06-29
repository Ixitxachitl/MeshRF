// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Nodes;

/// <summary>
/// Meshtastic derives a node number from the 32-byte PKI public key as a
/// standard CRC-32 over the raw public-key bytes.
/// </summary>
public static class PkiNodeNumber
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public static bool TryFromPublicKey(ReadOnlySpan<byte> publicKey, out uint nodeNum)
    {
        if (publicKey.Length != 32)
        {
            nodeNum = 0;
            return false;
        }

        nodeNum = ComputeCrc32(publicKey);
        return true;
    }

    public static bool TryFromHexPublicKey(string? publicKeyHex, out uint nodeNum)
    {
        if (string.IsNullOrWhiteSpace(publicKeyHex))
        {
            nodeNum = 0;
            return false;
        }

        try
        {
            return TryFromPublicKey(Convert.FromHexString(publicKeyHex.Trim()), out nodeNum);
        }
        catch (FormatException)
        {
            nodeNum = 0;
            return false;
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        return ~crc;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }

        return table;
    }
}