// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// <see cref="ChannelConfig.EffectiveKey"/> has to reproduce firmware
/// <c>Channels::getKey()</c> exactly, including the parts of it that are
/// out of spec: a channel we can't key the same way is a channel we can't
/// decode.
/// </summary>
public class ChannelConfigKeyTests
{
    private static ChannelConfig WithPsk(params byte[] psk) => new() { Name = "Alta", Psk = psk };

    [Fact]
    public void SingleZeroByteMeansNoEncryption()
    {
        Assert.Empty(WithPsk(0x00).EffectiveKey);
    }

    [Fact]
    public void ShorthandOneIsTheDefaultKey()
    {
        Assert.Equal(ChannelConfig.DefaultPsk, WithPsk(0x01).EffectiveKey);
    }

    [Fact]
    public void SpellingTheDefaultKeyOutMatchesItsShorthand()
    {
        // The Default button writes the full sixteen bytes rather than AQ==.
        // Both forms have to key and hash identically or pressing it would
        // silently move the channel off the traffic it was matching.
        var shorthand = WithPsk(0x01);
        var spelledOut = WithPsk(ChannelConfig.DefaultPsk);

        Assert.Equal(shorthand.EffectiveKey, spelledOut.EffectiveKey);
        Assert.Equal(shorthand.Hash, spelledOut.Hash);
        Assert.True(spelledOut.UsesDefaultKey);
    }

    [Theory]
    [InlineData(0x02, 0x02)] // documented simple2
    [InlineData(0x0A, 0x0A)] // documented simple10
    [InlineData(0x30, 0x30)] // out of spec, but firmware expands it anyway
    public void ShorthandBumpsTheLastByteOfTheDefaultKey(byte index, byte expectedLast)
    {
        var key = WithPsk(index).EffectiveKey;

        Assert.Equal(ChannelConfig.DefaultPsk.Length, key.Length);
        Assert.Equal(ChannelConfig.DefaultPsk.AsSpan(0, 15).ToArray(), key.AsSpan(0, 15).ToArray());
        Assert.Equal(expectedLast, key[^1]);
    }

    [Fact]
    public void ShortKeysArePaddedToTheNextAesSize()
    {
        Assert.Equal(16, WithPsk(0xAA, 0xBB).EffectiveKey.Length);
        Assert.Equal(32, WithPsk(new byte[20]).EffectiveKey.Length);

        var padded = WithPsk(0xAA, 0xBB).EffectiveKey;
        Assert.Equal(0xAA, padded[0]);
        Assert.Equal(0xBB, padded[1]);
        Assert.All(padded[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void FullLengthKeysAreUsedVerbatim()
    {
        var aes256 = new byte[32];
        aes256[0] = 0x11;
        aes256[^1] = 0x22;

        Assert.Equal(aes256, WithPsk(aes256).EffectiveKey);
    }

    [Fact]
    public void SecondaryWithNoPskBorrowsThePrimaryKeyAndHash()
    {
        var primary = new ChannelConfig
        {
            Index = 0,
            Name = "LongFast",
            Role = ChannelRole.Primary,
            Psk = new byte[] { 0x01 },
        };
        var secondary = new ChannelConfig
        {
            Index = 1,
            Name = "Alta",
            Role = ChannelRole.Secondary,
            Psk = Array.Empty<byte>(),
            PrimaryProvider = () => primary,
        };

        Assert.Equal(primary.EffectiveKey, secondary.EffectiveKey);

        // The borrowed key feeds the hash too, so a keyless secondary does not
        // hash as an unencrypted channel would.
        byte nameOnly = 0;
        foreach (var c in "Alta") nameOnly ^= (byte)c;
        Assert.NotEqual(nameOnly, secondary.Hash);
    }

    [Fact]
    public void ShorthandZeroIsNoCryptoEvenOnASecondary()
    {
        var primary = new ChannelConfig { Name = "LongFast", Role = ChannelRole.Primary, Psk = new byte[] { 0x01 } };
        var secondary = new ChannelConfig
        {
            Name = "Open",
            Role = ChannelRole.Secondary,
            Psk = new byte[] { 0x00 },
            PrimaryProvider = () => primary,
        };

        // Firmware checks the inheritance case on a stored length of 0, before
        // it expands the 1-byte shorthand, so 0x00 stays "no crypto".
        Assert.Empty(secondary.EffectiveKey);
    }

    [Fact]
    public void DisabledChannelsAreFlagged()
    {
        Assert.True(new ChannelConfig { Role = ChannelRole.Disabled }.IsDisabled);
        Assert.False(new ChannelConfig { Role = ChannelRole.Secondary }.IsDisabled);
    }

    /// <summary>
    /// The collision that blackholed a channel on firmware develop: a wrong key
    /// can still produce the right one-byte channel hash, because the hash is an
    /// order-insensitive XOR fold. Both keys below fold to 0x33.
    /// </summary>
    [Fact]
    public void DifferentKeysCanShareAChannelHash()
    {
        var wrong = new ChannelConfig { Name = "Alta", Psk = new byte[] { 0x30 } };
        var right = new ChannelConfig
        {
            Name = "Alta",
            Psk = Convert.FromBase64String("kHfEUquaRkCWuaX8Rv2CBAgQvWbcmp0ua03dTNzl4GY="),
        };

        Assert.NotEqual(wrong.EffectiveKey, right.EffectiveKey);
        Assert.Equal(right.Hash, wrong.Hash);
        Assert.Equal(0x0B, wrong.Hash);
    }
}
