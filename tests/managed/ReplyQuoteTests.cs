// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A reply is drawn as a quote of what it answers, then its own words. The
/// quote names a node, so it is composed when the bubble is drawn rather than
/// folded into the message text when it arrives — baked in, it kept whatever
/// the quoted node was called at the time.
/// </summary>
public class ReplyQuoteTests
{
    private const uint Alice = 0x1111_1111u;

    private static ChannelMessage Reply(string body, ReplyQuote quote, uint replyTo = 0x2222u) => new()
    {
        FromId = "Barn Node",
        SenderNodeNum = 0x3333_3333u,
        Text = body,
        IsReplyLinked = true,
        ReplyTargetFound = quote.HasTarget,
        ReplyToPacketId = replyTo,
        ReplyToSenderNodeNum = quote.SenderNodeNum,
        ReplyToSenderName = quote.SenderName,
        ReplyToPreview = quote.Preview,
    };

    [Fact]
    public void AReplyIsDrawnAsTheQuoteThenItsOwnWords()
    {
        var reply = Reply("count me in", new ReplyQuote(Alice, "!11111111", "field day is on"));

        Assert.Equal("replying to !11111111: \"field day is on\"\ncount me in", reply.DisplayText);
        // The body alone is the message: what the clipboard copies as the text,
        // what a reply to this reply quotes in turn.
        Assert.Equal("count me in", reply.Text);
    }

    [Fact]
    public void TheQuoteFollowsTheQuotedNodesName()
    {
        var reply = Reply("count me in", new ReplyQuote(Alice, "!11111111", "field day is on"));

        reply.ReplyToSenderName = "Rooftop Relay";

        Assert.Equal("replying to Rooftop Relay: \"field day is on\"\ncount me in", reply.DisplayText);
    }

    /// <summary>A reply to something this station never saw has only the
    /// packet id the reply itself carries.</summary>
    [Fact]
    public void AMissingTargetIsNamedByItsPacketId()
    {
        var reply = Reply("count me in", ReplyQuote.None, replyTo: 0x0badf00du);

        Assert.Equal("replying to 0badf00d (original message not found)\ncount me in", reply.DisplayText);
    }

    [Fact]
    public void AnOrdinaryMessageIsDrawnAsItself()
    {
        var plain = new ChannelMessage { FromId = "Barn Node", Text = "field day is on" };

        Assert.Equal(string.Empty, plain.ReplyContext);
        Assert.Equal("field day is on", plain.DisplayText);
    }

    [Fact]
    public void ALongQuoteIsCutToOneReadableLine()
    {
        var quote = ReplyQuote.Of(new ChannelMessage { Text = new string('x', 120) });

        Assert.Equal(new string('x', 80) + "...", quote.Preview);
        // A quoted message spanning lines is flattened: the quote is one line.
        Assert.Equal("two lines", ReplyQuote.PreviewOf("two\nlines"));
        Assert.Equal("(empty)", ReplyQuote.PreviewOf("   "));
    }
}
