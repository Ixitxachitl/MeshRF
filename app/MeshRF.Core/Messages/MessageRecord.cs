// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Messages;

/// <summary>A received mesh packet recorded to the message database.</summary>
public sealed class MessageRecord
{
    public long Id { get; set; }

    /// <summary>32-bit Meshtastic packet id (from the header).</summary>
    public uint PacketId { get; set; }

    /// <summary>Data.reply_id for replies/reactions (0 when not set).</summary>
    public uint ReplyId { get; set; }

    /// <summary>Data.emoji Unicode codepoint for reactions (0 when not set).</summary>
    public uint Emoji { get; set; }

    /// <summary>True when this row represents a per-message reaction packet.
    /// Persisted explicitly so history replay does not rely on protocol-field
    /// heuristics that vary across firmware versions.</summary>
    public bool IsReaction { get; set; }

    public uint FromNode { get; set; }
    public uint ToNode { get; set; }

    /// <summary>Channel name the packet decoded on (empty if undecoded).</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Which mesh it was heard on: empty for the primary's, otherwise the
    /// preset. A channel name is not unique — two meshes can each have a
    /// "LongFast", and one of them can be somebody's primary running an
    /// entirely different preset — so the name alone cannot say which tab a
    /// stored message belongs to.
    /// </summary>
    public string Preset { get; set; } = string.Empty;

    /// <summary>Meshtastic port number (application type).</summary>
    public int PortNum { get; set; }

    /// <summary>Decoded UTF-8 text for text messages; empty otherwise.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Hex of the decrypted application payload (or raw frame if
    /// decryption failed).</summary>
    public string PayloadHex { get; set; } = string.Empty;

    /// <summary>True if the channel key successfully decrypted the packet.</summary>
    public bool Decrypted { get; set; }

    /// <summary>True when the packet header reported <c>via_mqtt</c>.</summary>
    public bool ViaMqtt { get; set; }

    public long RxEpoch { get; set; }
    public float? RssiDbfs { get; set; }
    public float? SnrDb { get; set; }

    /// <summary>Delivery state for messages we sent, mirroring the UI's
    /// MessageDelivery enum (0 = none/received, 1 = sent, 2 = delivered,
    /// 3 = failed). Persisted so ACK/NAK status survives a restart.</summary>
    public int Delivery { get; set; }

    public DateTime RxTime =>
        DateTimeOffset.FromUnixTimeSeconds(RxEpoch).LocalDateTime;

    public string FromId => $"!{FromNode:x8}";
    public string ToId => ToNode == 0xFFFFFFFFu ? "^all" : $"!{ToNode:x8}";
}
