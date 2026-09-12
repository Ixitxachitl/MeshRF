// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Some senders are bots relaying a web page and hand over its HTML entities
/// with it: real traffic here carries "40.224&amp;deg;N" and "Kittens
/// &amp;amp; cats". A bubble resolves them, since what the reader is meant to
/// see is what the page said. Whitespace is left exactly as it arrived.
/// </summary>
public class MessageEntityDecodeTests
{
    private static string Drawn(string wire) => new ChannelMessage { Text = wire }.DisplayText;

    [Fact]
    public void EntitiesAreResolved()
    {
        Assert.Equal("40.224°N 124.175°W", Drawn("40.224&deg;N 124.175&deg;W"));
        Assert.Equal("Kittens & cats at Petco", Drawn("Kittens &amp; cats at Petco"));
        Assert.Equal("it’s 21°C", Drawn("it&rsquo;s 21&deg;C"));
    }

    /// <summary>Decoded into text, never into markup: the result is drawn in a
    /// text run, so an angle bracket is only ever a character.</summary>
    [Fact]
    public void DecodedMarkupStaysText() =>
        Assert.Equal("<b>not bold</b>", Drawn("&lt;b&gt;not bold&lt;/b&gt;"));

    /// <summary>An ampersand that names no entity is an ampersand.</summary>
    [Fact]
    public void APlainAmpersandIsLeftAlone() =>
        Assert.Equal("R&D & more", Drawn("R&D & more"));

    /// <summary>
    /// The blank lines a sender laid a report out with are theirs, trailing
    /// ones included: collapsing or trimming them would be this app rewriting
    /// somebody else's message.
    /// </summary>
    [Fact]
    public void WhitespaceIsLeftExactlyAsItArrived()
    {
        const string wire = "(1/1) Earthquake: M 2.8\n\n\nLocation\n\n40.224&deg;N\n\n\n";
        Assert.Equal("(1/1) Earthquake: M 2.8\n\n\nLocation\n\n40.224°N\n\n\n", Drawn(wire));
        Assert.Equal("Heard ya in 7 hops\n", Drawn("Heard ya in 7 hops\n"));
    }

    /// <summary>A quote of a message reads like the message it points at.</summary>
    [Fact]
    public void AQuotePreviewReadsLikeTheBubble() =>
        Assert.Equal("Kittens & cats at Petco", ReplyQuote.PreviewOf("Kittens &amp; cats at Petco"));

    /// <summary>The clipboard copies what was on screen.</summary>
    [Fact]
    public void TheCopiedLineCarriesTheDecodedWords() =>
        Assert.Contains("40.224°N", new ChannelMessage { FromId = "bot", Text = "40.224&deg;N" }.Display);
}
