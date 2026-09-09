// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// The vocabulary a script's <c>mesh:</c> uses, and the rule that decides
/// whether one names the mesh an event was heard on.
/// </summary>
/// <remarks>
/// <para>A mesh is a preset on a frequency — one channel list, named for the
/// preset that owns it. This station can be listening to several at once, so a
/// script has to be able to say which of them it answers and which it speaks
/// on.</para>
/// <para>Leaving <c>mesh:</c> out means the primary alone, which is what every
/// script meant before the key existed.</para>
/// </remarks>
public static class ScriptMeshes
{
    /// <summary>Names the mesh this station is on by role rather than by the
    /// preset it happens to be running, so a script survives the toolbar being
    /// moved. Spelled like the channel token, and in braces for the same
    /// reason: a bare word is always a preset name.</summary>
    public const string PrimaryToken = ScriptChannels.PrimaryToken;

    /// <summary>Every mesh this station is listening to. A bare word, like
    /// <c>geofence: any</c> — no preset is called "any".</summary>
    public const string AnyToken = "any";

    /// <summary>Whether a <c>mesh:</c> entry is the all-meshes word.</summary>
    public static bool IsAnyToken(string? value) =>
        string.Equals(value?.Trim(), AnyToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a <c>mesh:</c> list names the mesh <paramref name="evt"/>
    /// arrived on. An empty list is the primary alone.
    /// </summary>
    /// <remarks>
    /// Names are compared without case, like every other name in the
    /// vocabulary, and the primary is matched by its flag rather than by its
    /// name — the preset it is running changes with the toolbar, and a script
    /// that stopped answering when the station retuned would be no use.
    /// </remarks>
    public static bool Names(IReadOnlyList<string> meshes, ScriptEvent evt)
    {
        if (meshes.Count == 0) return evt.IsPrimaryMesh;

        foreach (var mesh in meshes)
        {
            if (IsAnyToken(mesh)) return true;
            if (ScriptChannels.IsPrimaryToken(mesh))
            {
                if (evt.IsPrimaryMesh) return true;
                continue;
            }
            if (string.Equals(mesh, evt.Mesh, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
