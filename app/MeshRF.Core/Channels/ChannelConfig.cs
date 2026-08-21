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

    /// <summary>
    /// Firmware <c>getKey()</c> reports a disabled channel's key as invalid and
    /// <c>generateHash()</c> returns -1 for it, so the channel matches no
    /// incoming packet and carries no outgoing one. Callers must skip it rather
    /// than reading <see cref="EffectiveKey"/>, which describes only the key.
    /// </summary>
    public bool IsDisabled => Role == ChannelRole.Disabled;

    /// <summary>
    /// Supplies the primary channel so a secondary that stores no PSK of its
    /// own can borrow it, as firmware <c>getKey()</c> does. Wired by whoever
    /// owns the channel set; on a standalone config it is null and no
    /// inheritance happens.
    /// </summary>
    public Func<ChannelConfig?>? PrimaryProvider { get; set; }

    public byte PositionPrecision { get; set; } = 13;

    /// <summary>
    /// What <see cref="PositionPrecision"/> is allowed to mean on the air.
    /// Every transmit path reads this rather than the raw setting, so a
    /// precise position cannot leave on a channel anyone can decrypt — see
    /// <see cref="PositionPrecisionPolicy"/>.
    /// </summary>
    public byte EffectivePositionPrecision => PositionPrecisionPolicy.EffectiveFor(this);
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
    /// <item>any other length → zero-padded up to the next AES size, because
    ///       firmware pads rather than rejects and we have to match a node
    ///       configured that way</item>
    /// </list>
    /// Returns an empty array when the channel is unencrypted.
    /// </summary>
    public byte[] EffectiveKey
    {
        get
        {
            // A secondary that stores no PSK is not unencrypted: firmware hands
            // it the primary's key, and hashes it with that key too. Only a
            // stored length of 0 inherits — a 1-byte 0x00 is the "no crypto"
            // shorthand, which firmware checks after this branch.
            if (Psk.Length == 0 && Role == ChannelRole.Secondary)
            {
                var primary = PrimaryProvider?.Invoke();
                if (primary is not null && !ReferenceEquals(primary, this))
                    return primary.EffectiveKey;
            }

            if (Psk.Length == 1)
            {
                byte index = Psk[0];
                if (index == 0) return Array.Empty<byte>();
                var key = (byte[])DefaultPsk.Clone();
                key[^1] = (byte)(DefaultPsk[^1] + index - 1);
                return key;
            }
            // Firmware getKey() pads a short key with zeros to the next AES
            // size instead of refusing it ("User provided a too short AES128
            // key - padding"), so a channel configured that way is perfectly
            // decodable on the mesh. Pad the same way or we'd skip it.
            if (Psk.Length is > 1 and < 16) return Pad(16);
            if (Psk.Length is > 16 and < 32) return Pad(32);

            // Return a copy, not the live backing array: callers that treat
            // this as "their" key (e.g. zeroing it after use) must not
            // corrupt the channel's actual stored PSK.
            return (byte[])Psk.Clone();

            byte[] Pad(int size)
            {
                var padded = new byte[size];
                Psk.AsSpan().CopyTo(padded);
                return padded;
            }
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
