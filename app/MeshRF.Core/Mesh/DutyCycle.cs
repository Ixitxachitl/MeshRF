// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// The regional transmit budget, mirroring firmware's
/// <c>getEffectiveDutyCycle()</c> and the abort in <c>Router::send</c>.
///
/// Most regions place no meaningful limit, but the EU bands do, and there the
/// role matters: EU_866 allows a router ten percent of the hour and everyone
/// else two and a half. A node that ignores this transmits legally-ineligible
/// traffic and crowds out the neighbours that are obeying it.
/// </summary>
public static class DutyCycle
{
    /// <summary>Firmware's <c>polite_duty_cycle_percent</c>: background chatter
    /// (a NodeInfo we volunteered, not a message the user sent) gets only half
    /// the budget, leaving headroom for traffic that matters.</summary>
    public const int PolitePercent = 50;

    /// <summary>
    /// The region's hourly transmit ceiling as a percentage, before the role
    /// adjustment. 100 means the region declares no limit.
    /// </summary>
    public static double RegionPercent(Region region) => region switch
    {
        Region.EU_866 => 2.5,
        Region.EU_868 or Region.EU_N_868 or Region.EU_433 or
        Region.UA_433 or Region.TH => 10.0,
        _ => 100.0,
    };

    /// <summary>
    /// Firmware <c>getEffectiveDutyCycle()</c>. EU_866 is the one region whose
    /// budget depends on the role — a router there is a fixed installation held
    /// to the 10% class, while a mobile node gets 2.5%.
    /// </summary>
    public static double EffectivePercent(Region region, string? role)
    {
        if (region == Region.EU_866)
            return Canonical(role) is "ROUTER" or "ROUTERLATE" ? 10.0 : 2.5;

        return RegionPercent(region);
    }

    /// <summary>
    /// Whether a transmit is within budget. <paramref name="airUtilTxPct"/> is
    /// our own transmit airtime over the last hour — the same figure we report
    /// as <c>air_util_tx</c>.
    /// </summary>
    /// <param name="polite">Background traffic we chose to send, which firmware
    /// holds to half the budget (<c>isTxAllowedAirUtil</c>). User-initiated
    /// sends pass false and get the whole allowance.</param>
    public static bool IsTxAllowed(Region region, string? role, double airUtilTxPct,
                                   bool polite = false, bool overridden = false)
    {
        if (overridden) return true;
        double limit = EffectivePercent(region, role);
        if (limit >= 100.0) return true;
        if (polite) limit = limit * PolitePercent / 100.0;
        return airUtilTxPct < limit;
    }

    /// <summary>
    /// Firmware <c>AirTime::getSilentMinutes</c>, approximated: how long before
    /// the hour-long window has aged out enough transmit time to be under
    /// budget again. Firmware walks its per-minute buckets; we only hold the
    /// total, so this assumes the airtime is spread evenly across the hour —
    /// close enough for a "try again in N minutes" message.
    /// </summary>
    public static int SilentMinutes(double airUtilTxPct, double limitPct)
    {
        if (limitPct <= 0 || airUtilTxPct <= limitPct) return 0;
        double fractionToShed = (airUtilTxPct - limitPct) / airUtilTxPct;
        return Math.Clamp((int)Math.Ceiling(fractionToShed * 60.0), 1, 60);
    }

    private static string Canonical(string? s) =>
        (s ?? string.Empty).Trim().Replace("_", string.Empty).ToUpperInvariant();
}
