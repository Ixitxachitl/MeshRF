// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using Google.Protobuf.Reflection;

namespace MeshRF.Mesh;

/// <summary>
/// Reads a generated protobuf enum back out as the names the <c>.proto</c>
/// spells, for the places MeshRF wants those names as plain strings.
/// </summary>
/// <remarks>
/// <para>The submodule's schemas are compiled into this assembly, and protoc
/// stamps every value with an <see cref="OriginalNameAttribute"/> carrying the
/// screaming-snake name from the schema — which is the form that goes in front
/// of a user and into settings, where the <c>SeeedWioTrackerL1Pro1W</c> protoc
/// coins for C# would not do.</para>
/// <para>Written here once because the alternative is a hand-kept table per
/// enum, and those can only agree with the schema or be wrong. Both of the
/// ones MeshRF had were wrong: the hardware models sat a submodule bump behind,
/// and the firmware editions offered a <c>PREMIUM</c> that has never existed in
/// any Meshtastic schema.</para>
/// </remarks>
public static class ProtoEnums
{
    /// <summary>
    /// Every value of <typeparamref name="TEnum"/> as (number, schema name),
    /// ordered by number.
    /// </summary>
    /// <remarks>
    /// Reflection over the fields rather than <see cref="Enum.GetValues{T}"/>,
    /// because the attribute is what carries the wanted spelling. A value
    /// protoc left unstamped is skipped rather than guessed at.
    /// </remarks>
    public static (int Id, string Name)[] Entries<TEnum>() where TEnum : struct, Enum =>
        typeof(TEnum)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (
                Id: (int)f.GetRawConstantValue()!,
                Name: f.GetCustomAttribute<OriginalNameAttribute>()?.Name))
            .Where(e => e.Name is not null)
            .OrderBy(e => e.Id)
            .Select(e => (e.Id, e.Name!))
            .ToArray();

    /// <summary>The schema names alone, ordered by number — what a picker
    /// wants.</summary>
    public static string[] Names<TEnum>() where TEnum : struct, Enum =>
        Entries<TEnum>().Select(e => e.Name).ToArray();
}
