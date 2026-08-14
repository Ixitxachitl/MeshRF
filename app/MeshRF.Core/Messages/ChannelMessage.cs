// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeshRF;

/// <summary>One rendered chat bubble in a channel or DM conversation view.</summary>
public partial class ChannelMessage : ObservableObject
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    [ObservableProperty]
    private string _fromId = string.Empty;

    /// <summary>Sender node id for display-name refresh (0 when unknown/system).</summary>
    public uint SenderNodeNum { get; init; }

    public string Text    { get; init; } = string.Empty;
    public float? RssiDbm { get; init; }
    public float? SnrDb   { get; init; }

    /// <summary>Packet id of this message (for matching ACKs). 0 = unknown.</summary>
    public uint PacketId { get; init; }

    /// <summary>True for messages we transmitted (so delivery status applies).</summary>
    public bool IsOutgoing { get; init; }

    /// <summary>True when the sender is marked ignored in the node list.</summary>
    public bool IsIgnoredSender { get; init; }

    /// <summary>True when this message references an earlier packet via reply_id.</summary>
    public bool IsReplyLinked { get; init; }

    /// <summary>True when the referenced reply target existed in the local view.</summary>
    public bool ReplyTargetFound { get; init; }

    /// <summary>Packet id this message replies to (0 when not reply-linked).</summary>
    public uint ReplyToPacketId { get; init; }

    /// <summary>Aggregated reactions attached to this message.</summary>
    public ObservableCollection<MessageReaction> Reactions { get; } = new();

    private readonly Dictionary<string, MessageReaction> _reactionsByEmoji = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _reactorsByEmoji = new(StringComparer.Ordinal);

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>True when <paramref name="fromId"/> has already reacted with
    /// this emoji. A tapback is per-person, so reacting again is a no-op —
    /// callers use this to say so rather than appearing to do nothing.</summary>
    public bool HasReactionFrom(string emoji, string fromId) =>
        _reactorsByEmoji.TryGetValue((emoji ?? string.Empty).Trim(), out var reactors) &&
        reactors.Contains(string.IsNullOrWhiteSpace(fromId) ? "unknown" : fromId.Trim());

    /// <summary>Delivery state for outgoing messages, updated when an ACK/NAK
    /// arrives. Always <see cref="MessageDelivery.None"/> for received messages.</summary>
    [ObservableProperty]
    private MessageDelivery _delivery = MessageDelivery.None;

    partial void OnDeliveryChanged(MessageDelivery value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(DeliveryGlyph));
    }

    partial void OnFromIdChanged(string value) => OnPropertyChanged(nameof(Display));

    /// <summary>
    /// Trailing delivery mark, on its own so the view can colour it. Sent
    /// renders nothing on purpose: every message we transmit passes through
    /// that state, so labelling it said nothing and put noise on every outgoing
    /// line.
    ///
    /// The two delivery stages deliberately share one glyph. Reaching the mesh
    /// and reaching the recipient are the same event to the reader — "it got
    /// somewhere" — so the difference belongs in the colour, not in a second
    /// symbol they'd have to learn.
    /// </summary>
    public string DeliveryGlyph => Delivery switch
    {
        MessageDelivery.DeliveredToMesh => "✓",
        MessageDelivery.Delivered       => "✓",
        MessageDelivery.Failed          => "✗",
        _ => string.Empty,
    };

    /// <summary>The mark as plain text, for renderings that carry no colour
    /// (clipboard copy). Both delivery stages collapse to the same check there,
    /// which is the best a monochrome line can do.</summary>
    private string DeliverySuffix => DeliveryGlyph.Length == 0 ? string.Empty : $"  {DeliveryGlyph}";

    /// <summary>Timestamp column, in the unit-system-aware convention.</summary>
    public string TimePrefix => $"[{UiFormats.Stamp(Timestamp)}]";

    /// <summary>Re-raises every binding on this bubble. Computed display
    /// properties (the timestamp prefix follows the unit system) have no
    /// notification of their own, so the unit-system owner calls this to make
    /// already-rendered rows re-read them.</summary>
    public void NotifyDisplayChanged() => OnPropertyChanged(string.Empty);

    /// <summary>Single-line rendering used for clipboard copy.</summary>
    public string Display =>
        $"[{UiFormats.Stamp(Timestamp)}] {FromId,-12}  {Text}{DeliverySuffix}";

    /// <summary>Add or update one reaction for this message. A sender only
    /// counts once per emoji.</summary>
    public void AddReaction(string emoji, string fromId)
    {
        var emojiKey = (emoji ?? string.Empty).Trim();
        if (emojiKey.Length == 0) return;

        var sender = string.IsNullOrWhiteSpace(fromId) ? "unknown" : fromId.Trim();
        if (!_reactorsByEmoji.TryGetValue(emojiKey, out var reactors))
        {
            reactors = new HashSet<string>(StringComparer.Ordinal);
            _reactorsByEmoji[emojiKey] = reactors;
        }

        if (!reactors.Add(sender)) return;

        if (!_reactionsByEmoji.TryGetValue(emojiKey, out var reaction))
        {
            reaction = new MessageReaction
            {
                Emoji = emojiKey,
                Count = reactors.Count,
                Reactors = string.Join(", ", reactors.OrderBy(x => x, StringComparer.Ordinal)),
            };
            _reactionsByEmoji[emojiKey] = reaction;
            Reactions.Add(reaction);
            OnPropertyChanged(nameof(HasReactions));
            return;
        }

        reaction.Count = reactors.Count;
        reaction.Reactors = string.Join(", ", reactors.OrderBy(x => x, StringComparer.Ordinal));
    }
}

public partial class MessageReaction : ObservableObject
{
    public string Emoji { get; init; } = string.Empty;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _reactors = string.Empty;

    public string Display => $"{Emoji} {Count}";

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Display));
}

/// <summary>Delivery state of an outgoing message based on Meshtastic ACKs.</summary>
public enum MessageDelivery
{
    None,
    Sent,
    Delivered,
    Failed,

    /// <summary>
    /// A neighbour was heard rebroadcasting the message — Meshtastic's implicit
    /// ACK. It proves the mesh picked the message up, not that the addressee
    /// read it, so a DM sits here until the recipient's own ACK upgrades it to
    /// <see cref="Delivered"/>.
    ///
    /// Appended rather than slotted in after <see cref="Sent"/>, where it
    /// belongs logically: the numeric value is what the message store persists,
    /// so inserting one in the middle would silently re-label every outgoing
    /// message already on disk.
    /// </summary>
    DeliveredToMesh = 4,
}
