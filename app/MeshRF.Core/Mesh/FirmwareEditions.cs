// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// The Meshtastic <c>FirmwareEdition</c> enum (mesh.proto), as the names a
/// picker shows and settings store.
/// </summary>
/// <remarks>
/// <para>Read out of the generated enum — see <see cref="ProtoEnums"/>. The
/// hand-written list this replaces offered two editions, one of which
/// (<c>PREMIUM</c>) appears in no Meshtastic schema and was therefore not
/// something any node could be running, while nine real ones were missing.
/// Most of them are event builds: DEFCON, Burning Man, Hamvention and the
/// rest.</para>
/// <para>MeshRF isn't real firmware, so this is part of the compatibility
/// identity it presents alongside the version string, and <c>VANILLA</c> —
/// value 0, and the schema's own default — is the honest answer for it.</para>
/// </remarks>
public static class FirmwareEditions
{
    /// <summary>Every edition in schema order, VANILLA first.</summary>
    public static IReadOnlyList<string> AllNames { get; } =
        ProtoEnums.Names<Meshtastic.Protobufs.FirmwareEdition>();

    /// <summary>What to present when nothing has been chosen, or when a stored
    /// value names an edition the schema no longer has.</summary>
    public const string Default = "VANILLA";
}
