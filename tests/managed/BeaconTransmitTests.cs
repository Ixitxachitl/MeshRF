// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// What this station puts on the air when it beacons. The decoder in this same
/// assembly is what reads other people's beacons, so encoding through one and
/// reading back through the other is the closest thing to a loopback with a
/// real node there is.
/// </summary>
public class BeaconTransmitTests
{
    private static readonly byte[] SharedKey =
    {
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00,
    };

    private static ChannelConfig Channel(string name, byte[]? psk = null) => new()
    {
        Index = 0,
        Name = name,
        Psk = psk ?? new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    private static MeshBeacon Decode(byte[] frame, ChannelConfig channel)
    {
        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(PortNum.MeshBeacon, result!.Port);
        Assert.NotNull(result.Beacon);
        return result.Beacon!;
    }

    [Fact]
    public void AMessageAndAnOfferSurviveTheRoundTrip()
    {
        var sealedWith = Channel("MediumFast");
        var offered = new ChannelConfig { Index = 3, Name = "Ranger", Psk = SharedKey, Role = ChannelRole.Secondary };

        var frame = MeshEncoder.EncodeBeacon(sealedWith, from: 0x4FA54F59u, packetId: 0x11223344u,
            message: "Ranger net, Tuesdays 19:00",
            offerChannel: offered, offerPreset: LoraPreset.MediumFast, offerRegion: Region.US);

        var beacon = Decode(frame, sealedWith);

        Assert.Equal("Ranger net, Tuesdays 19:00", beacon.Message);
        Assert.True(beacon.HasChannel);
        Assert.Equal("Ranger", beacon.ChannelName);
        Assert.Equal(SharedKey, beacon.ChannelPsk);
        Assert.Equal(LoraPreset.MediumFast, beacon.Preset);
        Assert.Equal(Region.US, beacon.Region);
    }

    /// <summary>
    /// The protobuf numbers its presets in its own order — LONG_FAST is 0
    /// there and LongFast is 6 in this app's enum — so a cast would advertise
    /// an entirely different mesh. Every preset the schema knows has to come
    /// back as itself.
    /// </summary>
    [Theory]
    [InlineData(LoraPreset.LongFast)]
    [InlineData(LoraPreset.LongSlow)]
    [InlineData(LoraPreset.MediumSlow)]
    [InlineData(LoraPreset.MediumFast)]
    [InlineData(LoraPreset.ShortSlow)]
    [InlineData(LoraPreset.ShortFast)]
    [InlineData(LoraPreset.LongModerate)]
    [InlineData(LoraPreset.ShortTurbo)]
    [InlineData(LoraPreset.LongTurbo)]
    [InlineData(LoraPreset.LiteFast)]
    [InlineData(LoraPreset.LiteSlow)]
    [InlineData(LoraPreset.NarrowFast)]
    [InlineData(LoraPreset.NarrowSlow)]
    [InlineData(LoraPreset.TinyFast)]
    [InlineData(LoraPreset.TinySlow)]
    [InlineData(LoraPreset.MediumTurbo)]
    public void EveryPresetTheSchemaKnowsComesBackAsItself(LoraPreset preset)
    {
        var channel = Channel("MediumFast");
        var frame = MeshEncoder.EncodeBeacon(channel, 1, 2, "hi", offerPreset: preset);

        Assert.Equal(preset, Decode(frame, channel).Preset);
    }

    /// <summary>
    /// Every preset this app has is one the schema has a number for, so none of
    /// them is ever dropped from an offer. The theory above is the guard: a
    /// preset added here before the schema gains it would fail it rather than
    /// quietly advertising a different mesh.
    /// </summary>
    [Fact]
    public void NoPresetIsLeftOutOfAnOffer()
    {
        var channel = Channel("MediumFast");
        foreach (var preset in Enum.GetValues<LoraPreset>())
        {
            var beacon = Decode(MeshEncoder.EncodeBeacon(channel, 1, 2, "hi", offerPreset: preset), channel);
            Assert.Equal(preset, beacon.Preset);
        }
    }

    /// <summary>Firmware caps the message at 100 bytes of UTF-8. The cap is in
    /// the encoder so no path can put an over-long one on the air, and it cuts
    /// on a character boundary rather than mid-rune.</summary>
    [Fact]
    public void AnOverLongMessageIsCutToTheByteCapWithoutSplittingACharacter()
    {
        var channel = Channel("MediumFast");
        // Four bytes each, so 30 of them is 120 bytes: past the cap, and the
        // cut lands where a naive byte slice would split one in half.
        var message = string.Concat(Enumerable.Repeat("🍄", 30));

        var beacon = Decode(MeshEncoder.EncodeBeacon(channel, 1, 2, message), channel);

        Assert.Equal(25, beacon.Message.Length / 2);   // 25 surrogate pairs
        Assert.Equal(100, BeaconPolicy.MessageBytes(beacon.Message));
        Assert.DoesNotContain('�', beacon.Message);
    }

    /// <summary>An advertisement that could be reflooded across the mesh it is
    /// announcing itself to would be spam, so firmware sends every beacon
    /// zero-hop and unacknowledged.</summary>
    [Fact]
    public void ABeaconIsZeroHopAndWantsNoAck()
    {
        var channel = Channel("MediumFast");
        var frame = MeshEncoder.EncodeBeacon(channel, 1, 2, "hi");

        Assert.True(MeshHeader.TryParse(frame, out var header));
        Assert.Equal(0, header.HopLimit);
        Assert.False(header.WantAck);
        Assert.True(header.IsBroadcast);
    }

    /// <summary>
    /// A blank channel name is a real Meshtastic name meaning "called after the
    /// preset". It goes out absent rather than as an empty string, which is
    /// what an unnamed channel looks like on the air — and the receiver has to
    /// reproduce the channel byte for byte to match its hash.
    /// </summary>
    [Fact]
    public void AnUnnamedOfferedChannelGoesOutUnnamed()
    {
        var channel = Channel("MediumFast");
        var offered = new ChannelConfig { Index = 1, Name = string.Empty, Psk = SharedKey };

        var beacon = Decode(MeshEncoder.EncodeBeacon(channel, 1, 2, null, offerChannel: offered), channel);

        Assert.True(beacon.HasChannel);
        Assert.Equal(string.Empty, beacon.ChannelName);
        Assert.Equal(SharedKey, beacon.ChannelPsk);
    }

    /// <summary>An offer-only beacon carries no text at all, which is a thing
    /// firmware sends and this has to be able to read back.</summary>
    [Fact]
    public void AnOfferWithNoWordsIsStillABeacon()
    {
        var channel = Channel("MediumFast");
        var offered = new ChannelConfig { Index = 1, Name = "Ranger", Psk = SharedKey };

        var beacon = Decode(MeshEncoder.EncodeBeacon(channel, 1, 2, null, offerChannel: offered), channel);

        Assert.Equal(string.Empty, beacon.Message);
        Assert.True(beacon.HasOffer);
    }
}
