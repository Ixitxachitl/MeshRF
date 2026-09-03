// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// What the script editor offers at the caret. The window only shows the list
/// and splices the chosen value in, so everything worth being sure about — that
/// it fires on the right keys, matches what has been typed, and hands back the
/// span to replace — is decided here.
/// </summary>
public class ScriptCompletionTests
{
    private static readonly ScriptCompletionSource Known = new(
        Channels:
        [
            new ScriptSuggestion("LongFast", "primary channel"),
            new ScriptSuggestion("Test", "channel 1"),
        ],
        Nodes:
        [
            new ScriptSuggestion("!a1b2c3d4", "\"!a1b2c3d4\"", "Ridge Repeater (RIDG)", NoteInFile: true),
            new ScriptSuggestion("!deadbeef", "\"!deadbeef\"", "Bob's Handheld (BOB)", NoteInFile: true),
        ],
        Credentials: ["openweather"],
        Geofences:
        [
            new ScriptSuggestion("North Gate", "North Gate", "circle, 500 m"),
            new ScriptSuggestion("Yes", "\"Yes\"", "box"),
        ]);

    /// <summary>Completes at the end of the text, which is where a caret sits
    /// while someone is typing the line.</summary>
    private static ScriptCompletionResult? At(string text) =>
        ScriptCompletion.Suggest(text, text.Length, Known);

    [Fact]
    public void A_Channel_Offers_The_Configured_Ones_And_The_Primary_By_Role()
    {
        var result = At("action:\n  - send:\n      channel: ");

        Assert.NotNull(result);
        Assert.Equal(["{primary}", "LongFast", "Test"], result!.Suggestions.Select(s => s.Label));
    }

    [Fact]
    public void A_Channel_Condition_Is_Offered_The_Primary_Token_Too()
    {
        // It means the same thing in a condition as it does where a
        // destination is chosen — the primary by role — so the list offers it
        // in both places.
        foreach (var key in new[] { "channel", "not_channel" })
        {
            var result = At($"trigger:\n  - command: ping\ncondition:\n  - {key}: ");
            Assert.NotNull(result);
            Assert.Equal([ScriptChannels.PrimaryToken, "LongFast", "Test"],
                         result!.Suggestions.Select(s => s.Label));
        }
    }

    [Fact]
    public void A_Destination_Channel_Offers_It_As_Well()
    {
        foreach (var text in new[]
                 {
                     "action:\n  - send:\n      channel: ",
                     "action:\n  - waypoint:\n      channel: ",
                     "sync:\n  waypoint:\n    channel: ",
                 })
        {
            var result = At(text);
            Assert.NotNull(result);
            Assert.Equal(ScriptChannels.PrimaryToken, result!.Suggestions[0].Label);
        }
    }

    [Fact]
    public void A_Node_Offers_The_Sender_And_Every_Known_Node()
    {
        var result = At("action:\n  - send:\n      to: ");

        Assert.NotNull(result);
        Assert.Equal(["{from.id}", "!a1b2c3d4", "!deadbeef"], result!.Suggestions.Select(s => s.Label));

        // Quoted on the way in: a bare !a1b2c3d4 opens a YAML tag and the file
        // stops parsing.
        Assert.Equal("\"!a1b2c3d4\"", result.Suggestions[1].Insert);
        Assert.Equal("Ridge Repeater (RIDG)", result.Suggestions[1].Note);
    }

    [Fact]
    public void A_Condition_Offers_Nodes_And_This_Node_But_Not_The_Sender_Placeholder()
    {
        // from:/not_from: are matched against literal ids by the engine, so
        // {from.id} in one would never match anything. {my.id} is the exception
        // — the engine resolves that one against this node.
        foreach (var key in new[] { "from", "not_from" })
        {
            var result = At($"condition:\n  - {key}: ");
            Assert.NotNull(result);
            Assert.Equal(["{my.id}", "!a1b2c3d4", "!deadbeef"], result!.Suggestions.Select(s => s.Label));
        }
    }

    [Fact]
    public void What_Has_Been_Typed_Filters_The_List_And_Is_What_Gets_Replaced()
    {
        const string text = "action:\n  - send:\n      to: \"!dead";
        var result = At(text);

        Assert.NotNull(result);
        Assert.Equal("!deadbeef", Assert.Single(result!.Suggestions).Label);

        // The span starts at the opening quote, not after it, so accepting a
        // suggestion that brings its own quotes cannot leave two.
        Assert.Equal(text.IndexOf("\"!dead", StringComparison.Ordinal), result.Start);
        Assert.Equal("\"!dead".Length, result.Length);
    }

    [Fact]
    public void A_List_Completes_The_Entry_Being_Typed_Rather_Than_The_Whole_List()
    {
        var result = At("condition:\n  - channel: [LongFast, Te");

        Assert.NotNull(result);
        Assert.Equal("Test", Assert.Single(result!.Suggestions).Label);
        Assert.Equal("Te".Length, result.Length);
    }

    [Fact]
    public void A_Prefix_Matching_Nothing_Offers_Nothing()
    {
        Assert.Null(At("action:\n  - send:\n      channel: Zzz"));
    }

    [Fact]
    public void Keys_That_Name_Nothing_This_Node_Knows_Are_Left_Alone()
    {
        // Every other key is either free text or in the Help window; a list
        // popping up over "text:" would be in the way rather than useful.
        Assert.Null(At("action:\n  - send:\n      text: Long"));
        Assert.Null(At("action:\n  - reply: \"pong\""));
        Assert.Null(At("alias: Test"));
    }

    [Fact]
    public void A_Url_Is_Not_Mistaken_For_A_Value_To_Complete()
    {
        // The colon in https:// is what keeps this off it — and url: is not an
        // offered key either way, so both guards have to fail to reach a list.
        Assert.Null(At("action:\n  - http:\n      url: \"https://api.example.com/to: "));
    }

    [Fact]
    public void A_Comment_Is_Offered_Only_When_The_Rest_Of_The_Line_Is_Empty()
    {
        // Caret right after "to: ", with nothing else on that line.
        const string clear = "action:\n  - send:\n      to: \n      text: x";
        int caret = clear.IndexOf("to: ", StringComparison.Ordinal) + "to: ".Length;
        Assert.True(ScriptCompletion.Suggest(clear, caret, Known)!.AllowComment);

        // And with something already there, so a comment would land mid-line.
        const string occupied = "action:\n  - send:\n      to:  # the operator";
        int occupiedCaret = occupied.IndexOf("to: ", StringComparison.Ordinal) + "to: ".Length;
        Assert.False(ScriptCompletion.Suggest(occupied, occupiedCaret, Known)!.AllowComment);
    }

    [Fact]
    public void Without_A_Radio_Session_Only_What_Needs_No_Radio_Is_Offered()
    {
        // The window opens with no runtime behind it — the render harness does
        // this — so there are no channels or nodes to name. The two values that
        // mean something on any mesh are still worth offering.
        const string text = "action:\n  - send:\n      channel: ";
        var channels = ScriptCompletion.Suggest(text, text.Length, ScriptCompletionSource.Empty);
        Assert.Equal(ScriptChannels.PrimaryToken, Assert.Single(channels!.Suggestions).Label);

        const string node = "action:\n  - send:\n      to: ";
        var nodes = ScriptCompletion.Suggest(node, node.Length, ScriptCompletionSource.Empty);
        Assert.Equal("{from.id}", Assert.Single(nodes!.Suggestions).Label);

        // And a key with nothing behind it at all offers nothing, so the popup
        // closes rather than showing an empty panel.
        const string credential = "action:\n  - http:\n      credential: ";
        Assert.Null(ScriptCompletion.Suggest(credential, credential.Length, ScriptCompletionSource.Empty));
    }

    [Fact]
    public void A_Geofence_Trigger_Offers_The_Fences_On_The_Map_And_Any()
    {
        var result = At("trigger:\n  - geofence: ");

        Assert.NotNull(result);
        Assert.Equal(["any", "North Gate", "Yes"], result!.Suggestions.Select(s => s.Label));
    }

    [Fact]
    public void A_Geofence_Name_Is_Matched_On_What_Has_Been_Typed()
    {
        var result = At("trigger:\n  - geofence: Nor");

        Assert.NotNull(result);
        Assert.Equal(["North Gate"], result!.Suggestions.Select(s => s.Label));
        // The typed prefix is what gets replaced, not the whole value.
        Assert.Equal(3, result.Length);
    }

    /// <summary>A fence name is inserted quoted only when YAML would otherwise
    /// read it as something else — the list shows the bare name either way.</summary>
    [Fact]
    public void A_Fence_Name_YAML_Would_Misread_Is_Inserted_Quoted()
    {
        Assert.Equal("North Gate", ScriptCompletion.QuoteForYaml("North Gate"));
        Assert.Equal("\"Yes\"", ScriptCompletion.QuoteForYaml("Yes"));
        Assert.Equal("\"3\"", ScriptCompletion.QuoteForYaml("3"));
        Assert.Equal("\"#3\"", ScriptCompletion.QuoteForYaml("#3"));
        Assert.Equal("\"Gate: North\"", ScriptCompletion.QuoteForYaml("Gate: North"));
        // A quote inside the value is only a quote — YAML reads a plain scalar
        // to the end of the line, so this needs no wrapping.
        Assert.Equal("He said \"go\"", ScriptCompletion.QuoteForYaml("He said \"go\""));
        // One that opens the value does, and is escaped on the way in.
        Assert.Equal("\"\\\"Home\\\"\"", ScriptCompletion.QuoteForYaml("\"Home\""));
    }
}
