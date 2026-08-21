// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using Xunit;

namespace MeshRF.Tests;

public class PositionPrecisionPolicyTests
{
    private static ChannelConfig Channel(byte[] psk, byte precision = 32,
                                         ChannelRole role = ChannelRole.Primary) => new()
    {
        Index = 0,
        Name = "LongFast",
        Psk = psk,
        Role = role,
        PositionPrecision = precision,
    };

    private static byte[] PrivateKey()
    {
        var key = new byte[16];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        return key;
    }

    [Theory]
    [InlineData(0)]   // No key at all.
    [InlineData(1)]   // The 0x01 shorthand for the published default key.
    [InlineData(7)]   // Any other short alias is the same key family.
    public void ShortAndEmptyKeysAreAllPublic(int pskByte)
    {
        var psk = pskByte == 0 ? Array.Empty<byte>() : new[] { (byte)pskByte };
        Assert.True(PositionPrecisionPolicy.UsesPublicKey(Channel(psk)));
    }

    [Fact]
    public void TheFullDefaultKeyIsPublicWhateverItsLastByte()
    {
        var psk = (byte[])ChannelConfig.DefaultPsk.Clone();
        Assert.True(PositionPrecisionPolicy.UsesPublicKey(Channel(psk)));

        // getKey() bumps only the last byte per channel index.
        psk[^1] = (byte)(psk[^1] + 5);
        Assert.True(PositionPrecisionPolicy.UsesPublicKey(Channel(psk)));
    }

    [Fact]
    public void AKeyOfYourOwnIsNotPublic()
    {
        Assert.False(PositionPrecisionPolicy.UsesPublicKey(Channel(PrivateKey())));
    }

    [Fact]
    public void PreciseIsCappedOnAPublicChannel()
    {
        Assert.Equal(PositionPrecisionPolicy.MaxOnPublicKey,
                     PositionPrecisionPolicy.EffectiveFor(Channel(new byte[] { 0x01 }, precision: 32)));
    }

    [Fact]
    public void PreciseSurvivesOnAChannelOnlyYouCanRead()
    {
        Assert.Equal(32, PositionPrecisionPolicy.EffectiveFor(Channel(PrivateKey(), precision: 32)));
    }

    [Fact]
    public void ACoarseSettingIsLeftAloneEitherWay()
    {
        // The cap is a ceiling, not a target: it must never make a position
        // more precise than it was configured to be.
        Assert.Equal(12, PositionPrecisionPolicy.EffectiveFor(Channel(new byte[] { 0x01 }, precision: 12)));
        Assert.Equal(12, PositionPrecisionPolicy.EffectiveFor(Channel(PrivateKey(), precision: 12)));
    }

    [Fact]
    public void SharingOffStaysOff()
    {
        Assert.Equal(0, PositionPrecisionPolicy.EffectiveFor(Channel(PrivateKey(), precision: 0)));
    }

    [Fact]
    public void ADisabledChannelSendsNothing()
    {
        var channel = Channel(PrivateKey(), precision: 32, role: ChannelRole.Disabled);
        Assert.Equal(0, PositionPrecisionPolicy.EffectiveFor(channel));
    }

    [Fact]
    public void ASecondaryWithNoKeyIsJudgedByItsPrimary()
    {
        var publicPrimary = Channel(new byte[] { 0x01 });
        var secondary = Channel(Array.Empty<byte>(), precision: 32, role: ChannelRole.Secondary);
        secondary.PrimaryProvider = () => publicPrimary;
        Assert.True(PositionPrecisionPolicy.UsesPublicKey(secondary));
        Assert.Equal(PositionPrecisionPolicy.MaxOnPublicKey,
                     PositionPrecisionPolicy.EffectiveFor(secondary));

        var privatePrimary = Channel(PrivateKey());
        secondary.PrimaryProvider = () => privatePrimary;
        Assert.False(PositionPrecisionPolicy.UsesPublicKey(secondary));
        Assert.Equal(32, PositionPrecisionPolicy.EffectiveFor(secondary));
    }

    [Fact]
    public void ASecondaryWithNoPrimaryToAskFailsClosed()
    {
        var orphan = Channel(Array.Empty<byte>(), precision: 32, role: ChannelRole.Secondary);
        Assert.True(PositionPrecisionPolicy.UsesPublicKey(orphan));

        // A provider that hands back the channel itself is the same dead end.
        orphan.PrimaryProvider = () => orphan;
        Assert.True(PositionPrecisionPolicy.UsesPublicKey(orphan));
    }

    [Fact]
    public void TheCeilingIsWhatThePickerMayOffer()
    {
        Assert.Equal(PositionPrecisionPolicy.MaxOnPublicKey,
                     PositionPrecisionPolicy.CeilingFor(Channel(new byte[] { 0x01 })));
        Assert.Equal(32, PositionPrecisionPolicy.CeilingFor(Channel(PrivateKey())));
    }
}
