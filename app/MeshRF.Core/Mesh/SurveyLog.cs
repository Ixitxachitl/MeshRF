// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using MeshRF.Map;

namespace MeshRF.Mesh;

/// <summary>One packet heard directly, with where this station was standing at
/// the time.</summary>
public readonly record struct SurveySample(
    DateTime HeardUtc,
    uint NodeNum,
    double MyLat,
    double MyLon,
    double PeerLat,
    double PeerLon,
    double SnrDb,
    double DistanceM);

/// <summary>A group of readings from one peer over a narrow band of range,
/// averaged. <paramref name="Count"/> is how many packets went into it.
/// </summary>
public sealed record SurveyBin(
    uint NodeNum, double DistanceM, double MeanSnrDb, int Count);

/// <summary>
/// A record of every direct packet this station has heard, and where it was
/// when it heard it.
///
/// This is the walked survey MeshLab RF ships a separate firmware to collect,
/// done without any of it. A client with a GPS attached is already a survey
/// instrument: drive around for an afternoon and the same neighbour is
/// measured at fifty ranges instead of one, which is exactly what a path-loss
/// fit needs and cannot get from a node list — there, every peer contributes a
/// single packet at a single distance, and a station whose neighbours all sit
/// on one mast can never measure a falloff at all.
///
/// Kept as CSV rather than in the database. It is append-only, it is the sort
/// of thing worth opening in a spreadsheet or handing to someone else, and the
/// format is close enough to MeshLab's own measurements.csv to be read by it.
/// </summary>
public sealed class SurveyLog
{
    private const string Header =
        "heard_utc,node_num,my_lat,my_lon,peer_lat,peer_lon,snr_db,distance_m";

    private readonly string _path;
    private readonly object _gate = new();

    public SurveyLog(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MeshRF", "survey.csv");

    /// <summary>Where the log lives. Named FilePath rather than Path: a
    /// member called Path on a type that also uses System.IO.Path shadows
    /// it, and every call in the class then fails to compile for a reason
    /// that reads as nonsense.</summary>
    public string FilePath => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>
    /// Records one reading, if it is one this station measured for itself.
    ///
    /// The same rule the calibration uses: zero hops and over the air. A
    /// relayed packet's SNR belongs to the last hop and an MQTT one never
    /// touched this receiver, and either would be a measurement of somewhere
    /// else recorded under this station's position.
    /// </summary>
    /// <returns>Whether anything was written.</returns>
    public bool Record(
        uint nodeNum, byte? hopsAway, bool viaMqtt, float? snrDb,
        GeoPoint? myPosition, GeoPoint? peerPosition, DateTime heardUtc)
    {
        if (hopsAway != 0 || viaMqtt) return false;
        if (snrDb is not float snr) return false;
        if (myPosition is not { } mine || peerPosition is not { } peer) return false;

        double distance = Geodesy.DistanceM(mine, peer);
        if (distance <= 0 || double.IsNaN(distance)) return false;

        var line = string.Join(',',
            heardUtc.ToString("O", CultureInfo.InvariantCulture),
            nodeNum.ToString(CultureInfo.InvariantCulture),
            mine.Lat.ToString("F6", CultureInfo.InvariantCulture),
            mine.Lon.ToString("F6", CultureInfo.InvariantCulture),
            peer.Lat.ToString("F6", CultureInfo.InvariantCulture),
            peer.Lon.ToString("F6", CultureInfo.InvariantCulture),
            snr.ToString("F2", CultureInfo.InvariantCulture),
            distance.ToString("F1", CultureInfo.InvariantCulture));

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                bool fresh = !File.Exists(_path);
                using var writer = new StreamWriter(_path, append: true);
                if (fresh) writer.WriteLine(Header);
                writer.WriteLine(line);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        return true;
    }

    /// <summary>Everything recorded so far. A line that will not parse is
    /// skipped rather than failing the read: the file is appended to while the
    /// radio runs, and a half-written last line is normal.</summary>
    public IReadOnlyList<SurveySample> Read()
    {
        var samples = new List<SurveySample>();
        if (!File.Exists(_path)) return samples;

        string[] lines;
        lock (_gate)
        {
            try { lines = File.ReadAllLines(_path); }
            catch (IOException) { return samples; }
            catch (UnauthorizedAccessException) { return samples; }
        }

        foreach (var line in lines)
        {
            var f = line.Split(',');
            if (f.Length < 8) continue;

            if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out var heard)) continue;
            if (!uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint node)) continue;
            if (!Number(f[2], out double myLat) || !Number(f[3], out double myLon)) continue;
            if (!Number(f[4], out double peerLat) || !Number(f[5], out double peerLon)) continue;
            if (!Number(f[6], out double snr) || !Number(f[7], out double distance)) continue;

            samples.Add(new SurveySample(heard, node, myLat, myLon, peerLat, peerLon, snr, distance));
        }

        return samples;
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(_path); }
            catch (IOException) { /* nothing to lose */ }
            catch (UnauthorizedAccessException) { /* nothing to lose */ }
        }
    }

    /// <summary>
    /// Groups readings by peer and by range, averaging the SNR in each group.
    ///
    /// Averaging is the point. A single packet's SNR carries several decibels
    /// of fading, which is why a fit built from one reading per node scatters
    /// so widely; twenty packets from the same peer at the same range average
    /// most of that away. Bins are logarithmic because the fit is: a hundred
    /// metres matters near the station and is nothing at ten kilometres.
    /// </summary>
    /// <param name="binsPerDecade">How finely range is divided. Ten gives bins
    /// about a quarter of a decade wide at the coarse end.</param>
    /// <param name="minCount">Readings a bin needs before it is worth
    /// believing.</param>
    public static IReadOnlyList<SurveyBin> Bin(
        IEnumerable<SurveySample> samples, double binsPerDecade = 10, int minCount = 3)
    {
        if (binsPerDecade <= 0)
            throw new ArgumentOutOfRangeException(nameof(binsPerDecade), "bins have to have a width");

        var groups = new Dictionary<(uint Node, int Bin), (double SnrTotal, double DistanceTotal, int Count)>();

        foreach (var s in samples)
        {
            if (s.DistanceM <= 0) continue;

            int bin = (int)Math.Floor(Math.Log10(s.DistanceM) * binsPerDecade);
            var key = (s.NodeNum, bin);

            var running = groups.TryGetValue(key, out var existing) ? existing : default;
            groups[key] = (running.SnrTotal + s.SnrDb,
                           running.DistanceTotal + s.DistanceM,
                           running.Count + 1);
        }

        return groups
            .Where(g => g.Value.Count >= minCount)
            .Select(g => new SurveyBin(
                g.Key.Node,
                g.Value.DistanceTotal / g.Value.Count,
                g.Value.SnrTotal / g.Value.Count,
                g.Value.Count))
            .OrderBy(b => b.DistanceM)
            .ToList();
    }

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
