// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// The one value a script's <c>channel:</c> may hold that is not a channel
/// name.
/// </summary>
/// <remarks>
/// <para>A mesh running a default preset has a primary with no name of its
/// own, so there is no string that would ever match it — hence a way to name
/// it by role.</para>
/// <para>Written in braces, which the rest of the language already reserves
/// for placeholders, so it cannot collide with a channel someone actually
/// called "primary". A bare word in a <c>channel:</c> is always a name and
/// nothing else, which is the property a keyword could not have.</para>
/// </remarks>
public static class ScriptChannels
{
    /// <summary>Names the primary by role rather than by name.</summary>
    public const string PrimaryToken = "{primary}";

    /// <summary>Whether a <c>channel:</c> value is the role token rather than
    /// a name. Case-insensitive, like every other channel comparison.</summary>
    public static bool IsPrimaryToken(string? value) =>
        string.Equals(value?.Trim(), PrimaryToken, StringComparison.OrdinalIgnoreCase);
}
