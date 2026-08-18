// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;
using MeshRF;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Guards against <see cref="Region"/> drifting from the Meshtastic
/// <c>Config.LoRaConfig.RegionCode</c> enum in the linked protobuf submodule
/// (third_party/meshtastic_protobufs). Region is hand-maintained alongside
/// ChannelPlan's band table, so a submodule bump that adds a region would
/// otherwise go unnoticed until someone in that region couldn't select it.
/// Unlike LoraPreset, Region's members carry the protobuf's own values, so
/// this checks names and numbers both.
/// </summary>
public class RegionSyncTests
{
    /// <summary>Regions present in the protobuf schema that MeshRF deliberately
    /// doesn't offer, because firmware's own <c>regions[]</c> table has no row
    /// for them — there is no band, default preset or slot plan to implement.
    /// UA_868 is additionally marked deprecated upstream and its row was
    /// removed; EU_874 and EU_917 have never had one.</summary>
    private static readonly HashSet<string> KnownUnsupported = new(StringComparer.Ordinal)
    {
        "UA_868",
        "EU_874",
        "EU_917",
    };

    [Fact]
    public void MatchesProtobufRegionCodeEnum()
    {
        string protoPath = FindConfigProto();
        string proto = File.ReadAllText(protoPath);

        var enumMatch = Regex.Match(proto, @"enum RegionCode \{(.*?)\r?\n    \}", RegexOptions.Singleline);
        Assert.True(enumMatch.Success, $"Could not find 'enum RegionCode {{ ... }}' in {protoPath}");

        var entries = Regex.Matches(enumMatch.Groups[1].Value,
            @"(?m)^\s*([A-Z0-9_]+)\s*=\s*(\d+)\s*(\[deprecated = true\])?\s*;");
        Assert.True(entries.Count > 0, $"Found the RegionCode enum block in {protoPath} but no entries inside it.");

        var protoRegions = entries.ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
        var csRegions = Enum.GetValues<Region>().ToDictionary(r => r.ToString(), r => (int)r);

        var missingFromCs = protoRegions.Keys
            .Where(name => !KnownUnsupported.Contains(name) && !csRegions.ContainsKey(name))
            .ToList();
        Assert.True(missingFromCs.Count == 0,
            "Region is missing region(s) present in config.proto's RegionCode: " +
            string.Join(", ", missingFromCs) +
            ". Add them to Region and to ChannelPlan's Info() table (band edges, profile, wideLora, " +
            "default preset and override slot come from firmware's RegionInfo regions[] in " +
            "src/mesh/RadioInterface.cpp), plus ToProtoRegionCode in RadioViewModel.Mqtt.cs — or add to " +
            "KnownUnsupported here if firmware has no row for them either.");

        var staleInCs = csRegions.Keys.Where(name => !protoRegions.ContainsKey(name)).ToList();
        Assert.True(staleInCs.Count == 0,
            "Region has entries not present in config.proto's RegionCode: " + string.Join(", ", staleInCs));

        var mismatched = csRegions
            .Where(kv => protoRegions[kv.Key] != kv.Value)
            .Select(kv => $"{kv.Key} is {kv.Value} in MeshRF, {protoRegions[kv.Key]} in the protobuf")
            .ToList();
        Assert.True(mismatched.Count == 0,
            "Region's values must be the protobuf's own: " + string.Join("; ", mismatched));
    }

    /// <summary>Walks up from the test binary's output directory to find the
    /// repo root (identified by third_party/meshtastic_protobufs existing
    /// under it), so this works regardless of Debug/Release/x64 output
    /// layout.</summary>
    private static string FindConfigProto()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "meshtastic_protobufs", "meshtastic", "config.proto");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate third_party/meshtastic_protobufs/meshtastic/config.proto by walking up from " +
            AppContext.BaseDirectory + ". Initialize submodules: git submodule update --init --recursive");
    }
}
