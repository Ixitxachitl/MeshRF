// SPDX-License-Identifier: GPL-3.0-or-later
using Google.Protobuf;
using MeshRF.Channels;
using MeshRF.Mesh;
using Meshtastic.Protobufs;
using Xunit;
using ProtoModemPreset = Meshtastic.Protobufs.Config.Types.LoRaConfig.Types.ModemPreset;
using ProtoRegionCode = Meshtastic.Protobufs.Config.Types.LoRaConfig.Types.RegionCode;

namespace MeshRF.Tests;

/// <summary>
/// A node in beacon mode broadcasts MESH_BEACON_APP: something to read, and
/// optionally the mesh it is spoken on — a channel's name and key, a modem
/// preset, a region. The offer is an invitation. Firmware caches it and
/// applies none of it, and neither does MeshRF: it resolves the offer against
/// the channels already held and, when the mesh is genuinely new, says so.
/// </summary>
public class BeaconOfferTests
{
    private static readonly byte[] SharedKey =
    {
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00,
    };

    private static byte[] Encode(Meshtastic.Protobufs.MeshBeacon beacon) => beacon.ToByteArray();

    private static Meshtastic.Protobufs.MeshBeacon Advertisement(string name, byte[] psk) => new()
    {
        Message = "Ranger net, Tuesdays 19:00",
        OfferChannel = new ChannelSettings { Name = name, Psk = ByteString.CopyFrom(psk) },
        OfferPreset = ProtoModemPreset.MediumFast,
        OfferRegion = ProtoRegionCode.Us,
    };

    private static ChannelConfig Channel(string preset, string name, byte[] psk, int index = 0) => new()
    {
        Preset = preset,
        Index = index,
        Name = name,
        Psk = psk,
        Role = index == 0 ? ChannelRole.Primary : ChannelRole.Secondary,
    };

    [Fact]
    public void TheWireCarriesTheChannelPresetAndRegion()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)));

        Assert.NotNull(beacon);
        Assert.Equal("Ranger net, Tuesdays 19:00", beacon!.Message);
        Assert.True(beacon.HasChannel);
        Assert.Equal("Ranger", beacon.ChannelName);
        Assert.Equal(SharedKey, beacon.ChannelPsk);
        Assert.Equal(LoraPreset.MediumFast, beacon.Preset);
        Assert.Equal(Region.US, beacon.Region);
        Assert.True(beacon.HasOffer);
    }

    /// <summary>A beacon that only says something is still a message. It has
    /// nothing to offer, so nothing is offered.</summary>
    [Fact]
    public void AMessageWithNoOfferAdvertisesNothing()
    {
        var beacon = MeshDecoder.ParseBeacon(
            Encode(new Meshtastic.Protobufs.MeshBeacon { Message = "hi" }));

        Assert.NotNull(beacon);
        Assert.False(beacon!.HasOffer);
        Assert.False(beacon.HasChannel);
        Assert.Null(beacon.Preset);
        Assert.Equal(Region.UNSET, beacon.Region);
    }

    [Fact]
    public void AChannelNotHeldYetIsOfferedWithWhatItIs()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)))!;
        var held = new[] { Channel(nameof(LoraPreset.MediumFast), "MediumFast", new byte[] { 0x01 }) };

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", held);

        Assert.True(offer.CanAdd);
        Assert.Equal("Ranger", offer.ChannelName);
        Assert.Equal(SharedKey, offer.Psk);
        Assert.Contains("“Ranger”", offer.Summary);
        Assert.Contains("shared key", offer.Summary);
        Assert.Contains("preset MediumFast", offer.Summary);
        Assert.Contains("region US", offer.Summary);
    }

    /// <summary>
    /// Identity on the air is the name and the key together — the two things
    /// the channel hash is taken over. Holding both is holding the channel.
    /// </summary>
    [Fact]
    public void AChannelAlreadyHeldOnThatMeshIsNotOfferedAgain()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)))!;
        var held = new[]
        {
            Channel(nameof(LoraPreset.MediumFast), "MediumFast", new byte[] { 0x01 }),
            Channel(nameof(LoraPreset.MediumFast), "Ranger", SharedKey, index: 1),
        };

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", held);

        Assert.False(offer.CanAdd);
        Assert.Equal("Already on MediumFast.", offer.StatusText);
        Assert.True(offer.HasStatus);
    }

    /// <summary>The same key under another name is another channel — firmware
    /// hashes the name, so the two do not even collide on the air.</summary>
    [Fact]
    public void TheSameKeyUnderAnotherNameIsAnotherChannel()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)))!;
        var held = new[] { Channel(nameof(LoraPreset.MediumFast), "Rangers", SharedKey) };

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", held);

        Assert.True(offer.CanAdd);
    }

    /// <summary>
    /// The same name on another key is a different channel on the air, but two
    /// tabs of one name are indistinguishable once drawn — so it is not added
    /// silently. Adding it anyway is what produced two "Alta" tabs.
    /// </summary>
    [Fact]
    public void TheSameNameOnAnotherKeyIsNotAddedSilently()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)))!;
        var held = new[] { Channel(nameof(LoraPreset.MediumFast), "Ranger", ChannelConfig.NewRandomPsk()) };

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", held);

        Assert.False(offer.CanAdd);
        Assert.Contains("already has a channel called “Ranger”", offer.StatusText);
        Assert.Contains("different key", offer.StatusText);
    }

    /// <summary>
    /// The Alta beacon as it actually arrived: a 32-byte PSK, no message,
    /// region US, preset MediumFast. A station already holding that channel is
    /// not offered it a second time.
    /// </summary>
    [Fact]
    public void TheAltaBeaconOffAirIsRecognisedAgainstAHeldAlta()
    {
        var payload = Convert.FromHexString(
            "122812209077C452AB9A464096B9A5FC46FD82040810BD66DC9A9D2E6B4DDD4CDCE5E0661A04416C746118012004");
        var beacon = MeshDecoder.ParseBeacon(payload);

        Assert.NotNull(beacon);
        Assert.Equal(string.Empty, beacon!.Message);
        Assert.Equal("Alta", beacon.ChannelName);
        Assert.Equal(32, beacon.ChannelPsk.Length);
        Assert.Equal(LoraPreset.MediumFast, beacon.Preset);
        Assert.Equal(Region.US, beacon.Region);

        // Held on the primary's own list, which is where a station whose
        // toolbar sits on MediumFast keeps that mesh's channels.
        var held = new[]
        {
            Channel(string.Empty, "MediumFast", new byte[] { 0x01 }),
            Channel(string.Empty, "Alta", beacon.ChannelPsk, index: 16),
        };

        var offer = BeaconOffer.For(beacon, string.Empty, "Primary", held);

        Assert.False(offer.CanAdd);
        Assert.Equal("Already on Primary.", offer.StatusText);
    }

    /// <summary>Held on another mesh is not held on this one: the caller
    /// passes only the channels on the mesh being offered, and a "Ranger" over
    /// on LongFast is somebody else's.</summary>
    [Fact]
    public void AChannelHeldOnAnotherMeshDoesNotCount()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)))!;
        var mediumFast = new[] { Channel(nameof(LoraPreset.MediumFast), "MediumFast", new byte[] { 0x01 }) };

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", mediumFast);

        Assert.True(offer.CanAdd);
        Assert.Equal(nameof(LoraPreset.MediumFast), offer.ListName);
    }

    /// <summary>A disabled channel matches no packet on the air, so it is not
    /// what an offer would be a duplicate of.</summary>
    [Fact]
    public void ADisabledChannelDoesNotCountAsHolding()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)))!;
        var held = new[] { Channel(nameof(LoraPreset.MediumFast), "Ranger", SharedKey) };
        held[0].Role = ChannelRole.Disabled;

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", held);

        Assert.True(offer.CanAdd);
    }

    /// <summary>A preset and a region with no channel say a mesh is out there
    /// and leave nothing to add.</summary>
    [Fact]
    public void APresetWithoutAChannelHasNothingToAdd()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(new Meshtastic.Protobufs.MeshBeacon
        {
            Message = "we are over here",
            OfferPreset = ProtoModemPreset.LongModerate,
            OfferRegion = ProtoRegionCode.Us,
        }))!;

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.LongModerate), "LongModerate", []);

        Assert.False(offer.CanAdd);
        Assert.True(beacon.HasOffer);
        Assert.Equal("Advertises preset LongModerate · region US", offer.Summary);
    }

    /// <summary>Nothing arrives on an added channel until the receiver is
    /// listening for its preset, so the note says so alongside the button.</summary>
    [Fact]
    public void ANoteRidesAlongWithAnOfferThatIsStillAddable()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)))!;

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", [],
                                    "Not listening for MediumFast — tick it in Monitors to hear this mesh.");

        Assert.True(offer.CanAdd);
        Assert.Contains("Monitors", offer.StatusText);
    }

    /// <summary>A blank channel name is a real Meshtastic name meaning "called
    /// after the preset". It is stored as sent — the sender hashed blank — and
    /// only filled in for the reader.</summary>
    [Fact]
    public void AnUnnamedChannelIsShownAsThePresetButStoredBlank()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(new Meshtastic.Protobufs.MeshBeacon
        {
            OfferChannel = new ChannelSettings { Psk = ByteString.CopyFrom(new byte[] { 0x01 }) },
            OfferPreset = ProtoModemPreset.MediumFast,
        }))!;

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", []);

        Assert.Equal(string.Empty, offer.ChannelName);
        Assert.Equal(string.Empty, offer.ToChannel(1).Name);
        Assert.Contains("channel “MediumFast” (default key)", offer.Summary);
    }

    /// <summary>
    /// A blank PSK is neither "no key" nor the default key. Firmware
    /// <c>Channels::getKey</c> hands a keyless secondary its primary's key, so
    /// on a mesh that has a primary the offer is as private as that channel
    /// already is — and the offer says so rather than calling it unencrypted.
    /// </summary>
    [Fact]
    public void ABlankKeyMeansTheMeshsOwnKeyWhereThereIsOneToBorrow()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(new Meshtastic.Protobufs.MeshBeacon
        {
            OfferChannel = new ChannelSettings { Name = "LongFast" },
            OfferRegion = ProtoRegionCode.Us,
        }))!;
        Assert.Empty(beacon.ChannelPsk);

        var mesh = new[] { Channel(nameof(LoraPreset.MediumFast), "MediumFast", SharedKey) };
        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", mesh);

        Assert.Contains("uses this mesh's", offer.Summary);
        Assert.DoesNotContain("unencrypted", offer.Summary);
        Assert.True(offer.CanAdd);
    }

    /// <summary>With no primary to borrow from there is nothing to inherit, and
    /// then a blank key really does mean in clear — which is what firmware
    /// warns about as "User disabled encryption".</summary>
    [Fact]
    public void ABlankKeyWithNothingToBorrowFromIsInClear()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(new Meshtastic.Protobufs.MeshBeacon
        {
            OfferChannel = new ChannelSettings { Name = "LongFast" },
        }))!;

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.LongFast), "LongFast", []);

        Assert.Contains("in clear", offer.Summary);
    }

    /// <summary>
    /// And it is recognised against a channel already held on that mesh with
    /// the same blank key. Resolving the offered key standalone made it look
    /// unencrypted, so it matched nothing and offered a duplicate.
    /// </summary>
    [Fact]
    public void ABlankKeyMatchesTheKeylessChannelAlreadyHeld()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(new Meshtastic.Protobufs.MeshBeacon
        {
            OfferChannel = new ChannelSettings { Name = "club" },
        }))!;

        var primary = Channel(nameof(LoraPreset.MediumFast), "MediumFast", SharedKey);
        var keyless = new ChannelConfig
        {
            Preset = nameof(LoraPreset.MediumFast),
            Index = 1,
            Name = "club",
            Psk = [],
            Role = ChannelRole.Secondary,
            PrimaryProvider = () => primary,
        };

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast",
                                    new[] { primary, keyless });

        Assert.False(offer.CanAdd);
        Assert.Equal("Already on MediumFast.", offer.StatusText);
    }

    /// <summary>The single byte 1 is the default key — the shorthand a stock
    /// channel carries, and what the Default button writes as AQ==. Distinct
    /// from a blank key, which carries no key at all.</summary>
    [Fact]
    public void TheDefaultKeyIsTheOneByteShorthandNotABlankOne()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(new Meshtastic.Protobufs.MeshBeacon
        {
            OfferChannel = new ChannelSettings
            {
                Name = "LongFast",
                Psk = ByteString.CopyFrom(new byte[] { 0x01 }),
            },
        }))!;

        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.LongFast), "LongFast", []);

        Assert.Contains("default key", offer.Summary);
    }

    /// <summary>The key came off the air, so the channel it becomes starts out
    /// withholding position — the same footing as one added by hand.</summary>
    [Fact]
    public void AnAcceptedChannelWithholdsPosition()
    {
        var beacon = MeshDecoder.ParseBeacon(Encode(Advertisement("Ranger", SharedKey)))!;
        var offer = BeaconOffer.For(beacon, nameof(LoraPreset.MediumFast), "MediumFast", []);

        var channel = offer.ToChannel(index: 2);

        Assert.Equal(nameof(LoraPreset.MediumFast), channel.Preset);
        Assert.Equal(2, channel.Index);
        Assert.Equal("Ranger", channel.Name);
        Assert.Equal(SharedKey, channel.Psk);
        Assert.Equal(ChannelRole.Secondary, channel.Role);
        Assert.Equal(0, channel.PositionPrecision);
        // A copy, so editing the channel afterwards cannot rewrite the offer.
        Assert.NotSame(offer.Psk, channel.Psk);
    }
}
