// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// How far a reply should be allowed to travel, ported from firmware's
/// <c>RoutingModule::getHopLimitForResponse</c> and <c>NodeDB</c>'s
/// <c>getHopsAway</c>. Pure so it can be tested directly.
/// </summary>
public static class ReplyHops
{
    /// <summary>Returned by <see cref="HopsUsed"/> when the distance the request
    /// travelled cannot be determined from its header.</summary>
    public const int Unknown = -1;

    /// <summary>
    /// Hops a received packet actually travelled, or <see cref="Unknown"/>.
    ///
    /// A hop_start of 0 is ambiguous on its own: it means either "the sender
    /// wanted zero hops" or "the sender predates the field", which firmware
    /// added in 2.3.0 and only guaranteed from 2.5.0. Firmware settles it with
    /// Data.bitfield, introduced alongside that guarantee — a decoded packet
    /// carrying the bitfield is known to mean the former, anything else is
    /// unknown. A packet we could not decrypt is therefore always unknown, since
    /// the bitfield is inside the ciphertext.
    /// </summary>
    public static int HopsUsed(MeshHeader header, bool hasBitfield)
    {
        if (header.HopStart == 0 && !hasBitfield) return Unknown;
        // Guards a malformed or tampered header: a packet cannot have more hops
        // remaining than it started with.
        if (header.HopStart < header.HopLimit) return Unknown;
        return header.HopStart - header.HopLimit;
    }

    /// <summary>
    /// Hop limit to answer <paramref name="header"/> with. A reply sent at the
    /// full configured limit is rebroadcast by every repeater in range no matter
    /// how close the requester actually was, so this gives the return path only
    /// the hops the request needed, plus a small margin for the route back
    /// differing from the route out.
    /// </summary>
    /// <param name="configuredHopLimit">Our own configured limit, clamped to 0-7.</param>
    public static byte ForResponse(MeshHeader header, bool hasBitfield, int configuredHopLimit)
    {
        byte configured = (byte)Math.Clamp(configuredHopLimit, 0, 7);
        int hopsUsed = HopsUsed(header, hasBitfield);

        if (hopsUsed >= 0)
        {
            // The request outran our own limit, so the way back needs at least
            // as many hops as the way out took.
            if (hopsUsed > configured) return (byte)hopsUsed;
            // The sender asked for zero hops: it wants a direct exchange with
            // nothing repeated on its behalf. Answering at the configured limit
            // would flood a reply across the mesh for a request that never left
            // the immediate neighbourhood.
            if (header.HopStart == 0) return 0;
            if (hopsUsed + 2 < configured) return (byte)(hopsUsed + 2);
        }
        return configured;
    }
}
