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
    public string FromShort { get; init; } = string.Empty;
    public string FromLong { get; init; } = string.Empty;

    /// <summary>Channel name, or "PKC" for an encrypted direct message.</summary>
    public string Channel { get; init; } = string.Empty;

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
