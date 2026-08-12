// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MeshRF.Mesh;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Transmit discipline and demodulator-event draining, ported from
/// MeshRF.App's MainViewModel.
///
/// This app previously called <c>Core.Transmit</c> straight from every send
/// path: no serialisation, and no regard for whether the channel was busy.
/// Two nodes cannot share the air that way. Concurrent sends raced on the one
/// native Core handle, and auto-replies — which fire the instant a packet is
/// received, i.e. exactly when the mesh is mid-flood — keyed up on top of the
/// traffic they were answering. The result is collisions, unacknowledged
/// packets, and a mesh that keeps retrying and reflooding.
/// </summary>
public partial class RadioViewModel
{
    // -- RX-busy tracking (feeds the listen-before-talk defer below) --------

    /// <summary>How long a detected preamble keeps the channel marked busy,
    /// absent an explicit end-of-frame.</summary>
    private static readonly TimeSpan RxBusyDefaultHold = TimeSpan.FromMilliseconds(220);

    /// <summary>Upper bound on deferring a transmit for a busy channel. Bounded
    /// so a response is delayed rather than blocked indefinitely.</summary>
    private static readonly TimeSpan RxBusyMaxWait = TimeSpan.FromMilliseconds(450);

    private const int RxBusyPollMs = 20;

    private readonly object _rxBusyLock = new();
    private DateTime _rxBusyUntilUtc = DateTime.MinValue;

    /// <summary>Mark RX busy for at least <paramref name="hold"/> from now.
    /// Called from the event drain when a preamble is detected.</summary>
    private void MarkRxBusy(DateTime nowUtc, TimeSpan hold)
    {
        var until = nowUtc + hold;
        lock (_rxBusyLock)
        {
            if (until > _rxBusyUntilUtc) _rxBusyUntilUtc = until;
        }
    }

    /// <summary>A payload line ends the frame the preamble started, so drop the
    /// hold immediately rather than waiting it out.</summary>
    private void MarkRxFrameComplete(DateTime nowUtc)
    {
        lock (_rxBusyLock) _rxBusyUntilUtc = nowUtc;
    }

    private bool IsRxBusy(DateTime nowUtc)
    {
        lock (_rxBusyLock) return nowUtc < _rxBusyUntilUtc;
    }

    private async Task WaitForRxIdleAsync(TimeSpan maxWait, CancellationToken ct = default)
    {
        if (maxWait <= TimeSpan.Zero) return;

        var start = DateTime.UtcNow;
        while (true)
        {
            var now = DateTime.UtcNow;
            if (!IsRxBusy(now)) return;

            var elapsed = now - start;
            if (elapsed >= maxWait) return;

            var remainMs = Math.Max(1, (int)(maxWait - elapsed).TotalMilliseconds);
            await Task.Delay(Math.Min(RxBusyPollMs, remainMs), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Opportunistic CSMA-like defer: wait for the channel to go idle
    /// up to a small bound, then add a short random backoff so several nodes
    /// answering the same packet don't key up in unison.</summary>
    private async Task WaitForTxOpportunityAsync(CancellationToken ct = default)
    {
        await WaitForRxIdleAsync(RxBusyMaxWait, ct).ConfigureAwait(false);
        await Task.Delay(Random.Shared.Next(8, 24), ct).ConfigureAwait(false);
    }

    // -- Transmit -----------------------------------------------------------

    /// <summary>Serialises transmits: the native Core handle is shared, and a
    /// concurrent send would race on it.</summary>
    private readonly SemaphoreSlim _txSemaphore = new(1, 1);

    /// <summary>Fire-and-forget transmit for auto-replies triggered on packet
    /// receipt. Goes through the same gate and channel-idle defer as an
    /// awaited send; failures are swallowed because auto-replies are
    /// best-effort.</summary>
    private void TransmitBackground(byte[] frame)
    {
        _ = Task.Run(async () =>
        {
            try { await TransmitFrameAsync(frame).ConfigureAwait(false); }
            catch { /* best-effort */ }
        });
    }

    // -- Relay --------------------------------------------------------------

    /// <summary>
    /// Current relay configuration, or null when relaying is off. Returning null
    /// is what the Routing checkbox controls: the rebroadcast mode alone can't
    /// express "off" for router roles, since firmware coerces NONE to ALL for
    /// them, so the opt-in is kept separate.
    /// </summary>
    private RelayContext? BuildRelayContext()
    {
        if (!RoutingRelayEnabled) return null;
        if (!CanTransmit) return null;

        return new RelayContext(
            MyRole ?? string.Empty,
            RebroadcastMode ?? "ALL",
            _rxHost.MyNodeNum,
            SelectedPreset,
            _nodeStore.Get,
            _nodeStore.All);
    }

    // -- Acknowledgements ---------------------------------------------------

    /// <summary>
    /// Acknowledges a unicast addressed to us that asked for one.
    ///
    /// Not optional politeness: an unacked want_ack packet is retransmitted by
    /// its sender, and every repeater refloods each retry with a decrementing
    /// hop limit. A node that never acks turns one direct message into dozens
    /// of airtime-consuming copies across the whole mesh. This app had no ack
    /// path at all, so every DM sent to it did exactly that.
    ///
    /// The addressing lives in the plaintext header, so a packet we cannot
    /// decrypt is still known to be for us and is still acked — which is the
    /// case that matters here, since PKC direct messages aren't decodable yet.
    /// </summary>
    private void SendAck(MeshHeader header, string? channelName, bool pkc)
    {
        if (!CanTransmit || _rxHost.MyNodeNum == 0) return;
        if (header.IsBroadcast || header.To != _rxHost.MyNodeNum) return;
        if (!header.WantAck) return;

        try
        {
            uint packetId = NextPacketId();
            byte[]? frame = null;

            if (pkc)
            {
                // A PKC message must be acked over PKC, sealed back to the
                // sender with our private key and their public key.
                var myPriv = TryParseKeyBase64(MyPrivateKey);
                var peerPub = TryParseHex(_rxHost.PublicKeyHexFor(header.From));
                if (myPriv.Length == 32 && peerPub.Length == 32)
                    frame = MeshEncoder.EncodePkcRouting(
                        _rxHost.MyNodeNum, header.From, packetId, header.PacketId,
                        myPriv, peerPub, errorReason: 0, hopLimit: ResponseHopLimit(header));
            }
            else
            {
                var channel = _rxHost.FindChannelByName(channelName) ?? PrimaryChannel();
                if (channel is not null)
                    frame = MeshEncoder.EncodeRouting(
                        channel, _rxHost.MyNodeNum, header.From, packetId, header.PacketId,
                        errorReason: 0, hopLimit: ResponseHopLimit(header));
            }

            if (frame is not null) TransmitBackground(frame);
        }
        catch (Exception ex)
        {
            StatusText = $"Ack failed: {ex.Message}";
        }
    }

    /// <summary>Hop limit for a reply, ported from firmware's
    /// RoutingModule::getHopLimitForResponse. A reply sent at the full
    /// configured limit is rebroadcast by every repeater in range regardless of
    /// how close the requester actually was; this gives the return path only
    /// the hops the request needed, plus a small margin.</summary>
    private byte ResponseHopLimit(MeshHeader header)
    {
        byte configured = (byte)Math.Clamp(HopLimit, 0, 7);
        int hopStart = header.HopStart;
        // hop_start of 0 means the field isn't in use; hops used is unknown.
        int hopsUsed = hopStart > 0 ? hopStart - header.HopLimit : -1;

        if (hopsUsed >= 0)
        {
            if (hopsUsed > configured) return (byte)hopsUsed;
            if (hopsUsed + 2 < configured) return (byte)(hopsUsed + 2);
        }
        return configured;
    }

    private static byte[] TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        try
        {
            var bytes = Convert.FromHexString(hex);
            return bytes.Length == 32 ? bytes : Array.Empty<byte>();
        }
        catch { return Array.Empty<byte>(); }
    }

    // -- Demodulator event drain -------------------------------------------

    private const int MaxRxEventsPerTick = 8;
    private const double MaxRxDrainMsPerTick = 4.0;

    private static readonly Regex PreamblePeakRegex = new(
        @"peak=(?<peak>-?\d+(?:\.\d+)?)dB", RegexOptions.Compiled);

    /// <summary>Peak-above-noise from the last preamble, used as the SNR for
    /// the payload that follows it.</summary>
    private float? _lastPreamblePeakDb;

    /// <summary>Preamble and payload lines arrive per frame and carry long hex;
    /// they're handled structurally instead of being echoed to the log.</summary>
    private static bool IsHighRateDemodEvent(string ev) =>
        ev.StartsWith("preamble", StringComparison.Ordinal) ||
        ev.StartsWith("payload", StringComparison.Ordinal);

    /// <summary>Large payload hex is expensive to render and can stall the UI
    /// at end-of-frame, so what gets displayed is compacted.</summary>
    private static string CompactDemodEventForUi(string ev)
    {
        if (!ev.StartsWith("payload", StringComparison.Ordinal)) return ev;

        var m = PayloadLineRegex.Match(ev);
        if (!m.Success) return ev;

        string status = m.Groups["status"].Success ? m.Groups["status"].Value : "?";
        string hex = m.Groups["hex"].Success ? m.Groups["hex"].Value : string.Empty;
        int byteCount = hex.Length / 2;
        string preview = hex.Length <= 24
            ? hex
            : $"{hex.AsSpan(0, 12)}..{hex.AsSpan(hex.Length - 8)}";

        return $"payload[{status}] {byteCount} B {preview}";
    }

    private static bool IsCrcOkPayload(string ev)
    {
        if (ev.IndexOf("payload", StringComparison.Ordinal) < 0) return false;
        var m = PayloadLineRegex.Match(ev);
        return m.Success && m.Groups["status"].Success && m.Groups["status"].Value == "OK";
    }

    /// <summary>Drains queued demodulator events, capped per tick so a burst
    /// can't lock up the UI thread.</summary>
    private void DrainDemodEvents()
    {
        if (_core is null) return;

        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < MaxRxEventsPerTick; i++)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= MaxRxDrainMsPerTick) break;

            var ev = _core.PullEvent();
            if (ev is null) break;
            var nowUtc = DateTime.UtcNow;

            if (!IsHighRateDemodEvent(ev)) _rxHost.Log(CompactDemodEventForUi(ev));

            if (ev.StartsWith("preamble", StringComparison.Ordinal))
            {
                // A preamble marks the start of a frame: hold off transmitting,
                // and keep its peak-above-noise as the SNR for the payload.
                MarkRxBusy(nowUtc, RxBusyDefaultHold);
                var pm = PreamblePeakRegex.Match(ev);
                if (pm.Success && float.TryParse(pm.Groups["peak"].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var peak))
                    _lastPreamblePeakDb = peak;
            }
            else if (ev.StartsWith("payload", StringComparison.Ordinal))
            {
                MarkRxFrameComplete(nowUtc);
            }

            DecodePayloadIfPossible(ev);

            if (IsCrcOkPayload(ev)) PacketDecoded?.Invoke();
        }
    }
}
