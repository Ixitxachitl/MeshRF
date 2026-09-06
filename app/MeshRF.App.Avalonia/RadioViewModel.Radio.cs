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
    /// <summary>Per listener: a frame on one preset's channel says nothing
    /// about another's, unless the two channels overlap.</summary>
    private readonly Dictionary<int, DateTime> _rxBusyUntilUtc = new();

    /// <summary>Mark a listener's channel busy for at least <paramref name="hold"/>
    /// from now. Called from the event drain when a preamble is detected.</summary>
    private void MarkRxBusy(int listener, DateTime nowUtc, TimeSpan hold)
    {
        var until = nowUtc + hold;
        lock (_rxBusyLock)
        {
            if (!_rxBusyUntilUtc.TryGetValue(listener, out var current) || until > current)
                _rxBusyUntilUtc[listener] = until;
        }
    }

    /// <summary>A payload line ends the frame the preamble started, so drop the
    /// hold immediately rather than waiting it out.</summary>
    private void MarkRxFrameComplete(int listener, DateTime nowUtc)
    {
        lock (_rxBusyLock) _rxBusyUntilUtc[listener] = nowUtc;
    }

    /// <summary>Whether a burst on <paramref name="listener"/>'s channel would
    /// land on a reception: its own, or one on a channel that overlaps it.</summary>
    private bool IsRxBusy(DateTime nowUtc, int listener)
    {
        lock (_rxBusyLock)
        {
            foreach (var (other, until) in _rxBusyUntilUtc)
            {
                if (nowUtc >= until) continue;
                if (other == listener || ChannelsOverlap(listener, other)) return true;
            }
            return false;
        }
    }

    private async Task WaitForRxIdleAsync(TimeSpan maxWait, int listener, CancellationToken ct = default)
    {
        if (maxWait <= TimeSpan.Zero) return;

        var start = DateTime.UtcNow;
        while (true)
        {
            var now = DateTime.UtcNow;
            if (!IsRxBusy(now, listener)) return;

            var elapsed = now - start;
            if (elapsed >= maxWait) return;

            var remainMs = Math.Max(1, (int)(maxWait - elapsed).TotalMilliseconds);
            await Task.Delay(Math.Min(RxBusyPollMs, remainMs), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Opportunistic CSMA-like defer: wait for the channel to go idle
    /// up to a small bound, then add a short random backoff so several nodes
    /// answering the same packet don't key up in unison.</summary>
    private async Task WaitForTxOpportunityAsync(int listener, CancellationToken ct = default)
    {
        await WaitForRxIdleAsync(RxBusyMaxWait, listener, ct).ConfigureAwait(false);
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
    private void TransmitBackground(byte[] frame) => TransmitBackground(frame, TargetForFrame(frame));

    private void TransmitBackground(byte[] frame, TxTarget target)
    {
        _ = Task.Run(async () =>
        {
            try { await TransmitFrameAsync(frame, target).ConfigureAwait(false); }
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
    private RelayContext? BuildRelayContext(RxSource source)
    {
        if (!RoutingRelayEnabled) return null;
        if (!CanTransmit) return null;

        return new RelayContext(
            MyRole ?? string.Empty,
            EffectiveRebroadcastMode,
            _rxHost.MyNodeNum,
            // The contention delay is worked out for the preset the packet
            // arrived on, since that is where the rebroadcast goes.
            source.Preset ?? SelectedPreset,
            _nodeStore.Get,
            _nodeStore.All,
            MyIsLicensed);
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
    /// Two shapes of ack, following firmware's ReliableRouter:
    ///
    /// A first-sight direct *text message* is acked reliably — want_ack set on
    /// the ack itself, and retried until the peer confirms it. That ack is the
    /// only thing that flips the sender's message from pending to delivered, so
    /// losing it to one collision is a visible failure for a message we in fact
    /// received and displayed.
    ///
    /// Anything else, and every repeat ack, goes out once and plain. A repeat
    /// in particular is capped at hop limit 0 (see <see cref="AckRequest"/>).
    ///
    /// A packet we could not decrypt is answered with a NAK instead. It always
    /// goes out plain on the primary channel: we do not know which channel the
    /// request used, and PKC is unavailable precisely because the missing key is
    /// what we are complaining about.
    /// </summary>
    private void SendAck(AckRequest request)
    {
        var header = request.Header;
        if (!CanTransmit || _rxHost.MyNodeNum == 0) return;
        if (header.IsBroadcast || header.To != _rxHost.MyNodeNum) return;
        if (!header.WantAck) return;

        bool nak = request.ErrorReason != RoutingError.None;
        bool reliable = request.TextMessage && !request.Duplicate && !nak;
        byte hopLimit = request.Duplicate ? (byte)0 : ResponseHopLimit(header, request.HasBitfield);
        // Back out on the settings the packet came in on.
        var source = request.Source ?? PrimarySource();
        var target = TargetForSource(source);

        // Firmware guards the repeat ack with !findInTxQueue(p->from, p->id):
        // no second ack while the first is still waiting to go out. MeshRF has
        // no queue keyed that way — TransmitFrameAsync serialises on a
        // semaphore and waits for a clear channel — so the equivalent is to
        // remember what we just acked. A sender repeating faster than our
        // transmitter drains would otherwise collect an ack per repeat.
        if (request.Duplicate)
        {
            var now = DateTime.UtcNow;
            if (_recentAcks.TryGetValue((header.From, header.PacketId), out var last) &&
                now - last < RepeatAckSuppression)
                return;
            _recentAcks[(header.From, header.PacketId)] = now;
            PruneRecentAcks(now);
        }

        try
        {
            uint packetId = NextPacketId();
            byte[]? frame = null;

            if (request.Pkc && !nak)
            {
                // A PKC message must be acked over PKC, sealed back to the
                // sender with our private key and their public key.
                var myPriv = TryParseKeyBase64(MyPrivateKey);
                var peerPub = TryParseHex(_rxHost.PublicKeyHexFor(header.From));
                if (myPriv.Length == 32 && peerPub.Length == 32)
                    frame = MeshEncoder.EncodePkcRouting(
                        _rxHost.MyNodeNum, header.From, packetId, header.PacketId,
                        myPriv, peerPub, errorReason: request.ErrorReason,
                        hopLimit: hopLimit, wantAck: reliable);
            }
            else
            {
                // On the list of the listener that heard the packet: the
                // sender is on that mesh, and an ack sealed with another
                // list's key is noise to it.
                var channel = nak
                    ? _rxHost.ChannelFor(source, null)
                    : _rxHost.ChannelFor(source, request.ChannelName);
                if (channel is not null)
                    frame = MeshEncoder.EncodeRouting(
                        channel, _rxHost.MyNodeNum, header.From, packetId, header.PacketId,
                        errorReason: request.ErrorReason, hopLimit: hopLimit, wantAck: reliable);
            }

            if (frame is null) return;
            TransmitBackground(frame, target);
            if (nak)
                _rxHost.Log($"  NAK (reason={request.ErrorReason}) to {header.FromId} for id {header.PacketId:x8}");
            if (reliable)
                _ackRetransmits[packetId] = new AckRetransmit(
                    frame, DateTime.UtcNow + AckRetxInterval, AckRetxAttempts, target);
        }
        catch (Exception ex)
        {
            StatusText = $"Ack failed: {ex.Message}";
        }
    }

    /// <summary>How long one repeat ack suppresses the next for the same
    /// packet. Long enough to cover a queued transmission and the airtime it
    /// waits for, short enough that a sender still genuinely unheard after it
    /// gets answered again.</summary>
    private static readonly TimeSpan RepeatAckSuppression = TimeSpan.FromSeconds(10);

    /// <summary>When each (sender, packet) was last repeat-acked. Stands in for
    /// firmware's findInTxQueue; see SendAck.</summary>
    private readonly Dictionary<(uint From, uint PacketId), DateTime> _recentAcks = new();

    private void PruneRecentAcks(DateTime now)
    {
        if (_recentAcks.Count < 64) return;
        foreach (var key in _recentAcks.Where(kv => now - kv.Value >= RepeatAckSuppression)
                                       .Select(kv => kv.Key).ToList())
            _recentAcks.Remove(key);
    }

    // -- Reliable ack retransmission -----------------------------------------

    /// <param name="NextTxUtc">When to send the next copy.</param>
    /// <param name="Remaining">Copies left after the one already sent.</param>
    /// <param name="Target">What the copies go out on: the same as the first.</param>
    private sealed record AckRetransmit(byte[] Frame, DateTime NextTxUtc, int Remaining, TxTarget Target);

    /// <summary>Acks sent with want_ack that the peer hasn't confirmed yet,
    /// keyed by the ack's own packet id.</summary>
    private readonly Dictionary<uint, AckRetransmit> _ackRetransmits = new();

    /// <summary>Firmware's NUM_RELIABLE_RETX is 3 total transmissions, so two
    /// follow-ups after the original.</summary>
    private const int AckRetxAttempts = 2;

    /// <summary>Gap between copies. Firmware derives this from the packet's
    /// airtime; a flat delay is enough here and stays clear of the peer's own
    /// retry cadence, so the two don't collide repeatedly.</summary>
    private static readonly TimeSpan AckRetxInterval = TimeSpan.FromSeconds(9);

    /// <summary>The peer answered one of our packets. If it was a reliable ack
    /// we're still repeating, it landed — stop sending it.</summary>
    private void CancelAckRetransmit(uint packetId) => _ackRetransmits.Remove(packetId);

    /// <summary>Send the next copy of any unconfirmed reliable ack that is due,
    /// and retire the ones that have run out of copies. Driven by the poll tick
    /// alongside <c>SweepPendingAcks</c>.</summary>
    private void SweepAckRetransmits()
    {
        if (_ackRetransmits.Count == 0) return;

        var now = DateTime.UtcNow;
        List<uint>? due = null;
        foreach (var kv in _ackRetransmits)
        {
            if (now < kv.Value.NextTxUtc) continue;
            (due ??= []).Add(kv.Key);
        }
        if (due is null) return;

        foreach (var id in due)
        {
            if (!_ackRetransmits.TryGetValue(id, out var retx)) continue;
            // Out of copies, or we can no longer transmit at all: give up
            // rather than hold the frame for a radio that may never come back.
            if (retx.Remaining <= 0 || !CanTransmit)
            {
                _ackRetransmits.Remove(id);
                continue;
            }

            _ackRetransmits[id] = retx with
            {
                NextTxUtc = now + AckRetxInterval,
                Remaining = retx.Remaining - 1,
            };
            TransmitBackground(retx.Frame, retx.Target);
        }
    }

    /// <summary>Hop limit for a reply to <paramref name="header"/>, against our
    /// configured limit. See <see cref="ReplyHops"/> for the rules.</summary>
    private byte ResponseHopLimit(MeshHeader header, bool hasBitfield)
        => ReplyHops.ForResponse(header, hasBitfield, HopLimit);

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

    /// <summary>Time budget for one drain, so a burst cannot hold the UI
    /// thread. There is no count cap to go with it: the native queue is
    /// bounded and drops its oldest line once full, so a drain that stops
    /// short of empty while lines are still arriving is what loses payloads.
    /// </summary>
    private const double MaxRxDrainMsPerTick = 4.0;

    private static readonly Regex PreamblePeakRegex = new(
        @"peak=(?<peak>-?\d+(?:\.\d+)?)dB", RegexOptions.Compiled);

    /// <summary>Peak-above-noise from the last preamble on each listener,
    /// used as the SNR for the payload that follows it there. Per listener,
    /// because the lines of several listeners' frames interleave in the
    /// queue.</summary>
    private readonly Dictionary<int, float?> _lastPreamblePeakDb = new();

    private float? TakePreamblePeak(int listener) =>
        _lastPreamblePeakDb.Remove(listener, out var peak) ? peak : null;

    // -- Listeners and where a transmission goes ------------------------------

    /// <summary>The receiver's listeners by index, as it was started; the
    /// primary alone until a multi-preset start.</summary>
    private RxSource[] _rxSources = [];

    private RxSource PrimarySource() => RxSource.Primary(SelectedPreset, IsCustomLoraParams, CenterFreqMHz);

    private RxSource SourceFor(int listener) =>
        listener >= 0 && listener < _rxSources.Length ? _rxSources[listener] : PrimarySource();

    /// <summary>The toolbar configuration as a transmit target.</summary>
    private TxTarget PrimaryTarget()
    {
        var hz = (ulong)Math.Round(CenterFreqMHz * 1_000_000.0);
        return IsCustomLoraParams
            ? TxTarget.ForParams(OverrideSf, (uint)Math.Round(OverrideBwKhz * 1000.0), OverrideCr, hz, 0)
            : TxTarget.ForPreset(SelectedPreset, hz, 0);
    }

    /// <summary>What a reply to a packet heard on <paramref name="source"/>
    /// goes out on: the same settings.</summary>
    private TxTarget TargetForSource(RxSource source)
    {
        if (source.IsPrimary || source.Preset is null) return PrimaryTarget();
        return TxTarget.ForPreset(source.Preset.Value, (ulong)Math.Round(source.FreqMHz * 1_000_000.0), source.Listener);
    }

    /// <summary>What a packet to a node goes out on: the settings it was last
    /// heard on, when that preset is one the receiver is listening on now,
    /// so the answer can be heard; otherwise the primary.</summary>
    private TxTarget TargetForNode(uint nodeNum)
    {
        var heardOn = _nodeStore.Get(nodeNum)?.HeardOnPreset;
        if (string.IsNullOrEmpty(heardOn)) return PrimaryTarget();
        foreach (var s in _rxSources)
            if (!s.IsPrimary && s.PresetName == heardOn) return TargetForSource(s);
        return PrimaryTarget();
    }

    /// <summary>The target of a list of channels: the listener whose preset
    /// owns it, or the primary for its own list.</summary>
    private TxTarget TargetForList(string? listName)
    {
        if (string.IsNullOrEmpty(listName)) return PrimaryTarget();
        foreach (var s in _rxSources)
            if (!s.IsPrimary && s.PresetName == listName) return TargetForSource(s);
        return PrimaryTarget();
    }

    /// <summary>
    /// Where a finished frame goes, read off the frame itself: a packet to a
    /// node goes out on that node's settings, a broadcast on the preset whose
    /// list holds the channel it was sealed with. Every send in the app that
    /// does not name its target passes through here, so auto-reports, which
    /// are sealed with the primary's channels, land on the primary.
    /// </summary>
    private TxTarget TargetForFrame(byte[] frame)
    {
        if (!MeshHeader.TryParse(frame, out var header)) return PrimaryTarget();
        if (!header.IsBroadcast && header.To != 0) return TargetForNode(header.To);
        return TargetForList(_rxHost.ListNameForChannelHash(header.ChannelHash));
    }

    /// <summary>Bandwidth of a listener's channel, for the overlap test.</summary>
    private uint BandwidthHz(RxSource s) => s.IsCustom || s.Preset is null
        ? (uint)Math.Round(OverrideBwKhz * 1000.0)
        : (uint)Math.Round(LoraParamsHelper.FromPreset(s.Preset.Value, ChannelPlan.IsWideLora(SelectedRegion)).BwKhz * 1000.0);

    private bool ChannelsOverlap(int a, int b)
    {
        var sa = SourceFor(a);
        var sb = SourceFor(b);
        double gapHz = Math.Abs(sa.FreqMHz - sb.FreqMHz) * 1e6;
        return gapHz < (BandwidthHz(sa) + BandwidthHz(sb)) / 2.0;
    }

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

    /// <summary>Drains queued demodulator events until the queue is empty or
    /// the time budget is spent.</summary>
    private void DrainDemodEvents()
    {
        if (_core is null) return;

        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= MaxRxDrainMsPerTick) break;

            var pulled = _core.PullEvent();
            if (pulled is null) break;
            var ev = pulled.Value.Text;
            // A line about the receiver as a whole (-1) is filed under the
            // primary; nothing below reads its index for such a line anyway.
            int listener = Math.Max(0, pulled.Value.Listener);
            var nowUtc = DateTime.UtcNow;

            // The modem indents the lines of a frame under its preamble, so
            // what kind of line this is has to be read past that indent. The
            // log keeps the indent.
            var kind = ev.TrimStart();

            if (!IsHighRateDemodEvent(kind)) _rxHost.Log(ev);

            if (kind.StartsWith("preamble", StringComparison.Ordinal))
            {
                // A preamble marks the start of a frame: hold off transmitting,
                // and keep its peak-above-noise as the SNR for the payload.
                MarkRxBusy(listener, nowUtc, RxBusyDefaultHold);
                var pm = PreamblePeakRegex.Match(kind);
                if (pm.Success && float.TryParse(pm.Groups["peak"].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var peak))
                    _lastPreamblePeakDb[listener] = peak;
            }
            else if (kind.StartsWith("payload", StringComparison.Ordinal))
            {
                MarkRxFrameComplete(listener, nowUtc);
            }

            DecodePayloadIfPossible(kind, listener);

            // The packet spectrogram follows the primary: its IQ ring is the
            // primary's channel.
            if (listener == 0 && IsCrcOkPayload(kind)) PacketDecoded?.Invoke();
        }
    }
}
