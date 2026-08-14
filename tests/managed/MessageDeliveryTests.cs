// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The two-stage delivery state for outgoing messages: a DM reaches the mesh
/// (a neighbour was heard relaying it) before it reaches the recipient (their
/// ACK came back), and the UI distinguishes the two.
/// </summary>
public class MessageDeliveryTests
{
    /// <summary>
    /// MessageStore persists <c>(int)Delivery</c>, so these numbers are a
    /// storage format, not an implementation detail. Reordering the enum — the
    /// obvious tidy-up, since DeliveredToMesh belongs after Sent — would
    /// silently re-label every outgoing message already on disk.
    /// </summary>
    [Fact]
    public void PersistedDeliveryValuesAreStable()
    {
        Assert.Equal(0, (int)MessageDelivery.None);
        Assert.Equal(1, (int)MessageDelivery.Sent);
        Assert.Equal(2, (int)MessageDelivery.Delivered);
        Assert.Equal(3, (int)MessageDelivery.Failed);
        Assert.Equal(4, (int)MessageDelivery.DeliveredToMesh);
    }

    /// <summary>Both delivery stages share the check on purpose — colour is
    /// what separates them — and the states with nothing to report draw
    /// nothing rather than putting a mark on every outgoing line.</summary>
    [Theory]
    [InlineData(MessageDelivery.None, "")]
    [InlineData(MessageDelivery.Sent, "")]
    [InlineData(MessageDelivery.DeliveredToMesh, "✓")]
    [InlineData(MessageDelivery.Delivered, "✓")]
    [InlineData(MessageDelivery.Failed, "✗")]
    public void GlyphMatchesDeliveryState(MessageDelivery delivery, string expected)
    {
        var message = new ChannelMessage { Text = "hi", IsOutgoing = true, Delivery = delivery };
        Assert.Equal(expected, message.DeliveryGlyph);
    }

    /// <summary>The glyph is a bound property, so a bubble already on screen
    /// has to repaint when an ACK arrives rather than keeping its first mark.</summary>
    [Fact]
    public void GlyphRaisesChangeNotification()
    {
        var message = new ChannelMessage { Text = "hi", IsOutgoing = true, Delivery = MessageDelivery.Sent };
        var raised = new List<string?>();
        message.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        message.Delivery = MessageDelivery.DeliveredToMesh;
        Assert.Contains(nameof(ChannelMessage.DeliveryGlyph), raised);

        raised.Clear();
        message.Delivery = MessageDelivery.Delivered;
        Assert.Contains(nameof(ChannelMessage.DeliveryGlyph), raised);
    }

    /// <summary>Clipboard copy has no colour to work with, so both stages
    /// collapse to the same check there.</summary>
    [Fact]
    public void ClipboardLineCarriesTheMark()
    {
        var message = new ChannelMessage { Text = "hi", IsOutgoing = true, Delivery = MessageDelivery.DeliveredToMesh };
        Assert.EndsWith("hi  ✓", message.Display);

        message.Delivery = MessageDelivery.Delivered;
        Assert.EndsWith("hi  ✓", message.Display);

        message.Delivery = MessageDelivery.Sent;
        Assert.EndsWith("hi", message.Display);
    }
}
