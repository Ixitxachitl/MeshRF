// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;

namespace MeshRF;

/// <summary>
/// How this app names itself to the services it fetches from.
/// </summary>
/// <remarks>
/// <para>Not decoration. OpenStreetMap's tile usage policy and Nominatim's
/// policy both require a User-Agent that identifies the application and offers
/// a way to reach whoever runs it; traffic without one is blocked, and traffic
/// pointing somewhere useless is worse than blocked — an operator with a
/// problem to report has nowhere to send it.</para>
/// <para>Built in one place because it is sent by four separate clients — map
/// tiles, elevation tiles, Overpass and Nominatim — and four copies of a string
/// is four chances for one of them to be wrong.</para>
/// </remarks>
public static class HttpIdentity
{
    /// <summary>Where to find the project, for anyone who needs to.</summary>
    public const string Home = "https://github.com/Ixitxachitl/MeshRF";

    /// <summary>The release, so a service can tell versions apart when one of
    /// them starts behaving badly.</summary>
    /// <remarks>From the informational version, not the assembly version: that
    /// one is pinned to major.minor.0.0 for binding stability, so every patch
    /// release would name itself after the minor release it came from. The
    /// source revision this build carries is trimmed — a service wants to know
    /// which release is misbehaving, not which commit.</remarks>
    private static string Version { get; } = Read();

    private static string Read()
    {
        var informational = typeof(HttpIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            string trimmed = plus >= 0 ? informational[..plus] : informational;
            if (trimmed.Length > 0) return trimmed;
        }

        return typeof(HttpIdentity).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";
    }

    /// <summary>The User-Agent every outbound request carries.</summary>
    /// <remarks>Declared after what it reads. Static initialisers run in
    /// declaration order, so building this string above <see cref="Version"/>
    /// captured the empty default and sent a User-Agent with no version in it
    /// at all — which is the thing this class exists to stop.</remarks>
    public static string UserAgent { get; } = $"MeshRF/{Version} (+{Home})";
}
