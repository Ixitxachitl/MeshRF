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
/// Firmware does not measure these either — <c>RadioLibInterface</c> logs
/// <c>getTimeOnAir()</c>, which works the datasheet equation from the configured
/// SF/BW/CR rather than reading a hardware timer. So the per-frame figure here
/// is not an approximation of firmware's: it is the same calculation, carried
/// out in the same integer steps, and agrees with it exactly.
///
/// What is genuinely ours alone is the coverage: these totals count the frames
/// we decoded, and traffic we could not hear is traffic we cannot count. That
/// understates channel utilisation on a mesh we hear only part of, and does not
/// affect <c>air_util_tx</c>, which is only ever our own transmissions.
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
    /// Time on air for a LoRa frame, by the Semtech formula — the same one
    /// firmware arrives at. Its figure is not measured either: RadioLib's
    /// <c>getTimeOnAir</c> works the datasheet equation from the configured
    /// SF/BW/CR, so matching it is a matter of feeding in the same parameters.
    /// Low-data-rate optimisation is inferred from the symbol time at the same
    /// 16 ms threshold RadioLib uses.
    /// </summary>
    /// <param name="preambleSymbols">Firmware transmits a 16-symbol preamble
    /// (12 above 2 GHz), not the radio default of 8 — see
    /// <see cref="PreambleSymbols"/>.</param>
    public static int EstimateAirtimeMs(int spreadingFactor, double bandwidthHz, int codingRate, int payloadBytes,
                                        int preambleSymbols = PreambleSymbols)
    {
        if (payloadBytes <= 0 || bandwidthHz <= 0) return 0;
        if (codingRate < 5 || codingRate > 8) return 0;
        if (spreadingFactor < 5 || spreadingFactor > 12) return 0;

        // RadioLib works in microseconds with integer arithmetic, carrying the
        // .25 terms pre-multiplied by four. Reproduced step for step rather
        // than rewritten in floating point: the two agree to the microsecond
        // this way, and the rounding of a rewrite would not.
        long bwKhzTimes10 = (long)Math.Round(bandwidthHz / 100.0);
        if (bwKhzTimes10 <= 0) return 0;
        long symbolLengthUs = (10_000L << spreadingFactor) / bwKhzTimes10;

        int sfCoeff1X4 = 17;   // 4.25 * 4
        int sfCoeff2 = 8;
        if (spreadingFactor is 5 or 6) { sfCoeff1X4 = 25; sfCoeff2 = 0; }

        const int bitsPerCrc = 16;      // CRC on
        const int symbolHeader = 20;    // explicit header

        int bitCount = 8 * payloadBytes + bitsPerCrc - 4 * spreadingFactor + sfCoeff2 + symbolHeader;
        if (bitCount < 0) bitCount = 0;

        // Low-data-rate optimisation once a symbol reaches 16 ms, the same
        // threshold RadioLib's automatic mode uses.
        bool ldrOptimize = symbolLengthUs >= 16_000;
        int sfDivisor = 4 * (ldrOptimize ? spreadingFactor - 2 : spreadingFactor);

        long preCodedSymbols = (bitCount + sfDivisor - 1) / sfDivisor;   // integer ceiling
        long symbolsX4 = (preambleSymbols + 8L) * 4 + sfCoeff1X4 + preCodedSymbols * codingRate * 4;

        long airtimeUs = symbolLengthUs * symbolsX4 / 4;
        // Truncated, not rounded: firmware divides the microseconds by 1000 as
        // integers before logging the result.
        return (int)(airtimeUs / 1000);
    }

    /// <summary>
    /// Firmware's <c>preambleLength</c>: 16 symbols, not the radio default of 8,
    /// so a receiver has longer to wake and catch the preamble. 12 above 2 GHz.
    /// </summary>
    /// <remarks>
    /// Worth stating because it is eight symbols of airtime on every packet the
    /// mesh sends — on MediumFast that is 16 ms, close to a tenth of a frame.
    /// Assuming the default 8 made every figure computed here read low.
    /// </remarks>
    public const int PreambleSymbols = 16;
    public const int WideLoraPreambleSymbols = 12;

    public static int PreambleSymbolsFor(bool wideLora) =>
        wideLora ? WideLoraPreambleSymbols : PreambleSymbols;
}
