// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;

namespace MeshRF;

/// <summary>
/// Shared RX engine: turns a demodulated frame into a decoded, deduplicated
/// Meshtastic packet and dispatches it to an <see cref="IMeshRxHost"/>. This
/// is the part of the pipeline that's identical for every frontend —
/// channel-PSK decode with an async PKC (public-key) fallback for direct
/// messages, own-packet-echo detection, and dedup via <see cref="MessageStore"/>.
/// Everything port-specific (chat UI, geofencing, telemetry, games, ...)
/// stays with the host. Demodulator-event-line parsing also stays with the
/// host: it owns other order-sensitive state (e.g. the preamble-peak SNR
/// estimate) tied to the same event stream, so it computes snr/rssi and
/// calls <see cref="ProcessReceivedFrame"/> directly once it has a frame.
/// </summary>
public sealed class MeshRxRouter : IDisposable
{
    private const int MaxQueuedPkcDecodes = 256;

    private readonly IMeshRxHost _host;
    private readonly MessageStore _messageStore;
    private readonly IUiDispatcher _dispatcher;

    private readonly Dictionary<uint, byte[]> _senderPublicKeyCache = new();
    private readonly Channel<PkcWorkItem> _pkcQueue;
    private readonly CancellationTokenSource _pkcCts = new();
    private readonly Task _pkcWorker;

    public MeshRxRouter(IMeshRxHost host, MessageStore messageStore, IUiDispatcher dispatcher)
    {
        _host = host;
        _messageStore = messageStore;
        _dispatcher = dispatcher;
        // The oldest waiting decode is dropped when the queue is full, which
        // costs a direct message its only chance of being read. Rare enough to
        // be worth a line rather than a counter, and silence here reads as a
        // packet that was never sent.
        _pkcQueue = Channel.CreateBounded<PkcWorkItem>(
            new BoundedChannelOptions(MaxQueuedPkcDecodes)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            dropped => _host.Log(
                $"  PKC decode queue full — dropped {dropped.Header.FromId} pkt {dropped.Header.PacketId:x8}"));
        _pkcWorker = Task.Run(RunPkcWorkerAsync);
    }

    /// <summary>
    /// Entry point for both real RX (a frame parsed from a demodulator
    /// "payload" event) and frames synthesized from an accepted MQTT
    /// downlink envelope — both get identical dedup/relay/uplink/store/
    /// dispatch handling.
    /// </summary>
    public void ProcessReceivedFrame(byte[] frame, MeshHeader header, float? snrDb, float? packetRssiDbm)
    {
        // Own packet heard back (Meshtastic isFromUs): a neighbour rebroadcast
        // a frame we sent. Never re-processed as a new packet — just an
        // implicit relay confirmation.
        if (_host.MyNodeNum != 0 && header.From == _host.MyNodeNum)
        {
            var own = MeshDecoder.Decode(frame, _host.Channels);
            _host.OnOwnPacketHeard(header, own);
            return;
        }

        // Our own rebroadcast heard back. The check above cannot catch it: a
        // relayed frame still names the *original* sender, not us. Dropping it
        // here rather than letting it fall through to the dedup path is what
        // keeps it out of RecordSighting below — otherwise every relay we make
        // records that sender at our own transmitter's signal strength, one hop
        // further away than they are, and with a refreshed last-heard time.
        if (_host.WasRelayedByUs(header))
        {
            _host.Log($"  (own relay) heard our rebroadcast of {header.FromId} pkt {header.PacketId:x8}");
            return;
        }

        var rxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte hopsAway = (byte)(header.HopStart >= header.HopLimit
            ? header.HopStart - header.HopLimit
            : 0);

        var result = MeshDecoder.Decode(frame, _host.Channels);

        // PKC fallback: modern firmware seals DMs to us with X25519 + AES-CCM
        // (channel-hash byte 0x00) instead of a channel PSK, so channel decode
        // can't read them. Mirrors firmware's perhapsDecode.
        if (result is null && _host.MyNodeNum != 0 &&
            header.To == _host.MyNodeNum && !header.IsBroadcast &&
            header.ChannelHash == 0x00)
        {
            if (TryQueuePkcDecode(frame, header, rxEpoch, snrDb, packetRssiDbm, hopsAway))
                return;

            result = TryDecodePkc(frame, header);
        }

        ApplyDecodedPayloadResult(frame, header, result, rxEpoch, snrDb, packetRssiDbm, hopsAway);
    }

    private bool TryQueuePkcDecode(byte[] frame, MeshHeader header, long rxEpoch,
        float? snrDb, float? packetRssiDbm, byte hopsAway)
    {
        if (_pkcCts.IsCancellationRequested) return false;
        var myKey = _host.MyPrivateKeyBytes;
        if (myKey.Length != 32) return false;

        var senderPub = GetSenderPublicKeyBytes(header.From);
        if (senderPub.Length != 32) return false;

        return _pkcQueue.Writer.TryWrite(new PkcWorkItem(
            frame, header, rxEpoch, snrDb, packetRssiDbm, hopsAway,
            (byte[])myKey.Clone(), senderPub));
    }

    private async Task RunPkcWorkerAsync()
    {
        try
        {
            var reader = _pkcQueue.Reader;
            while (await reader.WaitToReadAsync(_pkcCts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    MeshDecodeResult? result = null;
                    try { result = MeshDecoder.DecodePkc(item.Frame, item.MyPrivateKey, item.SenderPublicKey); }
                    catch { result = null; }

                    if (_pkcCts.IsCancellationRequested) return;

                    await _dispatcher.InvokeAsync(() => ApplyDecodedPayloadResult(
                        item.Frame, item.Header, result, item.RxEpoch, item.SnrDb, item.PacketRssiDbm, item.HopsAway));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private MeshDecodeResult? TryDecodePkc(byte[] frame, MeshHeader header)
    {
        var myKey = _host.MyPrivateKeyBytes;
        if (myKey.Length != 32) return null;

        var senderPub = GetSenderPublicKeyBytes(header.From);
        if (senderPub.Length != 32) return null;

        return MeshDecoder.DecodePkc(frame, myKey, senderPub);
    }

    private byte[] GetSenderPublicKeyBytes(uint nodeNum)
    {
        if (_senderPublicKeyCache.TryGetValue(nodeNum, out var cached))
            return cached;

        var parsed = TryParsePublicKeyHex(_host.GetStoredPublicKeyHex(nodeNum));
        _senderPublicKeyCache[nodeNum] = parsed;
        return parsed;
    }

    /// <summary>Call when a node's stored public key changes (e.g. a fresh
    /// NodeInfo arrived), so a stale/empty cached entry doesn't linger.</summary>
    public void InvalidateSenderPublicKeyCache(uint nodeNum) => _senderPublicKeyCache.Remove(nodeNum);

    private static byte[] TryParsePublicKeyHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        var s = hex.Trim();
        if (s.Length != 64) return Array.Empty<byte>();
        try
        {
            var bytes = Convert.FromHexString(s);
            return bytes.Length == 32 ? bytes : Array.Empty<byte>();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private void ApplyDecodedPayloadResult(byte[] frame, MeshHeader header, MeshDecodeResult? result,
        long rxEpoch, float? snrDb, float? packetRssiDbm, byte hopsAway)
    {
        bool nodeInfoRecord = result is { Port: PortNum.NodeInfo, User: not null } && result.AppPayload.Length != 0;

        // Always record the sender sighting (RSSI/last-heard), decoded or
        // not. NodeInfo records fold these fields into their own upsert.
        if (!nodeInfoRecord)
            _host.RecordSighting(header.From, rxEpoch, packetRssiDbm, snrDb, hopsAway, header.ViaMqtt);

        if (result is null)
        {
            if (!_host.RememberUndecodedPacket(header))
            {
                _host.HandleDuplicateForRelay(frame, header, result, snrDb);
                // Repeats get answered too: the sender only retransmits because
                // it never heard our first reply.
                _host.OnUndecodedPacket(header);
                _host.Log($"  (dup) rx undecoded from {header.FromId} pkt {header.PacketId:x8} (chan hash {header.ChannelHash:X2})");
                _host.MarkNodeDirty(header.From);
                return;
            }

            _host.RelayIfEligible(frame, header, result, snrDb);
            _host.UplinkIfEligible(frame, header, result, isFromUs: false, snrDb: snrDb, rssiDbm: packetRssiDbm);
            _host.OnUndecodedPacket(header);
            _host.Log($"  rx undecoded from {header.FromId} (chan hash {header.ChannelHash:X2})");
            _host.MarkNodeDirty(header.From);
            return;
        }

        uint normalizedReplyId = result.Port == PortNum.TextMessage ? ResolveReactionTargetId(result) : 0;
        bool isReactionRecord = result.Port == PortNum.TextMessage && normalizedReplyId != 0 && result.Emoji != 0;

        var record = new MessageRecord
        {
            PacketId = header.PacketId,
            FromNode = header.From,
            ToNode = header.To,
            PortNum = (int)result.Port,
            Channel = result.ChannelName,
            ReplyId = normalizedReplyId,
            Emoji = result.Emoji,
            IsReaction = isReactionRecord,
            Decrypted = true,
            ViaMqtt = header.ViaMqtt,
            RxEpoch = rxEpoch,
            RssiDbfs = float.IsNegativeInfinity(_host.CurrentRssiDbfs) ? null : _host.CurrentRssiDbfs,
            SnrDb = snrDb,
        };
        record.PayloadHex = BytesToHex(result.AppPayload);
        if (result.Port == PortNum.TextMessage)
            record.Text = result.Text ?? string.Empty;

        // Dedup: Meshtastic floods packets, so the same message arrives
        // several times via different relays. Add returns false for a
        // packet we've already stored — skip all further handling for repeats.
        bool isNew;
        bool stored = true;
        try { isNew = _messageStore.Add(record); }
        catch (Exception ex)
        {
            _host.Log($"message store failed: {ex.Message}");
            stored = false;
            isNew = false;
        }

        if (!isNew)
        {
            _host.HandleDuplicateForRelay(frame, header, result, snrDb);
            _host.MarkNodeDirty(header.From);
            // Acked on the same terms either way: a want_ack packet we could
            // not file is still one the sender is waiting on, and a broken
            // store is no reason to answer it differently.
            _host.OnDuplicateDecoded(header, result);
            // A throw is not a dedup hit, though. Both take the same
            // conservative path on air, but calling the failure a duplicate
            // reports a packet never seen before as one already handled.
            if (stored) _host.Log($"  (dup) {header.FromId} pkt {header.PacketId:x8}");
            else _host.OnDecodeNotStored(header, result, rxEpoch, snrDb, packetRssiDbm, hopsAway);
            return;
        }

        _host.RelayIfEligible(frame, header, result, snrDb);
        // Matches the original inline call exactly: unlike the undecoded
        // branch above, snr/rssi are intentionally NOT forwarded here.
        _host.UplinkIfEligible(frame, header, result, isFromUs: false, snrDb: null, rssiDbm: null);

        _host.OnMessageDecoded(frame, header, record, result, rxEpoch, snrDb, packetRssiDbm, hopsAway);
        _host.MarkNodeDirty(header.From);
    }

    private static uint ResolveReactionTargetId(MeshDecodeResult result)
    {
        if (result.ReplyId != 0) return result.ReplyId;
        // Some firmware paths reuse request_id for reply-linked packets.
        if (result.RequestId != 0) return result.RequestId;
        return 0;
    }

    private static string BytesToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var x in bytes) sb.Append(x.ToString("X2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        _pkcCts.Cancel();
        _pkcQueue.Writer.TryComplete();
        try { _pkcWorker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _pkcCts.Dispose();
    }

    private sealed record PkcWorkItem(
        byte[] Frame,
        MeshHeader Header,
        long RxEpoch,
        float? SnrDb,
        float? PacketRssiDbm,
        byte HopsAway,
        byte[] MyPrivateKey,
        byte[] SenderPublicKey);
}
