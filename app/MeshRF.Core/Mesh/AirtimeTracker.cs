// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// Local approximations of the two utilisation figures a Meshtastic node
/// reports in its device metrics:
///
/// <list type="bullet">
/// <item><c>channel_utilization</c> — all airtime heard or sent in the last minute.</item>
/// <item><c>air_util_tx</c> — our own transmit airtime over the last hour.</item>
/// </list>
///
/// Firmware counts these from the radio's own timers. We have no such counter,
/// so each frame's time on air is estimated from the preset and length and kept
/// in a rolling window. The numbers are an estimate and are only as good as the
/// frames we actually decoded — traffic we could not hear is traffic we cannot
/// count.
/// </summary>
public sealed class AirtimeTracker
{
    private readonly record struct Sample(DateTime Utc, int Ms, bool IsTx);

    // An hour of frames: the longest window either figure looks at, so anything
    // older can never contribute again.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private readonly Queue<Sample> _samples = new();
    private readonly object _gate = new();

    /// <summary>
    /// Notes a frame's time on air. Called for our own transmissions and for
    /// every frame decoded, since channel utilisation counts both.
    /// </summary>
    public void Record(int milliseconds, bool isTx, DateTime? nowUtc = null)
    {
        if (milliseconds <= 0) return;
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_gate)
        {
            _samples.Enqueue(new Sample(now, milliseconds, isTx));
            TrimLocked(now);
        }
    }

    /// <summary>Both figures as percentages, 0-100.</summary>
    public void Compute(out float channelUtilPct, out float airUtilTxPct, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        const double minuteMs = 60_000.0;
        const double hourMs = 3_600_000.0;

        double channelMs = 0, txMs = 0;
        lock (_gate)
        {
            TrimLocked(now);
            foreach (var s in _samples)
            {
                var age = now - s.Utc;
                if (age <= TimeSpan.FromMinutes(1)) channelMs += s.Ms;
                if (s.IsTx && age <= MaxAge) txMs += s.Ms;
            }
        }

        channelUtilPct = (float)Math.Clamp(channelMs / minuteMs * 100.0, 0.0, 100.0);
        airUtilTxPct = (float)Math.Clamp(txMs / hourMs * 100.0, 0.0, 100.0);
    }

    /// <summary>Frames currently in the window. For tests and diagnostics.</summary>
    public int SampleCount
    {
        get { lock (_gate) return _samples.Count; }
    }

    private void TrimLocked(DateTime nowUtc)
    {
        while (_samples.Count > 0 && nowUtc - _samples.Peek().Utc > MaxAge)
            _samples.Dequeue();
    }

    /// <summary>
    /// Time on air for a LoRa frame, by the Semtech formula — the same one the
    /// native modem uses to bound a burst. Low-data-rate optimisation is
    /// inferred from the symbol time, as firmware does, rather than passed in.
    /// </summary>
    public static int EstimateAirtimeMs(int spreadingFactor, double bandwidthHz, int codingRate, int payloadBytes)
    {
        if (payloadBytes <= 0 || bandwidthHz <= 0) return 0;

        double sf = spreadingFactor;
        double cr = codingRate - 4.0; // 5..8 -> 1..4
        if (cr < 1.0) return 0;

        double tSym = Math.Pow(2.0, sf) / bandwidthHz;
        int de = tSym >= 0.016 ? 1 : 0; // LDRO once a symbol reaches 16 ms
        const int ih = 0;               // explicit header
        const int crc = 1;

        double numerator = 8.0 * payloadBytes - 4.0 * sf + 28.0 + 16.0 * crc - 20.0 * ih;
        double denominator = 4.0 * (sf - 2.0 * de);
        double payloadSym = 8.0;
        if (denominator > 0)
            payloadSym += Math.Max(Math.Ceiling(numerator / denominator) * (cr + 4.0), 0.0);

        const double preambleSym = 8.0 + 4.25;
        return (int)Math.Round((preambleSym + payloadSym) * tSym * 1000.0, MidpointRounding.AwayFromZero);
    }
}
