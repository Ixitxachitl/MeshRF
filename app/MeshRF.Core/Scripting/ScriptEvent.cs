// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

public enum ScriptEventKind
{
    /// <summary>A text message was decoded.</summary>
    Text,
    /// <summary>A node with no prior record was heard for the first time.</summary>
    NewNode,
    /// <summary>An emoji tapback landed on one of our messages.</summary>
    Reaction,
    /// <summary>A scheduled trigger came due. Carries no sender.</summary>
    Timer,
    /// <summary>A Quick send button was pressed. Carries no sender, but does
    /// carry the destination chosen for it.</summary>
    QuickSend,
}

/// <summary>This node, as scripts see it.</summary>
/// <param name="NodeNum">Our node number.</param>
/// <param name="ShortName">Our configured short name.</param>
/// <param name="LongName">Our configured long name.</param>
/// <param name="BatteryPct">Battery level, or 101 for the mains-powered
/// sentinel this app reports (see RadioViewModel's device metrics).</param>
/// <param name="Latitude">This node's home latitude, or null when none is set.</param>
/// <param name="Longitude">This node's home longitude, or null when none is set.</param>
public sealed record ScriptSelf(
    uint NodeNum,
    string ShortName,
    string LongName,
    int? BatteryPct,
    double? Latitude = null,
    double? Longitude = null)
{
    public string Id => $"!{NodeNum:x8}";

    /// <summary>Whether this node knows where it is, so a script asking a
    /// location-shaped question of an API has something to ask about.</summary>
    public bool HasLocation => Latitude is not null && Longitude is not null;

    public static readonly ScriptSelf Unknown = new(0, string.Empty, string.Empty, null);
}

/// <summary>
/// A flat snapshot of something that happened, handed to the engine to match
/// scripts against.
/// </summary>
/// <remarks>
/// Deliberately self-contained: everything a condition or a placeholder could
/// want is copied in by the host before evaluation, so the engine never reaches
/// back into the node store, the channel list or the radio. That is what makes
/// it testable without any of them, and what keeps script evaluation off the
/// critical path of the decode loop.
/// </remarks>
public sealed record ScriptEvent
{
    public ScriptEventKind Kind { get; init; } = ScriptEventKind.Text;

    /// <summary>Message body, for <see cref="ScriptEventKind.Text"/>.</summary>
    public string Text { get; init; } = string.Empty;

    public uint FromNode { get; init; }

    /// <summary>Button label, for <see cref="ScriptEventKind.QuickSend"/>. Only
    /// the script whose trigger names this button runs.</summary>
    public string QuickSendName { get; init; } = string.Empty;

    /// <summary>Peer a <see cref="ScriptEventKind.QuickSend"/> was aimed at, or
    /// 0 when it was aimed at a channel. Kept apart from <see cref="FromNode"/>
    /// so conditions asking who sent this still fail closed: a button press has
    /// a destination but no sender.</summary>
    public uint ToNode { get; init; }

    /// <summary>Node an answer goes to: the peer chosen for a button press, the
    /// sender for anything that arrived over the air.</summary>
    public uint DestinationNode => Kind == ScriptEventKind.QuickSend ? ToNode : FromNode;

    /// <summary>Whether somebody put this event on the air, so conditions about
    /// them (from:, snr_above:, hops_below:) have something to read.</summary>
    public bool HasSender => Kind is ScriptEventKind.Text or ScriptEventKind.NewNode
                                  or ScriptEventKind.Reaction;

    /// <summary>Whether this event knows where a message would go. Only a
    /// schedule does not: it is neither on a channel nor aimed at anyone.</summary>
    public bool HasDestination => Kind != ScriptEventKind.Timer;
    public string FromShort { get; init; } = string.Empty;
    public string FromLong { get; init; } = string.Empty;

    /// <summary>Where the sender was last reported to be, from the node table,
    /// or null when they have never sent a position. Copied in with the rest of
    /// the snapshot so a script asking "what is the weather where you are"
    /// never reaches back into the node store.</summary>
    public double? FromLatitude { get; init; }
    public double? FromLongitude { get; init; }

    /// <summary>Whether the sender's position is known, so a script can stop
    /// before building a request around an empty coordinate.</summary>
    public bool SenderHasLocation => FromLatitude is not null && FromLongitude is not null;

    /// <summary>Channel name, or "PKC" for an encrypted direct message.</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>Whether that channel is the primary one. Carried as a flag
    /// rather than compared by name, since the primary's name differs from mesh
    /// to mesh and is empty on a default-preset channel.</summary>
    public bool IsPrimaryChannel { get; init; }

    /// <summary>Addressed to us specifically, rather than broadcast.</summary>
    public bool IsDirect { get; init; }

    public double? SnrDb { get; init; }
    public double? RssiDbm { get; init; }
    public int Hops { get; init; }

    public bool SenderIsFavorite { get; init; }

    /// <summary>The sender's public key is on file, so a reply can be
    /// PKC-sealed.</summary>
    public bool SenderHasKey { get; init; }

    /// <summary>Packet the event came from, so a reply can thread under it and
    /// a reaction can target it.</summary>
    public uint PacketId { get; init; }

    /// <summary>The glyph, for <see cref="ScriptEventKind.Reaction"/>.</summary>
    public string Emoji { get; init; } = string.Empty;

    public ScriptSelf Self { get; init; } = ScriptSelf.Unknown;

    /// <summary>Local time the event happened, used by <c>between:</c> and by
    /// the rate limiter. Local rather than UTC because the times a user writes
    /// in a script are wall-clock times.</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public string FromId => $"!{FromNode:x8}";
}
