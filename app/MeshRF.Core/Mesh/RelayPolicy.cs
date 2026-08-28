// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Nodes;

namespace MeshRF.Mesh;

/// <summary>
/// Everything the relay decision needs from the host, so the policy below can
/// stay pure and testable.
/// </summary>
public sealed record RelayContext(
    string Role,
    string RebroadcastMode,
    uint MyNodeNum,
    LoraPreset Preset,
    Func<uint, NodeRecord?> LookupNode,
    Func<IEnumerable<NodeRecord>> AllNodes,
    bool IsLicensed = false);

/// <summary>
/// Whether and when to rebroadcast an overheard packet, mirroring Meshtastic
/// firmware's flooding router. Getting this right matters: a node that relays
/// by different rules than the mesh expects either drops traffic others rely on
/// it to carry, or adds copies the mesh has to absorb.
///
/// The rules come from firmware's Router/FloodingRouter and RadioInterface.
/// Names in the comments refer to the firmware functions being matched.
/// </summary>
public static class RelayPolicy
{
    // RadioInterface.h / RadioInterface.cpp getCWsize.
    private const int CwMin = 3;
    private const int CwMax = 8;
    private const double SnrMinDb = -20.0;
    private const double SnrMaxDb = 10.0;

    /// <summary>Firmware isRebroadcaster(): CLIENT_MUTE never relays.</summary>
    public static bool IsRoutingRoleEnabled(string? role) => Canonical(role) != "CLIENTMUTE";

    /// <summary>Ports firmware counts as "core" for CORE_PORTNUMS_ONLY.</summary>
    public static bool IsCorePort(PortNum port) => port switch
    {
        PortNum.TextMessage or PortNum.TextMessageCompressed or PortNum.Position or
        PortNum.NodeInfo or PortNum.Routing or PortNum.Telemetry or PortNum.Admin or
        PortNum.Alert or PortNum.KeyVerification or PortNum.StoreForward or
        PortNum.StoreForwardPlusPlus or PortNum.Traceroute or PortNum.Waypoint => true,
        _ => false,
    };

    /// <summary>
    /// Firmware's admin module coerces some role/mode combinations, so the
    /// configured mode isn't always the one that applies. Licensed operation
    /// coerces hardest of all.
    /// </summary>
    /// <param name="isLicensed">Amateur-radio operation. Firmware forces
    /// LOCAL_ONLY here (NodeDB.cpp) so a licensed station never rebroadcasts
    /// traffic it could not decrypt, which would be relaying encryption.</param>
    public static string EffectiveRebroadcastMode(string? role, string? mode, bool isLicensed = false)
    {
        string r = Canonical(role);
        string m = Canonical(string.IsNullOrWhiteSpace(mode) ? "ALL" : mode);

        if (isLicensed) return "LOCAL_ONLY";
        // NONE is not honoured for routers: they exist to relay.
        if (m == "NONE" && (r == "ROUTER" || r == "ROUTERLATE")) return "ALL";
        // ALL_SKIP_DECODING is repeater-only; other roles behave as ALL.
        if (m == "ALLSKIPDECODING" && r != "REPEATER") return "ALL";

        return m switch
        {
            "ALLSKIPDECODING" => "ALL_SKIP_DECODING",
            "LOCALONLY" => "LOCAL_ONLY",
            "KNOWNONLY" => "KNOWN_ONLY",
            "COREPORTNUMSONLY" => "CORE_PORTNUMS_ONLY",
            _ => m,
        };
    }

    /// <summary>Upper-cased with separators stripped, so the UI's "LocalOnly"
    /// and firmware's "LOCAL_ONLY" compare equal. Without this every multi-word
    /// mode silently fell through to the permissive default.</summary>
    private static string Canonical(string? s) =>
        (s ?? string.Empty).Trim().Replace("_", string.Empty).ToUpperInvariant();

    public static bool PassesRebroadcastPolicy(RelayContext ctx, MeshHeader header, MeshDecodeResult? result) =>
        EffectiveRebroadcastMode(ctx.Role, ctx.RebroadcastMode, ctx.IsLicensed) switch
        {
            "NONE" => false,
            "ALL" or "ALL_SKIP_DECODING" => true,
            // Local mesh only: an undecryptable packet is foreign traffic.
            "LOCAL_ONLY" => result is not null,
            "KNOWN_ONLY" => result is not null && ctx.LookupNode(header.From) is not null,
            "CORE_PORTNUMS_ONLY" => result is not null && IsCorePort(result.Port),
            _ => true,
        };

    /// <summary>
    /// Firmware's hop preservation: router-ish roles keep the hop limit when the
    /// previous relay was a favourited router, letting traffic cross a trusted
    /// backbone without spending hops. The first hop always decrements, or a
    /// packet could circulate without ever ageing out.
    /// </summary>
    public static bool ShouldDecrementHopLimit(RelayContext ctx, MeshHeader header)
    {
        int hopsAway = header.HopStart >= header.HopLimit ? header.HopStart - header.HopLimit : 0;
        if (hopsAway == 0) return true;

        if (!IsRouterish(ctx.Role)) return true;

        byte relayByte = header.RelayNode;
        if (relayByte == 0) return true;

        if (!ResolveUniqueLastByte(ctx, relayByte, out var relay)) return true;

        // Firmware re-checks the predicate on the single resolved node rather
        // than on whatever the scan happened to hit first.
        return !(relay!.Favorite && HasUser(relay) && IsRouterish(relay.Role));
    }

    /// <summary>
    /// Firmware <c>NodeDB::resolveUniqueLastByte</c>. <c>relay_node</c> carries
    /// only the low byte of the previous relay's node number, so on a dense mesh
    /// several nodes answer to it. Scanning for the first match makes hop
    /// preservation depend on node-store order and can credit the wrong station;
    /// reporting the collision and decrementing is the safe answer.
    /// </summary>
    /// <remarks>
    /// The candidate set is narrowed first, exactly as firmware does: only a
    /// direct neighbour, a favourite, or a router-role node is a plausible
    /// relay, so an unrelated distant node sharing the byte is not a collision.
    /// </remarks>
    public static bool ResolveUniqueLastByte(RelayContext ctx, byte lastByte, out NodeRecord? resolved)
    {
        resolved = null;
        foreach (var node in ctx.AllNodes())
        {
            if ((node.NodeNum & 0xFF) != lastByte) continue;
            if (!(node.HopsAway == 0 || node.Favorite || IsRouterish(node.Role))) continue;

            // A second plausible candidate shares this byte: ambiguous, and no
            // further scanning can change that.
            if (resolved is not null) { resolved = null; return false; }
            resolved = node;
        }
        return resolved is not null;
    }

    /// <summary>Firmware's <c>nodeInfoLiteHasUser</c>: a node we have only ever
    /// seen relay, with no NodeInfo of its own, is not a station we trust hops
    /// to.</summary>
    private static bool HasUser(NodeRecord node) =>
        !string.IsNullOrEmpty(node.UserId) || !string.IsNullOrEmpty(node.LongName);

    /// <summary>The roles firmware treats as backbone for hop preservation and
    /// next-hop relevance: ROUTER, ROUTER_LATE and CLIENT_BASE.</summary>
    public static bool IsRouterish(string? role) =>
        Canonical(role) is "ROUTER" or "ROUTERLATE" or "CLIENTBASE";

    /// <summary>
    /// Firmware FloodingRouter::roleAllowsCancelingDupe. Routers never abandon a
    /// scheduled rebroadcast just because another station transmitted first —
    /// they're the backbone and are expected to relay regardless. CLIENT_BASE
    /// gets the same treatment, but only for favourited traffic.
    /// </summary>
    public static bool RoleAllowsCancelingScheduledRelay(RelayContext ctx, MeshHeader header)
    {
        string role = Canonical(ctx.Role);
        if (role is "ROUTER" or "ROUTERLATE") return false;
        if (role == "CLIENTBASE") return !IsFromOrToFavoritedNode(ctx, header);
        return true;
    }

    public static bool IsFromOrToFavoritedNode(RelayContext ctx, MeshHeader header)
    {
        if (ctx.LookupNode(header.From)?.Favorite == true) return true;
        if (!header.IsBroadcast && ctx.LookupNode(header.To)?.Favorite == true) return true;
        return false;
    }

    /// <summary>
    /// Whether this packet is ours to rebroadcast at all. Ordered cheapest-first;
    /// each rejection is a case where relaying would duplicate traffic, loop it,
    /// or speak for a node that didn't ask us to.
    /// </summary>
    public static bool ShouldRelay(RelayContext ctx, MeshHeader header, MeshDecodeResult? result,
                                   bool senderIgnored)
    {
        if (ctx.MyNodeNum == 0) return false;
        if (header.From == ctx.MyNodeNum) return false;   // our own echo
        if (header.To == ctx.MyNodeNum) return false;     // we're the destination
        if (senderIgnored) return false;
        if (header.PacketId == 0) return false;
        if (header.HopLimit == 0) return false;           // spent
        if (!IsRoutingRoleEnabled(ctx.Role)) return false;

        // next_hop names a specific relay; anyone else staying quiet is what
        // makes directed routing quieter than flooding.
        byte myRelayByte = (byte)(ctx.MyNodeNum & 0xFF);
        if (header.NextHop != 0 && header.NextHop != myRelayByte) return false;

        if (ctx.IsLicensed && InvolvesKnownUnlicensedNode(ctx, header)) return false;

        return PassesRebroadcastPolicy(ctx, header, result);
    }

    /// <summary>
    /// Firmware RoutingModule: a licensed station won't carry traffic for a node
    /// it knows to be unlicensed, because relaying it would put that station's
    /// call sign behind someone else's unlicensed transmission. A node that has
    /// never advertised either way is not assumed unlicensed — firmware's
    /// NotKnown passes the same test.
    /// </summary>
    public static bool InvolvesKnownUnlicensedNode(RelayContext ctx, MeshHeader header)
    {
        if (ctx.LookupNode(header.From)?.IsLicensed == false) return true;
        if (!header.IsBroadcast && ctx.LookupNode(header.To)?.IsLicensed == false) return true;
        return false;
    }

    /// <summary>Rewrites the frame for rebroadcast: new hop limit, next_hop
    /// cleared, relay_node set to us.</summary>
    public static byte[] BuildRelayFrame(RelayContext ctx, byte[] frame, byte nextHopLimit)
    {
        var relay = (byte[])frame.Clone();
        relay[12] = (byte)((relay[12] & 0xF8) | (nextHopLimit & 0x07));
        relay[14] = 0x00;
        relay[15] = (byte)(ctx.MyNodeNum & 0xFF);
        return relay;
    }

    // ---- CSMA-style relay delay (RadioInterface.cpp) ----

    private static double MapClamped(double value, double inMin, double inMax, double outMin, double outMax)
    {
        double t = Math.Clamp((value - inMin) / (inMax - inMin), 0.0, 1.0);
        return outMin + t * (outMax - outMin);
    }

    private static int GetCwSize(double snrDb) =>
        (int)Math.Round(MapClamped(snrDb, SnrMinDb, SnrMaxDb, CwMin, CwMax), MidpointRounding.AwayFromZero);

    /// <summary>Firmware RadioInterface::computeSlotTimeMsec() for sub-GHz
    /// hardware; the 2.4 GHz branch never applies here.</summary>
    public static double ComputeSlotTimeMsec(LoraPreset preset)
    {
        var p = LoraParamsHelper.FromPreset(preset);
        double symbolTimeMs = Math.Pow(2.0, p.Sf) / p.BwKhz;
        const double kCadSymbols = 2.5;                     // max(2.25, NUM_SYM_CAD + 0.5)
        const double kFixedOverheadMs = 0.2 + 0.4 + 7.0;    // CAD + propagation + turnaround + MAC
        return kCadSymbols * symbolTimeMs + kFixedOverheadMs;
    }

    /// <summary>
    /// Firmware getTxDelayMsecWeighted(): how long to wait before relaying,
    /// weighted by the SNR the packet arrived with. Routers get a short, tight
    /// window so they relay first; everyone else waits a base offset plus a
    /// wider one, which is what keeps a dozen listeners from all rebroadcasting
    /// the same packet simultaneously.
    /// </summary>
    public static int GetTxDelayMsecWeighted(LoraPreset preset, float snrDb, bool isRouterRole)
    {
        int cwSize = GetCwSize(snrDb);
        double slotMs = ComputeSlotTimeMsec(preset);
        double delayMs = isRouterRole
            ? Random.Shared.Next(0, Math.Max(1, 2 * cwSize)) * slotMs
            : (2 * CwMax * slotMs) + Random.Shared.Next(0, 1 << cwSize) * slotMs;
        return (int)Math.Round(delayMs);
    }

    /// <summary>
    /// Firmware getTxDelayMsecWeightedWorst(): the far end of the contention
    /// window. Used to push a scheduled relay into the late window rather than
    /// cancel it — see <see cref="ShouldClampToLateWindow"/>.
    /// </summary>
    public static int GetTxDelayMsecWeightedWorst(LoraPreset preset, float snrDb)
    {
        int cwSize = GetCwSize(snrDb);
        double slotMs = ComputeSlotTimeMsec(preset);
        return (int)Math.Round((2 * CwMax * slotMs) + (1 << cwSize) * slotMs);
    }

    /// <summary>
    /// Firmware FloodingRouter::perhapsCancelDupe. A role that must not cancel
    /// its rebroadcast still shouldn't transmit on top of the station we just
    /// heard, so the pending relay slides to the end of the window instead:
    /// ROUTER_LATE always, CLIENT_BASE for favourited traffic. ROUTER is absent
    /// on purpose — it is the backbone and relays early by design.
    /// </summary>
    public static bool ShouldClampToLateWindow(RelayContext ctx, MeshHeader header)
    {
        string role = Canonical(ctx.Role);
        if (role == "ROUTERLATE") return true;
        return role == "CLIENTBASE" && IsFromOrToFavoritedNode(ctx, header);
    }

    /// <summary>Firmware <c>shouldRebroadcastEarlyLikeRouter</c>: ROUTER alone,
    /// not ROUTER_LATE.</summary>
    public static bool IsRouterRole(string? role) => Canonical(role) == "ROUTER";
}
