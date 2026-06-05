// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Channels;

/// <summary>Mirrors firmware <c>Channel.Role</c>.</summary>
public enum ChannelRole : byte
{
    Disabled  = 0,
    Primary   = 1,
    Secondary = 2,
}

/// <summary>
/// Mirrors firmware <c>ChannelSettings</c> + <c>Channel</c>. A device tracks
/// up to 8 channels; index 0 is the primary, the rest are secondaries.
/// </summary>
public sealed class ChannelConfig
{
    /// <summary>0..7. Slot 0 = primary.</summary>
    public int Index { get; set; }

    /// <summary>
    /// Display name. Meshtastic clients use this as the "name" field of the
    /// channel and feed it into the channel-hash hint. Empty for the
    /// "default" channel (firmware then uses the modem-preset name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 16-byte AES128 or 32-byte AES256 PSK. A 1-byte value of 0x01 means
    /// "use the well-known default key" (matches firmware behaviour).
    /// </summary>
    public byte[] Psk { get; set; } = DefaultPsk;

    public ChannelRole Role { get; set; } = ChannelRole.Secondary;

    public byte PositionPrecision { get; set; } = 13;
    public bool UplinkEnabled     { get; set; }
    public bool DownlinkEnabled   { get; set; }

    /// <summary>
    /// The well-known default PSK shipped by Meshtastic firmware. Anything
    /// transmitted on a channel whose PSK is the single byte 0x01 is encrypted
    /// with this key.
    /// </summary>
    public static readonly byte[] DefaultPsk = new byte[]
    {
        0xd4, 0xf1, 0xbb, 0x3a, 0x20, 0x29, 0x07, 0x59,
        0xf0, 0xbc, 0xff, 0xab, 0xcf, 0x4e, 0x69, 0x01,
    };

    /// <summary>True if this channel uses the firmware's default PSK.</summary>
    public bool UsesDefaultKey =>
        (Psk.Length == 1 && Psk[0] == 0x01) ||
        (Psk.Length == DefaultPsk.Length && Psk.AsSpan().SequenceEqual(DefaultPsk));

    /// <summary>
    /// The actual AES key bytes used on the air, expanding Meshtastic's
    /// single-byte PSK shorthand:
    /// <list type="bullet">
    /// <item>length 0, or single byte 0x00 → empty (no encryption)</item>
    /// <item>single byte 0x01 → the well-known default key</item>
    /// <item>single byte N (2..255) → default key with the last byte set to
    ///       <c>defaultLast + (N - 1)</c> (firmware <c>Channels::getKey</c>)</item>
    /// <item>length 16 or 32 → used verbatim</item>
    /// </list>
    /// Returns an empty array when the channel is unencrypted.
    /// </summary>
    public byte[] EffectiveKey
    {
        get
        {
            if (Psk.Length == 1)
            {
                byte index = Psk[0];
                if (index == 0) return Array.Empty<byte>();
                var key = (byte[])DefaultPsk.Clone();
                key[^1] = (byte)(DefaultPsk[^1] + index - 1);
                return key;
            }
            return Psk;
        }
    }

    /// <summary>
    /// One-byte channel hash hint stored in packet headers (XOR-fold of the
    /// channel name bytes XORed against each PSK byte). Same algorithm as
    /// firmware <c>Channels::generateHash</c>.
    /// </summary>
    public byte Hash
    {
        get
        {
            byte h = 0;
            foreach (var c in Name) h ^= (byte)c;
            var key = EffectiveKey;
            foreach (var b in key) h ^= b;
            return h;
        }
    }

    /// <summary>Generate a fresh random AES256 PSK.</summary>
    public static byte[] NewRandomPsk(int byteLength = 32)
    {
        var k = new byte[byteLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(k);
        return k;
    }
}
