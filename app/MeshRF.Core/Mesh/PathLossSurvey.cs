// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using MeshRF.Nodes;

namespace MeshRF.Mesh;

/// <summary>The station's own facts, which no amount of received traffic
/// reveals and which every observation is measured against.</summary>
/// <param name="AssumedPeerTxPowerDbm">What the peers are taken to be running.
/// Meshtastic reports no such field, so this is an assumption, and the error in
/// it lands in the fitted offset rather than in the exponent — see
/// <see cref="PathLossFit"/>.</param>
public sealed record PathLossSurveyOptions(
    GeoPoint Home,
    double MyAntennaM,
    double PeerAntennaM,
    double MyGainDbi,
    double PeerGainDbi,
    double AssumedPeerTxPowerDbm,
    double FrequencyMhz,
    double BandwidthKhz,
    double NoiseFigureDb = LinkBudget.DefaultNoiseFigureDb,
    double MinDistanceM = 100,
    double MaxDistanceM = 100_000,
    BuildingIndex? Buildings = null,
    BuildingLossModel? BuildingLoss = null);

/// <summary>What one neighbour contributes. <paramref name="TerrainKnown"/> is
/// false when no elevation could be read for the path, which leaves the terrain
/// loss unaccounted for and would push it into the fit as if it were clutter.
/// </summary>
/// <param name="BuildingLossDb">What the footprints along the path cost, when
/// buildings are in use. Subtracted like the terrain: whatever the model can
/// name is taken out before the fit, so the exponent describes what is left
/// rather than re-absorbing something already accounted for.</param>
public sealed record PathLossObservation(
    uint NodeNum,
    string Name,
    double DistanceM,
    double MeasuredSnrDb,
    double DiffractionLossDb,
    double PropagationLossDb,
    bool TerrainKnown,
    double BuildingLossDb = 0)
{
    public PathLossSample ToSample() => new(NodeNum, DistanceM, PropagationLossDb);
}

/// <summary>
/// Turns direct neighbours into path-loss observations: for each one, how far
/// away it is, what the terrain between takes out, and how much loss is left
/// for distance to account for.
///
/// Only nodes heard over the air at zero hops count. A relayed packet's SNR was
/// measured on the last hop, which is a path between two other radios, and a
/// packet that arrived over MQTT was not measured by this station's receiver at
/// all — either one would be a reading of somewhere else entirely.
/// </summary>
public sealed class PathLossSurvey
{
    private readonly TerrainTiles _terrain;

    public PathLossSurvey(TerrainTiles terrain) => _terrain = terrain;

    /// <summary>The neighbours worth measuring, nearest first.</summary>
    public static IReadOnlyList<NodeRecord> Candidates(
        IEnumerable<NodeRecord> nodes, PathLossSurveyOptions options, uint myNodeNum) =>
        Candidates(nodes, options.Home, myNodeNum, options.MinDistanceM, options.MaxDistanceM);

    /// <summary>
    /// The same selection without a whole survey's worth of radio settings, for
    /// callers that only want to know which nodes this station has genuinely
    /// heard for itself and how far away they are.
    /// </summary>
    public static IReadOnlyList<NodeRecord> Candidates(
        IEnumerable<NodeRecord> nodes, GeoPoint home, uint myNodeNum,
        double minDistanceM = 100, double maxDistanceM = 100_000)
    {
        var candidates = new List<(NodeRecord Node, double Distance)>();

        foreach (var node in nodes)
        {
            if (node.NodeNum == myNodeNum) continue;
            if (node.HopsAway != 0) continue;
            if (node.SeenViaMqtt == true) continue;
            if (node.SnrDb is not float) continue;
            if (node.Latitude is not double lat || node.Longitude is not double lon) continue;

            double distance = Geodesy.DistanceM(home, new GeoPoint(lat, lon));

            // Too close and the position fuzzing Meshtastic applies is a large
            // fraction of the range; too far and the node is reporting a
            // position it does not have.
            if (distance < minDistanceM || distance > maxDistanceM) continue;

            candidates.Add((node, distance));
        }

        candidates.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        return candidates.Select(c => c.Node).ToList();
    }

    /// <summary>Reads the terrain to each candidate and works out the loss its
    /// measured SNR implies. Runs one node at a time: the tile cache makes
    /// neighbours in the same direction nearly free, and a burst of parallel
    /// fetches would only make the progress meaningless.</summary>
    public async Task<IReadOnlyList<PathLossObservation>> MeasureAsync(
        IReadOnlyList<NodeRecord> candidates,
        PathLossSurveyOptions options,
        IProgress<int>? completed = null,
        CancellationToken ct = default)
    {
        var observations = new List<PathLossObservation>(candidates.Count);
        double noiseFloor = LinkBudget.NoiseFloorDbm(options.BandwidthKhz, options.NoiseFigureDb);

        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var node = candidates[i];
            var peer = new GeoPoint(node.Latitude!.Value, node.Longitude!.Value);
            double snr = node.SnrDb!.Value;

            double diffraction = 0;
            bool terrainKnown = false;

            var terrain = await _terrain.SampleAsync(options.Home, peer, ct).ConfigureAwait(false);
            if (terrain is not null)
            {
                var profile = LinkProfile.Build(
                    terrain.Ground, options.MyAntennaM, options.PeerAntennaM, options.FrequencyMhz);
                diffraction = profile.DiffractionLossDb;
                terrainKnown = terrain.Complete;
            }

            // Back out the loss from the reading: a received power is an SNR
            // above the floor, and everything between the two antenna ports had
            // to account for the difference.
            double buildings = BuildingLossAlong(options, options.Home, peer);

            double receivedPower = snr + noiseFloor;
            double totalLoss = options.AssumedPeerTxPowerDbm + options.PeerGainDbi + options.MyGainDbi
                             - receivedPower;

            observations.Add(new PathLossObservation(
                NodeNum: node.NodeNum,
                Name: DisplayName(node),
                DistanceM: Geodesy.DistanceM(options.Home, peer),
                MeasuredSnrDb: snr,
                DiffractionLossDb: diffraction,
                PropagationLossDb: totalLoss - diffraction - buildings,
                TerrainKnown: terrainKnown,
                BuildingLossDb: buildings));

            completed?.Report(i + 1);
        }

        return observations;
    }

    /// <summary>
    /// Turns binned survey readings into observations, reading the terrain
    /// between this station and each peer once per bin.
    ///
    /// The terrain is looked up from the station's own position, which is where
    /// the survey's own <c>my_lat</c> differs — a driven survey was recorded
    /// from all over. That is a deliberate simplification: the fit is a model of
    /// this site, and the bins exist to average a peer's readings rather than to
    /// place each one. A survey walked far from home is better fitted from
    /// wherever it was walked.
    /// </summary>
    public async Task<IReadOnlyList<PathLossObservation>> MeasureBinsAsync(
        IReadOnlyList<SurveyBin> bins,
        IReadOnlyDictionary<uint, (string Name, GeoPoint At)> peers,
        PathLossSurveyOptions options,
        IProgress<int>? completed = null,
        CancellationToken ct = default)
    {
        var observations = new List<PathLossObservation>(bins.Count);
        double noiseFloor = LinkBudget.NoiseFloorDbm(options.BandwidthKhz, options.NoiseFigureDb);

        // One terrain read per peer, not per bin: every bin from the same node
        // walks the same ground.
        var diffractionByPeer = new Dictionary<uint, (double LossDb, bool Known)>();

        for (int i = 0; i < bins.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var bin = bins[i];
            if (!peers.TryGetValue(bin.NodeNum, out var peer)) continue;

            if (!diffractionByPeer.TryGetValue(bin.NodeNum, out var terrainLoss))
            {
                var path = await _terrain.SampleAsync(options.Home, peer.At, ct).ConfigureAwait(false);
                terrainLoss = path is null
                    ? (0, false)
                    : (LinkProfile.Build(path.Ground, options.MyAntennaM, options.PeerAntennaM,
                                         options.FrequencyMhz).DiffractionLossDb,
                       path.Complete);
                diffractionByPeer[bin.NodeNum] = terrainLoss;
            }

            double buildings = BuildingLossAlong(options, options.Home, peer.At);

            double receivedPower = bin.MeanSnrDb + noiseFloor;
            double totalLoss = options.AssumedPeerTxPowerDbm + options.PeerGainDbi + options.MyGainDbi
                             - receivedPower;

            observations.Add(new PathLossObservation(
                NodeNum: bin.NodeNum,
                Name: $"{peer.Name} ({bin.Count} readings)",
                DistanceM: bin.DistanceM,
                MeasuredSnrDb: bin.MeanSnrDb,
                DiffractionLossDb: terrainLoss.LossDb,
                PropagationLossDb: totalLoss - terrainLoss.LossDb - buildings,
                TerrainKnown: terrainLoss.Known,
                BuildingLossDb: buildings));

            completed?.Report(i + 1);
        }

        return observations;
    }

    /// <summary>What the buildings between two points cost, or nothing when
    /// they are not in use.</summary>
    public static double BuildingLossAlong(PathLossSurveyOptions options, GeoPoint from, GeoPoint to) =>
        options is { Buildings: { Count: > 0 } index, BuildingLoss: { } model }
            ? model.LossDb(index.AlongPath(from, to))
            : 0;

    private static string DisplayName(NodeRecord node) =>
        !string.IsNullOrWhiteSpace(node.LongName) ? node.LongName
        : !string.IsNullOrWhiteSpace(node.ShortName) ? node.ShortName
        : $"!{node.NodeNum:x8}";
}
