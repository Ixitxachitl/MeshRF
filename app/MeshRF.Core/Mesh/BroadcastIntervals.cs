// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// Firmware's <c>Default::getConfiguredOrDefaultMsScaled</c>: how far apart a
/// periodic broadcast actually lands once the mesh is busy.
///
/// A configured interval is a floor, not a promise. Past forty online nodes
/// firmware stretches every node's schedule in proportion to how long one packet
/// occupies the air, so a mesh that doubles in size doesn't double the beacons
/// competing for the same airtime. Without this a MeshRF node keeps its
/// configured cadence while every firmware node around it backs off — it would
/// take a steadily larger share of a channel that is already congested.
/// </summary>
public static class BroadcastIntervals
{
    /// <summary>Firmware's threshold: below this the mesh is not busy enough to
    /// be worth throttling.</summary>
    public const int CongestionFreeNodeCount = 40;

    /// <summary>Firmware counts a node as online if it was heard within two
    /// hours (<c>NUM_ONLINE_SECS</c>).</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// Firmware <c>min_node_info_broadcast_secs</c>, applied unconditionally in
    /// <c>AdminModule</c> — every channel, every role, every region. A NodeInfo
    /// carries a name, a key and a hardware model, none of which change; a
    /// faster beacon is airtime spent repeating what the mesh already knows.
    /// </summary>
    public const int MinNodeInfoSeconds = 60 * 60;

    /// <summary>
    /// Firmware <c>default_broadcast_smart_minimum_interval_secs</c>: the gap a
    /// smart position keeps when the setting is left unset. Nothing to do with
    /// the default channel — zero in firmware's config means "unset", so a
    /// private channel gets these five minutes too.
    /// </summary>
    public const int DefaultSmartPositionSeconds = 5 * 60;

    /// <summary>
    /// Firmware <c>Default::getConfiguredOrDefault</c> for that gap. Someone who
    /// wants every fix sent as it arrives types one second, not zero.
    /// </summary>
    public static int SmartPositionGapSeconds(int configuredSeconds) =>
        configuredSeconds > 0 ? configuredSeconds : DefaultSmartPositionSeconds;

    /// <summary>
    /// Roles firmware exempts from scaling: the routers because their intervals
    /// are already long, and the tracker/sensor family because their whole
    /// purpose is a timely position or reading.
    /// </summary>
    public static bool IsExempt(string? role) =>
        Canonical(role) is "ROUTER" or "ROUTERLATE" or "SENSOR" or "TRACKER" or "TAKTRACKER";

    /// <summary>
    /// Firmware's <c>congestionScalingCoefficient</c>. The throttling factor is
    /// the symbol time in disguise — a slow preset holds the channel longer per
    /// packet, so it backs off harder for the same node count.
    /// </summary>
    public static double CongestionScalingCoefficient(int onlineNodes, LoraPreset preset, bool wideLora = false)
    {
        if (onlineNodes <= CongestionFreeNodeCount) return 1.0;

        var p = LoraParamsHelper.FromPreset(preset, wideLora);
        double throttlingFactor = Math.Pow(2.0, p.Sf) / (p.BwKhz * 100.0);
        return 1.0 + (onlineNodes - CongestionFreeNodeCount) * throttlingFactor;
    }

    /// <summary>
    /// The interval to actually wait, in seconds, for a role that scales.
    /// </summary>
    /// <param name="configuredSeconds">What the user set.</param>
    /// <param name="onlineNodes">Nodes heard inside <see cref="OnlineWindow"/>.</param>
    public static int ScaledSeconds(int configuredSeconds, string? role, int onlineNodes,
                                    LoraPreset preset, bool wideLora = false)
    {
        if (configuredSeconds <= 0) return configuredSeconds;
        if (IsExempt(role)) return configuredSeconds;

        double scaled = configuredSeconds * CongestionScalingCoefficient(onlineNodes, preset, wideLora);
        // Firmware saturates at INT32_MAX because the result is consumed as a
        // signed millisecond count downstream; we hold seconds, so the ceiling
        // is that many milliseconds' worth.
        return scaled >= int.MaxValue / 1000.0 ? int.MaxValue / 1000 : (int)Math.Round(scaled);
    }

    private static string Canonical(string? s) =>
        (s ?? string.Empty).Trim().Replace("_", string.Empty).ToUpperInvariant();
}
