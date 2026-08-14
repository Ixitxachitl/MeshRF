// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// Values of the Meshtastic <c>Routing.error_reason</c> field (mesh.proto,
/// <c>Routing.Error</c>). Only the ones MeshRF sends or reads are named; the
/// full enum is much larger and the rest are relayed as raw numbers.
/// </summary>
public static class RoutingError
{
    /// <summary>Success — this is an ACK, not a NAK.</summary>
    public const uint None = 0;

    /// <summary>We could not decrypt the packet with any channel key we hold.
    /// Firmware's answer for a want_ack packet addressed to it that it cannot
    /// read, sent on the primary channel since the real one is unknown.</summary>
    public const uint NoChannel = 6;

    /// <summary>The packet looked PKI-encrypted but we have no public key for
    /// the sender, so we could not even attempt to decrypt it. Distinct from
    /// <see cref="NoChannel"/> because it is actionable: the sender's client
    /// answers it by sending us their NodeInfo.</summary>
    public const uint PkiUnknownPubkey = 35;
}
