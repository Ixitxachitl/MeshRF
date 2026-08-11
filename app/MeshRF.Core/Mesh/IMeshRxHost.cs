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
}
