// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace MeshRF.Mesh;

/// <summary>
/// How much text one TEXT_MESSAGE_APP frame can carry.
/// </summary>
/// <remarks>
/// <para>
/// Two ceilings apply, and the text has to clear both. The protobuf one is
/// <c>Constants.DATA_PAYLOAD_LEN</c> = 233, which nanopb generates as a
/// 233-byte array — a longer payload field fails decode at the receiver. The
/// LoRa one is firmware's <c>perhapsEncode</c>, which requires <c>numbytes +
/// MESHTASTIC_HEADER_LENGTH &lt;= MAX_LORA_PAYLOAD_LEN</c> (Router.cpp), so the
/// encoded <c>Data</c> submessage gets 239 bytes and the text shares those with
/// protobuf framing.
/// </para>
/// <para>
/// The frame is the binding one, which puts a broadcast at 232 and leaves 233
/// one byte out of reach — 233 bytes of text encodes to 240 and comes back
/// <c>Routing_Error_TOO_LARGE</c>. The protobuf ceiling is enforced anyway
/// rather than assumed unreachable: it is what would bind if a field ever
/// stopped being written.
/// </para>
/// <para>
/// Channel encryption costs nothing: AES-CTR is a stream cipher, so ciphertext
/// is the same length as plaintext, and a ham-mode plaintext message gets the
/// same budget as an encrypted one. PKC costs 12 bytes — the AES-CCM tag plus
/// the extra nonce appended after the sealed payload.
/// </para>
/// <para>
/// XEdDSA signing is not a limit. <see cref="MeshEncoder"/> drops the signature
/// when the signed frame wouldn't fit, mirroring firmware <c>signedDataFits</c>,
/// so a long broadcast simply goes out unsigned rather than failing.
/// </para>
/// </remarks>
public static class TextMessageLimits
{
    /// <summary>Firmware <c>MAX_LORA_PAYLOAD_LEN</c> — the SX12xx limit.</summary>
    public const int FrameBytes = 255;

    /// <summary>Firmware <c>MESHTASTIC_PKC_OVERHEAD</c>: an 8-byte AES-CCM auth
    /// tag plus a 4-byte extra nonce, appended to a PKC-sealed payload.</summary>
    public const int PkcOverhead = 12;

    /// <summary>
    /// Meshtastic <c>Constants.DATA_PAYLOAD_LEN</c>, which mesh.options turns
    /// into <c>*Data.payload max_size:233</c> and nanopb into a 233-byte array.
    /// A longer payload field fails decode at the receiver however well it fits
    /// the frame, so it caps the frame arithmetic below.
    /// </summary>
    public const int PayloadFieldBytes = 233;

    /// <summary>
    /// UTF-8 bytes of text that fit in one frame, given what else the
    /// <c>Data</c> submessage has to carry.
    /// </summary>
    /// <param name="pkc">Sealed with X25519 + AES-CCM (a DM to a peer whose
    /// public key we hold) rather than the channel key.</param>
    /// <param name="reply">Carries <c>reply_id</c>.</param>
    /// <param name="reaction">Carries <c>emoji</c>.</param>
    public static int MaxTextBytes(bool pkc = false, bool reply = false,
                                   bool reaction = false)
    {
        int budget = FrameBytes - MeshHeader.Size - (pkc ? PkcOverhead : 0);

        // Every field MeshEncoder writes beside the text itself. Kept in step
        // with Encode/EncodePkc by MeshEncoderPayloadLimitTests, which encodes a
        // message of exactly this length and measures the frame.
        int fields = 2                      // field 1 portnum: tag + 1-byte varint
                   + 2                      // field 9 bitfield: tag + 1-byte varint, always written
                   + (reply ? 5 : 0)        // field 7 reply_id: tag + fixed32
                   + (reaction ? 5 : 0);    // field 8 emoji: tag + fixed32

        // What is left goes to field 2: tag byte, length varint, then the text.
        // The varint needs a second byte once the text reaches 128 bytes.
        int forPayloadField = budget - fields;
        int max = forPayloadField - 2;
        if (max > 127) max = forPayloadField - 3;

        // Belt and braces: the frame is the binding ceiling for every case we
        // encode today, so this never fires — but it is the one that would if a
        // field were ever dropped from the submessage.
        if (max > PayloadFieldBytes) max = PayloadFieldBytes;
        return max < 0 ? 0 : max;
    }

    /// <summary>What <paramref name="text"/> costs on air. Bytes, not
    /// characters: an emoji costs four and most non-ASCII costs two or
    /// three.</summary>
    public static int ByteCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>Whether <paramref name="text"/> fits the frame.</summary>
    public static bool Fits(string? text, bool pkc = false, bool reply = false,
                            bool reaction = false) =>
        ByteCount(text) <= MaxTextBytes(pkc, reply, reaction);
}
