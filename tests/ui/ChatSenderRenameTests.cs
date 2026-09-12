// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Waypoints;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// A chat bubble is labelled with the sender's name as the packet arrives, so
/// a node that renames itself — or one that is first heard as a bare id and
/// introduces itself a moment later — used to leave every earlier line under
/// the old label until the tab was rebuilt, showing one node under two names
/// in the conversation on screen.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class ChatSenderRenameTests(HeadlessAvalonia avalonia)
{
    private const uint Us = 0x11111111u;
    private const uint Peer = 0x4A2B3C4Du;

    private static ChannelConfig Channel() => new()
    {
        Index = 0,
        Name = nameof(LoraPreset.MediumFast),
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    private static AvaloniaMeshRxHost Station(NodeStore nodes) =>
        new(nodes, new ChannelStore(), new WaypointStore(), new MessageStore(), Us, [],
            null, primaryList: nameof(LoraPreset.MediumFast));

    /// <summary>Files a frame away exactly as a reception does.</summary>
    private static void Receive(AvaloniaMeshRxHost host, byte[] frame, string text = "", uint replyId = 0)
    {
        var result = MeshDecoder.Decode(frame, [Channel()]);
        Assert.NotNull(result);
        Assert.True(MeshHeader.TryParse(frame, out var header));
        var record = new MessageRecord
        {
            FromNode = Peer,
            Text = text,
            PacketId = header.PacketId,
            ReplyId = replyId,
            RxEpoch = 1_700_000_000,
        };
        host.OnMessageDecoded(frame, header, record, result!,
                              rxEpoch: 1_700_000_000, SignalReading.None, hopsAway: 0,
                              RxSource.Primary(LoraPreset.MediumFast, isCustom: false, freqMHz: 913.125));
    }

    private static void SaysSomething(AvaloniaMeshRxHost host, string text, uint packetId) =>
        Receive(host, MeshEncoder.Encode(Channel(), Peer, 0xFFFFFFFFu, packetId,
                                         PortNum.TextMessage, Encoding.UTF8.GetBytes(text)), text);

    private static void Whispers(AvaloniaMeshRxHost host, string text, uint packetId) =>
        Receive(host, MeshEncoder.Encode(Channel(), Peer, Us, packetId,
                                         PortNum.TextMessage, Encoding.UTF8.GetBytes(text)), text);

    private static void IntroducesItselfAs(AvaloniaMeshRxHost host, string longName, uint packetId)
    {
        var user = new ProtoWriter();
        user.WriteStringField(1, $"!{Peer:x8}");
        user.WriteStringField(2, longName);
        user.WriteStringField(3, longName[..2].ToUpperInvariant());
        Receive(host, MeshEncoder.Encode(Channel(), Peer, 0xFFFFFFFFu, packetId,
                                         PortNum.NodeInfo, user.ToArray()));
    }

    [Fact]
    public void ARenameReachesWhatTheNodeAlreadySaid() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);

        // Heard before it ever said who it was: the bubble can only be the id.
        SaysSomething(host, "anyone about?", 1);
        var bubble = Assert.Single(host.Tabs.OfType<ChannelTabViewModel>().Single().Messages);
        Assert.Equal($"!{Peer:x8}", bubble.FromId);

        // It introduces itself, and what it said earlier is its all the same.
        IntroducesItselfAs(host, "Rooftop Relay", 2);
        Assert.Equal("Rooftop Relay", bubble.FromId);

        // And a later rename moves the whole history onto the new name, with
        // the chat open in front of the operator.
        SaysSomething(host, "moved to the barn", 3);
        IntroducesItselfAs(host, "Barn Relay", 4);
        Assert.All(host.Tabs.OfType<ChannelTabViewModel>().Single().Messages,
                   m => Assert.Equal("Barn Relay", m.FromId));
    }));

    /// <summary>A reply is drawn as a quote of what it answers, and the quote
    /// names the node that wrote it.</summary>
    [Fact]
    public void ARenameReachesTheQuoteInAReplyToWhatItSaid() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);

        SaysSomething(host, "field day is on", 1);
        Receive(host, MeshEncoder.Encode(Channel(), Peer, 0xFFFFFFFFu, 2, PortNum.TextMessage,
                                         Encoding.UTF8.GetBytes("count me in"), replyId: 1),
                "count me in", replyId: 1);

        var reply = host.Tabs.OfType<ChannelTabViewModel>().Single().Messages.Last();
        Assert.True(reply.IsReplyLinked);
        Assert.Equal($"replying to !{Peer:x8}: \"field day is on\"\ncount me in", reply.DisplayText);

        IntroducesItselfAs(host, "Rooftop Relay", 3);

        Assert.Equal("replying to Rooftop Relay: \"field day is on\"\ncount me in", reply.DisplayText);
        // The message itself is the words; the quote is drawn around them.
        Assert.Equal("count me in", reply.Text);
    }));

    /// <summary>A tapback is attributed to whoever left it, and that
    /// attribution is the node's name as well.</summary>
    [Fact]
    public void ARenameReachesTheReactionsItLeft() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);

        SaysSomething(host, "field day is on", 1);
        var bubble = Assert.Single(host.Tabs.OfType<ChannelTabViewModel>().Single().Messages);

        // A tapback on that message, from a node that has not said who it is.
        Receive(host, MeshEncoder.Encode(Channel(), Peer, 0xFFFFFFFFu, 2, PortNum.TextMessage,
                                         Encoding.UTF8.GetBytes("👍"), replyId: 1, emoji: 1));
        var reaction = Assert.Single(bubble.Reactions);
        Assert.Equal(1, reaction.Count);
        Assert.Equal($"!{Peer:x8}", reaction.Reactors);

        IntroducesItselfAs(host, "Rooftop Relay", 3);

        // Relabelled, and still one reactor: the same node under a new name is
        // not a second person who liked it.
        Assert.Equal("Rooftop Relay", reaction.Reactors);
        Assert.Equal(1, reaction.Count);
    }));

    /// <summary>The label follows the node, not the tab that happens to be on
    /// show: a direct message is in a tab of its own, and nobody would think to
    /// go and look at it.</summary>
    [Fact]
    public void ARenameReachesEveryTabAtOnce() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);

        SaysSomething(host, "on the channel", 1);
        // The first direct message opens the conversation; the bubble for it
        // comes from the stored history, so the one to watch is the second.
        Whispers(host, "are you there?", 2);
        Whispers(host, "and in private", 3);

        IntroducesItselfAs(host, "Rooftop Relay", 4);

        Assert.All(host.Tabs.SelectMany(t => t.Messages).Where(m => m.SenderNodeNum == Peer),
                   m => Assert.Equal("Rooftop Relay", m.FromId));
        // Both tabs really were carrying one of its lines.
        Assert.Equal(2, host.Tabs.SelectMany(t => t.Messages).Count(m => m.SenderNodeNum == Peer));
        // The conversation's own header follows it too.
        Assert.Contains(host.Tabs.OfType<ConversationTabViewModel>(), c => c.PeerName == "Rooftop Relay");
    }));
}
