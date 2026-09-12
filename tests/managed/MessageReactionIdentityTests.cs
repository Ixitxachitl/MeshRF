// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A tapback belongs to a node, not to a name. Counting reactors by the name
/// they happened to be showing merged two nodes that call themselves the same
/// thing into one reactor, and split one node that renamed itself into two.
/// </summary>
public class MessageReactionIdentityTests
{
    private const uint Alice = 0x1111_1111u;
    private const uint Bob = 0x2222_2222u;

    [Fact]
    public void TwoNodesSharingANameAreTwoReactors()
    {
        var message = new ChannelMessage { Text = "field day is on" };
        message.AddReaction("👍", Alice, "Meshtastic");
        message.AddReaction("👍", Bob, "Meshtastic");

        var reaction = Assert.Single(message.Reactions);
        Assert.Equal(2, reaction.Count);
        // One name, said once: the tooltip cannot tell them apart, and there is
        // nothing better to call them. The count is what carries the truth.
        Assert.Equal("Meshtastic, Meshtastic", reaction.Reactors);
    }

    [Fact]
    public void OneNodeUnderTwoNamesIsStillOneReactor()
    {
        var message = new ChannelMessage { Text = "field day is on" };
        message.AddReaction("👍", Alice, "!11111111");
        message.AddReaction("👍", Alice, "Rooftop Relay");

        var reaction = Assert.Single(message.Reactions);
        Assert.Equal(1, reaction.Count);
        Assert.Equal("Rooftop Relay", reaction.Reactors);
    }

    /// <summary>The same question the UI asks before sending a tapback, so a
    /// rename must not let the same node react twice.</summary>
    [Fact]
    public void AlreadyReactedFollowsTheNodeThroughARename()
    {
        var message = new ChannelMessage { Text = "field day is on" };
        message.AddReaction("👍", Alice, "!11111111");

        Assert.True(message.HasReactionFrom("👍", Alice));
        Assert.False(message.HasReactionFrom("👍", Bob));
        Assert.False(message.HasReactionFrom("🎉", Alice));

        message.RelabelReactor(Alice, "Rooftop Relay");
        Assert.True(message.HasReactionFrom("👍", Alice));
    }

    [Fact]
    public void ARenameRelabelsEveryTooltipTheNodeAppearsIn()
    {
        var message = new ChannelMessage { Text = "field day is on" };
        message.AddReaction("👍", Alice, "!11111111");
        message.AddReaction("👍", Bob, "Barn Node");
        message.AddReaction("🎉", Alice, "!11111111");

        message.RelabelReactor(Alice, "Rooftop Relay");

        var thumbs = message.Reactions.Single(r => r.Emoji == "👍");
        var party = message.Reactions.Single(r => r.Emoji == "🎉");
        Assert.Equal(2, thumbs.Count);
        Assert.Equal("Barn Node, Rooftop Relay", thumbs.Reactors);
        Assert.Equal("Rooftop Relay", party.Reactors);

        // A node that never reacted here has nothing to relabel.
        message.RelabelReactor(0x3333_3333u, "Somebody Else");
        Assert.Equal("Barn Node, Rooftop Relay", thumbs.Reactors);
    }
}
