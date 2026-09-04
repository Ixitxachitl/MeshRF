// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// How the app names itself to OpenStreetMap and friends. Their usage policies
/// require a User-Agent that identifies the application and offers a way to
/// reach whoever runs it, so this is a compliance surface, not cosmetics.
/// </summary>
public class HttpIdentityTests
{
    [Fact]
    public void TheContactLinkPointsAtThisProject()
    {
        // It once read github.com/meshrf, which is a real and entirely
        // unrelated person's account — so every tile request named a stranger
        // as the place to send complaints about our traffic.
        Assert.Equal("https://github.com/Ixitxachitl/MeshRF", HttpIdentity.Home);
        Assert.DoesNotContain("github.com/meshrf", HttpIdentity.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentNamesTheAppItsVersionAndWhereToFindIt()
    {
        string agent = HttpIdentity.UserAgent;

        Assert.StartsWith("MeshRF/", agent, StringComparison.Ordinal);
        Assert.Contains(HttpIdentity.Home, agent, StringComparison.Ordinal);

        // A bare product name with no version leaves an operator unable to say
        // which release started misbehaving.
        Assert.Matches(@"^MeshRF/\d+\.\d+\.\d+\S* \(\+https://\S+\)$", agent);
    }

    [Fact]
    public void TheVersionTracksTheBuildRatherThanBeingFrozenAtOne()
    {
        // Hardcoded "MeshRF/1.0" outlived four releases before anyone noticed.
        Assert.DoesNotContain("MeshRF/1.0 ", HttpIdentity.UserAgent + " ", StringComparison.Ordinal);
    }

    [Fact]
    public void ThePatchReleaseIsNamedNotTheMinorItCameFrom()
    {
        // AssemblyVersion is pinned to major.minor.0.0 for binding stability,
        // so reading it would make every patch release introduce itself as the
        // minor release. The source revision is trimmed off the other end: a
        // service wants to know which release misbehaved, not which commit.
        string agent = HttpIdentity.UserAgent;

        Assert.DoesNotContain("+", agent[..agent.IndexOf('(', StringComparison.Ordinal)],
            StringComparison.Ordinal);
        Assert.Matches(@"MeshRF/\d+\.\d+\.\d+ ", agent);
    }
}
