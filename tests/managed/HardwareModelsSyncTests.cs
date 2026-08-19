// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Checks that <see cref="HardwareModels"/> really does hand back everything
/// <c>mesh.proto</c> declares.
/// </summary>
/// <remarks>
/// It used to guard a hand-written table against the submodule, and failed
/// exactly once for real before the table was replaced by reflection over the
/// generated enum. Kept because it now checks the other half: this reads the
/// enum out of the <c>.proto</c> as text, where HardwareModels reads it out of
/// what protoc compiled, so the two only agree if every value carries the
/// <c>OriginalName</c> the derivation depends on and the build is looking at
/// the submodule it thinks it is.
/// </remarks>
public class HardwareModelsSyncTests
{
    [Fact]
    public void MatchesProtobufHardwareModelEnum()
    {
        string protoPath = FindMeshProto();
        string proto = File.ReadAllText(protoPath);

        var enumMatch = Regex.Match(proto, @"enum HardwareModel \{(.*?)\r?\n\}", RegexOptions.Singleline);
        Assert.True(enumMatch.Success, $"Could not find 'enum HardwareModel {{ ... }}' in {protoPath}");

        var entries = Regex.Matches(enumMatch.Groups[1].Value, @"(?m)^\s*([A-Z0-9_]+)\s*=\s*(\d+)\s*;");
        Assert.True(entries.Count > 0, $"Found the HardwareModel enum block in {protoPath} but no entries inside it.");

        var protoById = new Dictionary<int, string>();
        foreach (Match m in entries)
            protoById[int.Parse(m.Groups[2].Value)] = m.Groups[1].Value;

        var missingFromCs = new List<string>();
        var mismatched = new List<string>();
        foreach (var (id, name) in protoById)
        {
            var csName = HardwareModels.Name(id);
            if (csName.StartsWith("UNKNOWN_"))
                missingFromCs.Add($"{id} ({name})");
            else if (csName != name)
                mismatched.Add($"{id}: proto={name} cs={csName}");
        }

        var staleInCs = new List<string>();
        foreach (var name in HardwareModels.AllNames)
        {
            var id = HardwareModels.Id(name);
            if (!protoById.TryGetValue(id, out var protoName) || protoName != name)
                staleInCs.Add($"{id} ({name})");
        }

        Assert.True(missingFromCs.Count == 0,
            "HardwareModels does not resolve hardware model(s) present in mesh.proto: " +
            string.Join(", ", missingFromCs) +
            ". The generated enum should carry these; check that the protobuf submodule and the compiled " +
            "output are in step, and that protoc stamped them with OriginalName.");
        Assert.True(mismatched.Count == 0,
            "HardwareModels resolves names that disagree with mesh.proto: " + string.Join(", ", mismatched));
        Assert.True(staleInCs.Count == 0,
            "HardwareModels reports model(s) not present (or renumbered) in mesh.proto: " +
            string.Join(", ", staleInCs) +
            ". The compiled enum is ahead of the .proto being read here, which means a stale build.");
    }

    /// <summary>Walks up from the test binary's output directory to find the
    /// repo root (identified by third_party/meshtastic_protobufs existing
    /// under it), so this works regardless of Debug/Release/x64 output
    /// layout.</summary>
    private static string FindMeshProto()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "meshtastic_protobufs", "meshtastic", "mesh.proto");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate third_party/meshtastic_protobufs/meshtastic/mesh.proto by walking up from " +
            AppContext.BaseDirectory + ". Initialize submodules: git submodule update --init --recursive");
    }
}
