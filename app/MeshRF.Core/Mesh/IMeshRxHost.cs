// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;

namespace MeshRF;

/// <summary>
/// App-specific hooks <see cref="MeshRxRouter"/> calls into. The router owns
/// the generic engine (demodulator-event parsing, channel-PSK/PKC decode,
/// dedup, node sighting); everything that's inherently app-specific —
/// chat-tab UI, relay/uplink policy, geofencing, telemetry history, games —
/// stays implemented by the host.
/// </summary>
public interface IMeshRxHost
{
    /// <summary>Our own node number, or 0 if unknown.</summary>
    uint MyNodeNum { get; }

    /// <summary>Our X25519 private key (32 bytes), or empty if we don't have one yet.</summary>
    byte[] MyPrivateKeyBytes { get; }

    /// <summary>Configured channels, used to try channel-PSK decode against each in turn.</summary>
    IReadOnlyList<ChannelConfig> Channels { get; }

    /// <summary>Current overall receiver RSSI (dBFS), used for the MessageRecord's
    /// RssiDbfs field — distinct from the per-packet <c>packetRssiDbm</c> parameters,
    /// matching existing MainViewModel behavior.</summary>
    float CurrentRssiDbfs { get; }

    /// <summary>Sender's stored public key as hex (64 chars), or null/empty if unknown.</summary>
    string? GetStoredPublicKeyHex(uint nodeNum);

    void Log(string message);

    /// <summary>Always called first for a non-echo frame, decoded or not.</summary>
    void RecordSighting(uint fromNode, long rxEpoch, float? rssiDbm, float? snrDb, byte hopsAway, bool viaMqtt);

    void MarkNodeDirty(uint nodeNum);

    /// <summary>We heard our own transmission relayed back (Meshtastic isFromUs).</summary>
    void OnOwnPacketHeard(MeshHeader header, MeshDecodeResult? ownDecode);

    /// <summary>
    /// This frame is a rebroadcast we transmitted ourselves, heard back off the
    /// air. Distinct from <see cref="OnOwnPacketHeard"/>: that one covers packets
    /// we *originated*, which name us in the header. A relay keeps the original
    /// sender's node number, so only the host — which knows what it put on the
    /// air — can recognise it.
    ///
    /// Defaulted to false so hosts that do not relay keep compiling unchanged.
    /// </summary>
    bool WasRelayedByUs(MeshHeader header) => false;

    /// <summary>Dedup gate for frames that failed to decode. Returns false if this
    /// exact undecoded packet was already seen recently.</summary>
    bool RememberUndecodedPacket(MeshHeader header);

    void HandleDuplicateForRelay(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb);
    void RelayIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb);
    void UplinkIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, bool isFromUs, float? snrDb, float? rssiDbm);

    /// <summary>
    /// A frame decoded successfully and is new (not a dedup hit). `record` is
    /// already stored in <see cref="MessageStore"/> by the router; the host
    /// handles all port-specific business logic (chat routing, node/telemetry/
    /// position updates, games, etc.) here.
    /// </summary>
    void OnMessageDecoded(byte[] frame, MeshHeader header, MessageRecord record, MeshDecodeResult result,
                          long rxEpoch, float? snrDb, float? packetRssiDbm, byte hopsAway);

    /// <summary>
    /// A frame decoded successfully but is a dedup hit — the sender is
    /// retransmitting something we already handled. Business logic must not run
    /// twice, but a want_ack packet still has to be re-acked: the retransmission
    /// is itself the evidence that our first ack never arrived. Firmware does
    /// this in <c>NextHopRouter::shouldFilterReceived</c>.
    ///
    /// Defaulted to a no-op so hosts that predate the ack path keep compiling
    /// unchanged.
    /// </summary>
    void OnDuplicateDecoded(MeshHeader header, MeshDecodeResult result) { }

    /// <summary>
    /// A frame decoded successfully, but <see cref="MessageStore"/> threw, so
    /// whether it is new cannot be established. Treated as a duplicate on air —
    /// no port handling, no change to relay or uplink — because a store that is
    /// failing cannot dedup, and re-relaying every copy of a flood is the worse
    /// error. It is still a packet we read, so the host records it for the log
    /// and the JSON feed.
    ///
    /// Defaulted to a no-op, like <see cref="OnDuplicateDecoded"/>.
    /// </summary>
    void OnDecodeNotStored(MeshHeader header, MeshDecodeResult result,
                           long rxEpoch, float? snrDb, float? packetRssiDbm, byte hopsAway) { }

    /// <summary>
    /// A frame no key we hold could decrypt. The plaintext header still says who
    /// it was for and whether it wanted an acknowledgement, so a packet
    /// addressed to us still deserves an answer — a NAK rather than silence.
    /// Firmware does this in <c>ReliableRouter::sniffReceived</c>.
    ///
    /// Defaulted to a no-op, like <see cref="OnDuplicateDecoded"/>.
    /// </summary>
    void OnUndecodedPacket(MeshHeader header) { }
}
