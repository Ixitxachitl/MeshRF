// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Guards against <see cref="HardwareModels"/> silently drifting from the
/// Meshtastic <c>HardwareModel</c> enum in the linked protobuf submodule
/// (third_party/meshtastic_protobufs). HardwareModels.cs is hand-maintained,
/// not code-generated, so nothing else catches a submodule bump that adds,
/// renumbers, or renames hardware models — this test parses the enum
/// straight out of mesh.proto and diffs it against HardwareModels.cs.
/// </summary>
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
            "HardwareModels.cs is missing hardware model(s) present in mesh.proto: " +
            string.Join(", ", missingFromCs) +
            ". Re-sync the s_models table in HardwareModels.cs with the submodule's HardwareModel enum.");
        Assert.True(mismatched.Count == 0,
            "HardwareModels.cs has id/name mismatches vs mesh.proto: " + string.Join(", ", mismatched));
        Assert.True(staleInCs.Count == 0,
            "HardwareModels.cs has entries not present (or renumbered) in mesh.proto: " +
            string.Join(", ", staleInCs) +
            ". Re-sync the s_models table in HardwareModels.cs with the submodule's HardwareModel enum.");
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
