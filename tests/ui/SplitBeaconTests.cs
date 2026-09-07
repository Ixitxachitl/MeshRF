// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using MeshRF;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Firmware's FLAG_LEGACY_SPLIT sends a beacon carrying both words and an
/// invitation as two packets — the offer alone on MESH_BEACON_APP and the
/// words alone on TEXT_MESSAGE_APP — so nodes that decode only text still get
/// the words. Drawn as they arrive that is two bubbles for one beacon, with
/// the invitation orphaned from the sentence explaining it.
/// </summary>
public class SplitBeaconTests
{
    private const uint Sender = 0x45a1a609u;

    private static BeaconOffer Offer() => BeaconOffer.For(
        new MeshBeacon { HasChannel = true, ChannelName = "Alta", ChannelPsk = [0x01] },
        "MediumFast", "MediumFast", Array.Empty<ChannelConfig>());

    private static ChannelMessage Words(string text, DateTime at, uint from = Sender) => new()
    {
        Timestamp = at,
        FromId = "Alta Gateway",
        SenderNodeNum = from,
        Text = text,
        PacketId = 0x1111u,
    };

    private static ChannelMessage OfferOnly(DateTime at, uint from = Sender) => new()
    {
        Timestamp = at,
        FromId = "Alta Gateway",
        SenderNodeNum = from,
        Text = string.Empty,
        IsBeacon = true,
        Offer = Offer(),
        PacketId = 0x2222u,
    };

    private static ObservableCollection<ChannelMessage> Tab(params ChannelMessage[] messages) => new(messages);

    /// <summary>Firmware sends the offer first, then the words.</summary>
    [Fact]
    public void TheWordsJoinAnInvitationAlreadyShown()
    {
        var at = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Local);
        var messages = Tab(OfferOnly(at));

        Assert.True(AvaloniaMeshRxHost.TryCombineSplitBeacon(messages, Words("Welcome to Alta", at.AddSeconds(3))));

        var one = Assert.Single(messages);
        Assert.Equal("Welcome to Alta", one.Text);
        Assert.True(one.HasOffer);
        Assert.True(one.IsBeacon);
        // Kept where the reader last saw it, and targetable by a reply.
        Assert.Equal(at, one.Timestamp);
        Assert.Equal(0x1111u, one.PacketId);
    }

    /// <summary>And the other way round, since either may be the one heard.</summary>
    [Fact]
    public void AnInvitationJoinsWordsAlreadyShown()
    {
        var at = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Local);
        var messages = Tab(Words("Welcome to Alta", at));

        Assert.True(AvaloniaMeshRxHost.TryCombineSplitBeacon(messages, OfferOnly(at.AddSeconds(3))));

        var one = Assert.Single(messages);
        Assert.Equal("Welcome to Alta", one.Text);
        Assert.True(one.HasOffer);
        Assert.Equal(at, one.Timestamp);
    }

    /// <summary>A beacon that carried both halves in one packet is already
    /// whole and merges with nothing.</summary>
    [Fact]
    public void AWholeBeaconIsLeftAlone()
    {
        var at = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Local);
        var messages = Tab(Words("earlier chatter", at));

        var whole = new ChannelMessage
        {
            Timestamp = at.AddSeconds(3),
            SenderNodeNum = Sender,
            Text = "🍄",
            IsBeacon = true,
            Offer = Offer(),
        };

        Assert.False(AvaloniaMeshRxHost.TryCombineSplitBeacon(messages, whole));
        Assert.Single(messages);
    }

    /// <summary>Somebody else talking at the same moment is not the other half
    /// of this beacon.</summary>
    [Fact]
    public void AnotherSendersWordsAreNotTheOtherHalf()
    {
        var at = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Local);
        var messages = Tab(OfferOnly(at));

        Assert.False(AvaloniaMeshRxHost.TryCombineSplitBeacon(
            messages, Words("unrelated", at.AddSeconds(2), from: 0xdeadbeefu)));
        Assert.Single(messages);
    }

    /// <summary>The two halves arrive together. An hour later is a different
    /// thing the same node happened to say.</summary>
    [Fact]
    public void WordsLongAfterwardsStandOnTheirOwn()
    {
        var at = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Local);
        var messages = Tab(OfferOnly(at));

        Assert.False(AvaloniaMeshRxHost.TryCombineSplitBeacon(messages, Words("much later", at.AddHours(1))));
        Assert.Single(messages);
    }

    /// <summary>Only the nearest few are considered: an invitation must not
    /// reach back past a conversation to find words.</summary>
    [Fact]
    public void AnInvitationDoesNotReachBackPastAConversation()
    {
        var at = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Local);
        var messages = Tab(Words("Welcome to Alta", at));
        for (int i = 1; i <= 10; i++)
            messages.Add(Words($"chatter {i}", at.AddSeconds(i), from: 0xdeadbeefu));

        Assert.False(AvaloniaMeshRxHost.TryCombineSplitBeacon(messages, OfferOnly(at.AddSeconds(11))));
        Assert.Equal(11, messages.Count);
    }

    /// <summary>A wordless invitation with nobody to pair with stays a bubble
    /// of its own — the card is the whole of it.</summary>
    [Fact]
    public void AnInvitationOnItsOwnIsStillABubble()
    {
        var messages = Tab();
        var offer = OfferOnly(DateTime.Now);

        Assert.False(AvaloniaMeshRxHost.TryCombineSplitBeacon(messages, offer));
        Assert.False(offer.HasText);
        Assert.True(offer.HasOffer);
    }
}
