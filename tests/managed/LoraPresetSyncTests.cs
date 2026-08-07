// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;
using MeshRF;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Guards against <see cref="LoraPreset"/> silently drifting from the
/// Meshtastic <c>Config.LoRaConfig.ModemPreset</c> enum in the linked
/// protobuf submodule (third_party/meshtastic_protobufs). LoraPreset is
/// hand-maintained (its ordinal values mirror the native
/// <c>mrf::modem::Preset</c> enum for P/Invoke, not the protobuf), so nothing
/// else catches a submodule bump that adds or renames a preset — this test
/// parses the enum straight out of config.proto and diffs it by NAME against
/// LoraPreset (ordinals are intentionally not compared; see the enum's own
/// doc comment for why they can't be).
/// </summary>
public class LoraPresetSyncTests
{
    /// <summary>Presets present (and deprecated) in the protobuf schema that
    /// MeshRF deliberately doesn't implement. VERY_LONG_SLOW is deprecated
    /// upstream ("works only with txco and is unusably slow") and firmware's
    /// own modemPresetToParams() has never had a case for it either — unlike
    /// LONG_SLOW, which is also deprecated but still fully supported.</summary>
    private static readonly HashSet<string> KnownUnsupported = new(StringComparer.Ordinal)
    {
        "VERY_LONG_SLOW",
    };

    [Fact]
    public void MatchesProtobufModemPresetEnum()
    {
        string protoPath = FindConfigProto();
        string proto = File.ReadAllText(protoPath);

        var enumMatch = Regex.Match(proto, @"enum ModemPreset \{(.*?)\r?\n    \}", RegexOptions.Singleline);
        Assert.True(enumMatch.Success, $"Could not find 'enum ModemPreset {{ ... }}' in {protoPath}");

        var entries = Regex.Matches(enumMatch.Groups[1].Value, @"(?m)^\s*([A-Z0-9_]+)\s*=\s*\d+\s*(\[deprecated = true\])?\s*;");
        Assert.True(entries.Count > 0, $"Found the ModemPreset enum block in {protoPath} but no entries inside it.");

        var protoNames = entries.Select(m => m.Groups[1].Value).ToList();
        var csNames = Enum.GetNames<LoraPreset>().ToHashSet(StringComparer.Ordinal);

        var missingFromCs = new List<string>();
        foreach (var protoName in protoNames)
        {
            if (KnownUnsupported.Contains(protoName)) continue;
            if (!csNames.Contains(ToPascalCase(protoName)))
                missingFromCs.Add(protoName);
        }

        var protoNamesPascal = protoNames.Select(ToPascalCase).ToHashSet(StringComparer.Ordinal);
        var staleInCs = csNames.Where(n => !protoNamesPascal.Contains(n)).ToList();

        Assert.True(missingFromCs.Count == 0,
            "LoraPreset is missing preset(s) present in config.proto's ModemPreset: " +
            string.Join(", ", missingFromCs) +
            ". Add them to LoraPreset (app/MeshRF.Core/MeshtasticCore.cs), the native mrf::modem::Preset enum " +
            "(native/core/include/mrf/modem/Preset.h), LoraParamsHelper.FromPreset, and ChannelPlan's " +
            "BandwidthMHz/PresetName — or add to KnownUnsupported here if intentionally not implemented.");
        Assert.True(staleInCs.Count == 0,
            "LoraPreset has entries not present in config.proto's ModemPreset: " + string.Join(", ", staleInCs));
    }

    /// <summary>"LONG_MODERATE" -&gt; "LongModerate" (protoc-gen-csharp's own
    /// enum-member naming convention, which MeshRF's LoraPreset member names
    /// already follow — this is unrelated to ChannelPlan.PresetName's
    /// "LongMod" channel-hash-naming shorthand).</summary>
    private static string ToPascalCase(string screamingSnakeCase) =>
        string.Concat(screamingSnakeCase.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

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
