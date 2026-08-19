// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using Google.Protobuf.Reflection;

namespace MeshRF.Mesh;

/// <summary>
/// Maps the Meshtastic <c>HardwareModel</c> enum (mesh.proto) between numeric
/// ids and their canonical names. Used to display a node's hardware (decode)
/// and to advertise our own model in NodeInfo broadcasts (encode).
/// </summary>
/// <remarks>
/// <para>Read out of the generated <see cref="Meshtastic.Protobufs.HardwareModel"/>
/// rather than written out here. The submodule's schemas are compiled into this
/// assembly, and protoc stamps every value with an
/// <see cref="OriginalNameAttribute"/> carrying the name as the .proto spells
/// it — which is exactly the string this class exists to hand out. A table
/// repeating them could only ever agree with the enum or be wrong, and it was
/// wrong: a submodule bump added model 144 and the copy here sat at 143, so
/// every node reporting one read as <c>UNKNOWN_144</c>.</para>
/// <para>Regions and LoRa presets are still hand-written, and that is not the
/// same situation: their tables carry band edges, channel spacing and
/// SF/BW/CR, which come from firmware's C++ and appear in no .proto. Only
/// their names and numbers can be checked against the schema, which is what
/// RegionSyncTests and LoraPresetSyncTests do.</para>
/// </remarks>
public static class HardwareModels
{
    /// <summary>
    /// Every value of the generated enum, as (id, proto name), ordered by id.
    /// </summary>
    /// <remarks>
    /// Reflection over the enum's fields rather than <c>Enum.GetValues</c>,
    /// because the name wanted here is the <c>SEEED_WIO_TRACKER_L1_PRO_1W</c>
    /// on the attribute, not the <c>SeeedWioTrackerL1Pro1W</c> protoc coins for
    /// C#. A value protoc left unstamped is skipped rather than guessed at.
    /// </remarks>
    private static readonly (int Id, string Name)[] s_models =
        typeof(Meshtastic.Protobufs.HardwareModel)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (
                Id: (int)f.GetRawConstantValue()!,
                Name: f.GetCustomAttribute<OriginalNameAttribute>()?.Name))
            .Where(m => m.Name is not null)
            .OrderBy(m => m.Id)
            .Select(m => (m.Id, m.Name!))
            .ToArray();

    private static readonly Dictionary<int, string> s_byId =
        s_models.ToDictionary(m => m.Id, m => m.Name);

    private static readonly Dictionary<string, int> s_byName =
        s_models.ToDictionary(m => m.Name, m => m.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>All model names in firmware enum order (for UI pickers).</summary>
    public static IReadOnlyList<string> AllNames { get; } =
        s_models.Select(m => m.Name).ToArray();

    /// <summary>Resolve a numeric id to its canonical name. Unknown ids fall
    /// back to "UNKNOWN_&lt;id&gt;" so the value is never silently lost.</summary>
    public static string Name(int id) =>
        s_byId.TryGetValue(id, out var name) ? name : $"UNKNOWN_{id}";

    /// <summary>Resolve a model name to its numeric id (0 = UNSET when the name
    /// is unknown or empty).</summary>
    public static int Id(string? name) =>
        !string.IsNullOrWhiteSpace(name) && s_byName.TryGetValue(name, out var id) ? id : 0;

    /// <summary>Normalize a stored hardware value for display. Legacy records
    /// may hold a raw numeric id (saved before names were resolved); those are
    /// mapped to a name. Values that are already names pass through unchanged.</summary>
    public static string Display(string? stored) =>
        int.TryParse(stored, out var id) ? Name(id) : (stored ?? string.Empty);
}
