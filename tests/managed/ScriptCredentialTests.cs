// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The on-disk contract for a stored API key. AppSettings.Save/Load cannot be
/// exercised directly here — they write to the real %APPDATA% path — so these
/// pin the serialization shape those two rely on.
/// </summary>
public class ScriptCredentialTests
{
    [Fact]
    public void The_Plaintext_Value_Is_Never_Serialized()
    {
        var credential = new ScriptCredential
        {
            Name = "openai",
            Placement = ScriptCredentialPlacement.Bearer,
            Value = "sk-super-secret",
            ValueOnDisk = "protected-blob",
        };

        var json = JsonSerializer.Serialize(credential);

        // The whole point of the JsonIgnore/JsonPropertyName pair: settings.json
        // must carry the protected blob under "Value", never the key itself.
        Assert.DoesNotContain("sk-super-secret", json);
        Assert.Contains("\"Value\":\"protected-blob\"", json);
    }

    [Fact]
    public void What_Is_Written_Round_Trips_Back_Into_The_On_Disk_Slot()
    {
        var json = JsonSerializer.Serialize(new ScriptCredential
        {
            Name = "weather",
            Placement = ScriptCredentialPlacement.Query,
            Parameter = "appid",
            ValueOnDisk = "protected-blob",
        });

        var restored = JsonSerializer.Deserialize<ScriptCredential>(json)!;

        Assert.Equal("weather", restored.Name);
        Assert.Equal(ScriptCredentialPlacement.Query, restored.Placement);
        Assert.Equal("appid", restored.Parameter);
        Assert.Equal("protected-blob", restored.ValueOnDisk);
        // Load() is what turns the blob back into Value; deserialization alone
        // must not populate it.
        Assert.Equal(string.Empty, restored.Value);
    }

    [Fact]
    public void A_Credential_List_Survives_A_Settings_Shaped_Round_Trip()
    {
        // Mirrors what AppSettings does with its ScriptCredentials property.
        var before = new List<ScriptCredential>
        {
            new() { Name = "a", Placement = ScriptCredentialPlacement.Bearer, ValueOnDisk = "one" },
            new() { Name = "b", Placement = ScriptCredentialPlacement.Header, Parameter = "X-API-Key", ValueOnDisk = "two" },
        };

        var after = JsonSerializer.Deserialize<List<ScriptCredential>>(JsonSerializer.Serialize(before))!;

        Assert.Equal(2, after.Count);
        Assert.Equal(["a", "b"], after.Select(c => c.Name).ToArray());
        Assert.Equal(["one", "two"], after.Select(c => c.ValueOnDisk).ToArray());
        Assert.Equal("X-API-Key", after[1].Parameter);
    }

    [Theory]
    [InlineData(ScriptCredentialPlacement.Bearer, "", "Authorization: Bearer …")]
    [InlineData(ScriptCredentialPlacement.Header, "X-API-Key", "X-API-Key: …")]
    [InlineData(ScriptCredentialPlacement.Query, "appid", "?appid=…")]
    public void The_List_Summary_Never_Shows_The_Value(
        ScriptCredentialPlacement placement, string parameter, string expected)
    {
        var credential = new ScriptCredential
        {
            Name = "k", Placement = placement, Parameter = parameter, Value = "sk-super-secret",
        };

        var summary = credential.Describe();

        Assert.Equal(expected, summary);
        Assert.DoesNotContain("sk-super-secret", summary);
    }

    [Fact]
    public void A_Pair_Is_One_Credential_And_Neither_Half_Is_Serialized_Plainly()
    {
        // A client id and secret are issued and rotated together and are
        // useless apart, so they are one entry rather than two kept in step.
        var credential = new ScriptCredential
        {
            Name = "xweather",
            Placement = ScriptCredentialPlacement.Query,
            Parameter = "client_id",
            Value = "the-id",
            Parameter2 = "client_secret",
            Value2 = "the-secret",
            ValueOnDisk = "blob-1",
            Value2OnDisk = "blob-2",
        };

        Assert.True(credential.IsPair);
        Assert.Equal("?client_id=…&client_secret=…", credential.Describe());
        Assert.DoesNotContain("the-id", credential.Describe());
        Assert.DoesNotContain("the-secret", credential.Describe());

        var json = JsonSerializer.Serialize(credential);
        Assert.DoesNotContain("the-id", json);
        Assert.DoesNotContain("the-secret", json);
        Assert.Contains("\"Value\":\"blob-1\"", json);
        Assert.Contains("\"Value2\":\"blob-2\"", json);

        var restored = JsonSerializer.Deserialize<ScriptCredential>(json)!;
        Assert.Equal("client_secret", restored.Parameter2);
        Assert.Equal("blob-2", restored.Value2OnDisk);
        Assert.Equal(string.Empty, restored.Value2);
    }

    [Fact]
    public void A_Single_Secret_Credential_Is_Not_A_Pair()
    {
        var credential = new ScriptCredential
        {
            Name = "openai", Placement = ScriptCredentialPlacement.Bearer, Value = "sk-x",
        };

        Assert.False(credential.IsPair);
        Assert.Equal("Authorization: Bearer …", credential.Describe());
    }
}
