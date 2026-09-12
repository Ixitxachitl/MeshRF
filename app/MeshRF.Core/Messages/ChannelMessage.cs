// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshRF.Mesh;

namespace MeshRF;

/// <summary>
/// Who and what a reply quotes: enough to draw the line above its own words,
/// and — because the node is held by number — to redraw that line when the
/// quoted node is renamed.
/// </summary>
public readonly record struct ReplyQuote(uint SenderNodeNum, string SenderName, string Preview)
{
    /// <summary>A reply whose target is not to hand: one answering a message
    /// this station never had, or has since dropped.</summary>
    public static readonly ReplyQuote None = new(0, string.Empty, string.Empty);

    /// <summary>Whether there is a quoted message to name at all.</summary>
    public bool HasTarget => SenderNodeNum != 0 || SenderName.Length > 0;

    /// <summary>The quote as it is drawn, wherever a reply is shown: above the
    /// bubble, and in the compose box while one is being written.</summary>
    public string ContextLine =>
        $"replying to {(SenderName.Length > 0 ? SenderName : "unknown")}: \"{Preview}\"";

    /// <summary>The quote of a message already on screen.</summary>
    public static ReplyQuote Of(ChannelMessage target) =>
        new(target.SenderNodeNum, target.FromId ?? string.Empty, PreviewOf(target.Text));

    /// <summary>A quoted message as one readable line. Long enough to
    /// recognise which message is meant, short enough not to repeat it. Quoted
    /// as the bubble draws it, entities and all, so the quote reads like the
    /// message it points at.</summary>
    public static string PreviewOf(string? text)
    {
        var decoded = string.IsNullOrEmpty(text) ? string.Empty : WebUtility.HtmlDecode(text) ?? text;
        var normalized = decoded.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length == 0) return "(empty)";
        return normalized.Length <= 80 ? normalized : normalized[..80] + "...";
    }
}

/// <summary>One rendered chat bubble in a channel or DM conversation view.</summary>
public partial class ChannelMessage : ObservableObject
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    [ObservableProperty]
    private string _fromId = string.Empty;

    /// <summary>Sender node id for display-name refresh (0 when unknown/system).</summary>
    public uint SenderNodeNum { get; init; }

    public string Text    { get; init; } = string.Empty;

    /// <summary>Whether the sender marked this as an alert with Meshtastic's
    /// bell character. Worth showing: the character is non-printing, so without
    /// a mark here an alert is indistinguishable from any other message on a
    /// client that does not buzz.</summary>
    public bool HasAlertBell => AlertBell.IsIn(Text);

    /// <summary>The words themselves, drawn: the bell taken out, since it has
    /// no glyph and a font lacking one draws a placeholder box. The bell emoji
    /// a sender may have paired with it is ordinary text and stays.</summary>
    /// <remarks>
    /// HTML entities are resolved, because the senders that emit them are bots
    /// relaying a web page and their readers are meant to see "40.224°N", not
    /// "40.224&amp;deg;N". Whitespace is not touched: the blank lines a sender
    /// laid a report out with are theirs. What arrived is kept verbatim in the
    /// store either way — this is only how it is shown, and it is shown in a
    /// text run, never parsed as markup, so a decoded angle bracket is only
    /// ever a character.
    /// </remarks>
    private string Body => Decoded(HasAlertBell ? AlertBell.StripFrom(Text) : Text);

    private static string Decoded(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : WebUtility.HtmlDecode(text) ?? text;

    /// <summary>The message as it should be drawn: a reply's quote line, then
    /// what was actually said.</summary>
    /// <remarks>
    /// Composed here rather than folded into <see cref="Text"/> when the bubble
    /// is built. The quote names a node, and a name changes — baked in, it kept
    /// the name the node had when the reply arrived. Keeping the body separate
    /// also means the clipboard, the alert-bell test and a reply quoting this
    /// reply in turn all see the words and not the quotation above them.
    /// </remarks>
    public string DisplayText =>
        ReplyContext.Length == 0 ? Body : $"{ReplyContext}\n{Body}";
    public float? Rssi { get; init; }

    /// <summary>True when <see cref="Rssi"/> is dBm off a packet radio, false
    /// when it is dBFS off an SDR. Carried so a bubble's detail line can name
    /// the unit it actually has.</summary>
    public bool RssiIsDbm { get; init; }
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

    /// <summary>The node whose message this one quotes, and what that message
    /// said. Held by number so a rename reaches a quote already on screen.</summary>
    public uint ReplyToSenderNodeNum { get; init; }
    public string ReplyToPreview { get; init; } = string.Empty;

    /// <summary>What the quoted node is called now.</summary>
    [ObservableProperty] private string _replyToSenderName = string.Empty;

    partial void OnReplyToSenderNameChanged(string value)
    {
        OnPropertyChanged(nameof(ReplyContext));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(Display));
    }

    /// <summary>The line above a reply's own words, or nothing when this is not
    /// a reply. A target this station never saw is named by its packet id,
    /// which is all the reply itself carries.</summary>
    public string ReplyContext =>
        !IsReplyLinked ? string.Empty
        : ReplyTargetFound
            ? new ReplyQuote(ReplyToSenderNodeNum, ReplyToSenderName, ReplyToPreview).ContextLine
            : $"replying to {ReplyToPacketId:x8} (original message not found)";

    /// <summary>The mesh a beacon advertised, when this bubble is one; null
    /// otherwise. Carries its own state, so the offer can go from addable to
    /// added without the bubble being rebuilt.</summary>
    public BeaconOffer? Offer { get; init; }

    public bool HasOffer => Offer is not null;

    /// <summary>True for a bubble that came from a beacon, whether or not it
    /// carries an offer or any words.</summary>
    public bool IsBeacon { get; init; }

    /// <summary>Whether there is anything to draw as text. A beacon that
    /// carries only an invitation has none, and an empty line above the card
    /// would be a gap with nothing in it.</summary>
    public bool HasText => DisplayText.Length > 0;

    /// <summary>Aggregated reactions attached to this message.</summary>
    public ObservableCollection<MessageReaction> Reactions { get; } = new();

    private readonly Dictionary<string, MessageReaction> _reactionsByEmoji = new(StringComparer.Ordinal);

    /// <summary>Who has reacted with each emoji, by node number. A name is
    /// neither unique nor fixed: keyed by name, two nodes called the same
    /// thing counted as one reactor, and a node that renamed itself between
    /// two reactions counted as two.</summary>
    private readonly Dictionary<string, HashSet<uint>> _reactorsByEmoji = new(StringComparer.Ordinal);

    /// <summary>What each reactor is called, for the tooltip. Kept beside the
    /// numbers rather than in place of them, so a rename relabels what is
    /// shown without disturbing who has reacted.</summary>
    private readonly Dictionary<uint, string> _reactorNames = new();

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>True when <paramref name="fromNode"/> has already reacted with
    /// this emoji. A tapback is per-node, so reacting again is a no-op —
    /// callers use this to say so rather than appearing to do nothing.</summary>
    public bool HasReactionFrom(string emoji, uint fromNode) =>
        _reactorsByEmoji.TryGetValue((emoji ?? string.Empty).Trim(), out var reactors) &&
        reactors.Contains(fromNode);

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

    /// <summary>Timestamp shown in a bubble's header, in the unit-system-aware
    /// convention. Unbracketed: the header is its own line, so the brackets
    /// that once separated a timestamp column from the text are just noise.
    ///
    /// Today's traffic — nearly all of what a live session shows — carries the
    /// time alone, since repeating the current date on every bubble says
    /// nothing. Anything older keeps the full stamp, which is the only place
    /// the day a message arrived is visible. The date is read when the header
    /// renders, so a session left open past midnight keeps calling yesterday
    /// "today" until something re-raises the binding.</summary>
    public string TimeLabel => Timestamp.Date == DateTime.Today
        ? UiFormats.Time(Timestamp)
        : UiFormats.Stamp(Timestamp);

    /// <summary>Re-raises every binding on this bubble. Computed display
    /// properties (the timestamp label follows the unit system) have no
    /// notification of their own, so the unit-system owner calls this to make
    /// already-rendered rows re-read them.</summary>
    public void NotifyDisplayChanged() => OnPropertyChanged(string.Empty);

    /// <summary>Single-line rendering used for clipboard copy.</summary>
    public string Display =>
        $"[{UiFormats.Stamp(Timestamp)}] {FromId,-12}  {DisplayText}{DeliverySuffix}";

    /// <summary>Add or update one reaction for this message. A node only
    /// counts once per emoji, however it is named at the time.</summary>
    public void AddReaction(string emoji, uint fromNode, string fromName)
    {
        var emojiKey = (emoji ?? string.Empty).Trim();
        if (emojiKey.Length == 0) return;

        // The newest name wins: a reactor seen again under another name is the
        // same node, and the tooltip should say what it is called now.
        _reactorNames[fromNode] = string.IsNullOrWhiteSpace(fromName) ? "unknown" : fromName.Trim();

        if (!_reactorsByEmoji.TryGetValue(emojiKey, out var reactors))
        {
            reactors = new HashSet<uint>();
            _reactorsByEmoji[emojiKey] = reactors;
        }
        reactors.Add(fromNode);

        if (!_reactionsByEmoji.TryGetValue(emojiKey, out var reaction))
        {
            reaction = new MessageReaction
            {
                Emoji = emojiKey,
                Count = reactors.Count,
                Reactors = NamesOf(reactors),
            };
            _reactionsByEmoji[emojiKey] = reaction;
            Reactions.Add(reaction);
            OnPropertyChanged(nameof(HasReactions));
            return;
        }

        reaction.Count = reactors.Count;
        reaction.Reactors = NamesOf(reactors);
    }

    /// <summary>Puts a reactor's new name into every tooltip it appears in.
    /// The count is untouched: who reacted has not changed, only what they are
    /// called.</summary>
    public void RelabelReactor(uint nodeNum, string name)
    {
        var reactor = string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim();
        if (!_reactorNames.TryGetValue(nodeNum, out var labelled) || labelled == reactor) return;
        _reactorNames[nodeNum] = reactor;

        foreach (var (emojiKey, reactors) in _reactorsByEmoji)
            if (reactors.Contains(nodeNum) && _reactionsByEmoji.TryGetValue(emojiKey, out var reaction))
                reaction.Reactors = NamesOf(reactors);
    }

    /// <summary>The reactors as the tooltip lists them, in name order.</summary>
    private string NamesOf(HashSet<uint> reactors) =>
        string.Join(", ", reactors.Select(NameOf).OrderBy(x => x, StringComparer.Ordinal));

    private string NameOf(uint nodeNum) =>
        _reactorNames.TryGetValue(nodeNum, out var name) ? name : $"!{nodeNum:x8}";
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
