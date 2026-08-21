// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Channels;

/// <summary>
/// How precise a position may be on a given channel, mirroring firmware's
/// <c>PositionPrecision</c>.
/// </summary>
/// <remarks>
/// A channel anyone can decrypt is a channel anyone can read a position off,
/// so an exact one on it is public information. Firmware caps those at
/// <see cref="MaxOnPublicKey"/> bits — roughly a 500 m cell, chosen to stay
/// outside what the CCPA calls precise geolocation (within ~564 m) — and
/// applies the cap on the way out rather than trusting the stored setting, so
/// a config that arrived from elsewhere cannot raise it.
/// </remarks>
public static class PositionPrecisionPolicy
{
    /// <summary>Firmware's <c>MAX_POSITION_PRECISION_PUBLIC_KEY</c>.</summary>
    public const byte MaxOnPublicKey = 15;

    /// <summary>
    /// Whether this channel's traffic can be decrypted by anyone: no key at
    /// all, or one of the well-known default keys.
    /// </summary>
    /// <remarks>
    /// Mirrors firmware <c>channelFileUsesPublicKey</c>. A single PSK byte is
    /// Meshtastic's shorthand — 0 disables encryption, 1..255 select the
    /// published default key family — so every one of them is public. A
    /// secondary that stores no key of its own is answered by whatever its
    /// primary uses, and a config with no primary to ask fails closed.
    /// </remarks>
    public static bool UsesPublicKey(ChannelConfig channel)
    {
        if (channel.Psk.Length == 0)
        {
            if (channel.Role != ChannelRole.Secondary) return true;
            var primary = channel.PrimaryProvider?.Invoke();
            if (primary is null || ReferenceEquals(primary, channel)) return true;
            return UsesPublicKey(primary);
        }

        if (channel.Psk.Length == 1) return true;

        return channel.Psk.Length == ChannelConfig.DefaultPsk.Length
            && channel.Psk.AsSpan(0, ChannelConfig.DefaultPsk.Length - 1)
                   .SequenceEqual(ChannelConfig.DefaultPsk.AsSpan(0, ChannelConfig.DefaultPsk.Length - 1));
    }

    /// <summary>
    /// The precision that may actually be transmitted on this channel: what it
    /// is configured for, capped when anyone could decrypt it. 0 means location
    /// sharing is off and nothing may be sent at all.
    /// </summary>
    public static byte EffectiveFor(ChannelConfig channel)
    {
        if (channel.IsDisabled) return 0;
        byte configured = channel.PositionPrecision;
        return configured > MaxOnPublicKey && UsesPublicKey(channel) ? MaxOnPublicKey : configured;
    }

    /// <summary>The ceiling a channel may be configured up to, for the picker
    /// that offers the choice. 32 (exact) where the key is the user's own.</summary>
    public static byte CeilingFor(ChannelConfig channel) =>
        UsesPublicKey(channel) ? MaxOnPublicKey : (byte)32;
}
