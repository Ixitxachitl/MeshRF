// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;

namespace MeshRF.Mesh;

/// <summary>
/// One channel this station beacons on, named the way the channel store names
/// channels: the mesh's list and the index within it.
/// </summary>
/// <remarks>
/// A channel is enough to name a destination on its own. Which mesh it is on
/// is which list holds it, and that decides the preset and the frequency the
/// beacon goes out with — so there is nothing to keep in step between the two
/// halves, and no way to name a channel on one mesh and transmit it on
/// another's settings, which would put a frame on the air nobody there can
/// read.
/// </remarks>
public sealed class BeaconTarget
{
    /// <summary>The mesh's channel list: the preset it is named for.</summary>
    public string Preset { get; set; } = string.Empty;

    /// <summary>Index within that list.</summary>
    public int ChannelIndex { get; set; }

    public bool Matches(ChannelConfig channel) =>
        string.Equals(channel.Preset, Preset, StringComparison.Ordinal) && channel.Index == ChannelIndex;

    public bool SameAs(BeaconTarget other) =>
        string.Equals(other.Preset, Preset, StringComparison.Ordinal) && other.ChannelIndex == ChannelIndex;
}

/// <summary>
/// What this station broadcasts as a beacon, and how often.
/// </summary>
/// <remarks>
/// Mirrors firmware's <c>MeshBeaconConfig</c> closely enough to interoperate,
/// with one deliberate difference: firmware treats an empty destination list
/// as "send one on the running preset over the primary channel", and this
/// sends nothing at all. A beacon names a mesh to strangers, and which meshes
/// it goes out on is not a thing to be inferred.
/// </remarks>
public sealed class BeaconSettings
{
    /// <summary>The one switch. Off, nothing is broadcast whatever else is
    /// configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>The human-readable part, capped at
    /// <see cref="BeaconPolicy.MaxMessageBytes"/> bytes of UTF-8 by the
    /// sending firmware.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Seconds between broadcasts, floored at
    /// <see cref="BeaconPolicy.MinIntervalSeconds"/>.</summary>
    public int IntervalSeconds { get; set; } = BeaconPolicy.MinIntervalSeconds;

    /// <summary>Whether a channel is being advertised at all.</summary>
    public bool HasOfferChannel { get; set; }

    /// <summary>The advertised channel's mesh, when one is advertised.</summary>
    public string OfferChannelPreset { get; set; } = string.Empty;

    /// <summary>The advertised channel's index in that mesh's list.</summary>
    public int OfferChannelIndex { get; set; }

    /// <summary>The channels this beacon goes out on, one copy each.</summary>
    public List<BeaconTarget> Targets { get; set; } = new();

    /// <summary>When the last broadcast went out, so a restart does not start
    /// the interval over and put one on the air on every launch.</summary>
    public DateTime? LastSentUtc { get; set; }

    public BeaconSettings Clone() => new()
    {
        Enabled = Enabled,
        Message = Message,
        IntervalSeconds = IntervalSeconds,
        HasOfferChannel = HasOfferChannel,
        OfferChannelPreset = OfferChannelPreset,
        OfferChannelIndex = OfferChannelIndex,
        LastSentUtc = LastSentUtc,
        Targets = Targets.Select(t => new BeaconTarget { Preset = t.Preset, ChannelIndex = t.ChannelIndex })
                         .ToList(),
    };
}

/// <summary>
/// The rules a beacon has to keep, taken from the firmware that receives them.
/// </summary>
public static class BeaconPolicy
{
    /// <summary>Firmware's <c>default_mesh_beacon_min_broadcast_interval_secs</c>.
    /// A beacon is an advertisement to strangers; an hour is the floor.</summary>
    public const int MinIntervalSeconds = 3600;

    /// <summary>What <c>MeshBeaconConfig.broadcast_message</c> holds, less the
    /// terminator nanopb reserves (max_size:101).</summary>
    public const int MaxMessageBytes = 100;

    /// <summary>Firmware sends every beacon zero-hop so an advertisement
    /// cannot be reflooded across a mesh it is only announcing itself to.</summary>
    public const byte HopLimit = 0;

    public static int ClampInterval(int seconds) => Math.Max(MinIntervalSeconds, seconds);

    /// <summary>How many bytes of UTF-8 a message occupies on the wire, which
    /// is what firmware's cap counts — not characters.</summary>
    public static int MessageBytes(string? message) =>
        string.IsNullOrEmpty(message) ? 0 : System.Text.Encoding.UTF8.GetByteCount(message);

    public static bool MessageFits(string? message) => MessageBytes(message) <= MaxMessageBytes;

    /// <summary>
    /// Cuts a message to the byte cap without splitting a character in half,
    /// which would put invalid UTF-8 on the air.
    /// </summary>
    public static string TrimMessage(string? message)
    {
        if (string.IsNullOrEmpty(message) || MessageFits(message)) return message ?? string.Empty;

        var encoding = System.Text.Encoding.UTF8;
        int chars = message.Length;
        while (chars > 0 && encoding.GetByteCount(message[..chars]) > MaxMessageBytes) chars--;
        return message[..chars];
    }

    /// <summary>
    /// Whether there is anything to broadcast: a beacon with neither words nor
    /// a mesh to advertise says nothing, and firmware skips it too
    /// ("Beacon: empty msg, no offer, skip").
    /// </summary>
    public static bool HasAnythingToSay(BeaconSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Message) || settings.HasOfferChannel;

    /// <summary>
    /// Whether the beacon would actually transmit as configured. The
    /// destination list is the part with no default: with nothing on it there
    /// is no mesh to speak on, so nothing goes out.
    /// </summary>
    public static bool WouldBroadcast(BeaconSettings settings) =>
        settings.Enabled && settings.Targets.Count > 0 && HasAnythingToSay(settings);

    /// <summary>
    /// The destinations to send on, in order, with repeats dropped. Two
    /// entries naming one channel are one transmission, as they are in
    /// firmware — which dedups on the radio settings each target resolves to.
    /// </summary>
    public static IReadOnlyList<BeaconTarget> DistinctTargets(BeaconSettings settings)
    {
        var seen = new List<BeaconTarget>();
        foreach (var target in settings.Targets)
            if (!seen.Any(s => s.SameAs(target)))
                seen.Add(target);
        return seen;
    }

    /// <summary>
    /// When the next broadcast is due, given when the last one went out. A
    /// station that has never beaconed is due at once; one that has waits out
    /// the rest of its interval, so restarting the app does not put a beacon
    /// on the air every launch.
    /// </summary>
    public static DateTime NextDueUtc(BeaconSettings settings, DateTime nowUtc) =>
        settings.LastSentUtc is { } last
            ? last.AddSeconds(ClampInterval(settings.IntervalSeconds))
            : nowUtc;
}
