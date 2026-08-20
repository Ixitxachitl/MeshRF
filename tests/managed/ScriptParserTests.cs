// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

public class ScriptParserTests
{
    private const string Minimal =
        """
        trigger:
          - command: ping
        action:
          - reply: "pong"
        """;

    private static ScriptProblem SingleError(ScriptParseResult result)
    {
        var errors = result.Problems.Where(p => p.Severity == ScriptProblemSeverity.Error).ToList();
        Assert.Single(errors);
        return errors[0];
    }

    [Fact]
    public void Minimal_Script_Parses()
    {
        var result = ScriptParser.Parse(Minimal);

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
        var script = result.Script!;
        Assert.Equal(ScriptTriggerKind.Command, Assert.Single(script.Triggers).Kind);
        Assert.Equal(ScriptActionKind.Reply, Assert.Single(script.Actions).Kind);
        // Absent limits fall back to the throttled defaults rather than to none.
        Assert.Equal(TimeSpan.FromSeconds(60), script.Limits.Cooldown);
        Assert.Equal(6, script.Limits.MaxPerHour);
    }

    [Fact]
    public void Full_Script_Parses_Every_Section()
    {
        var result = ScriptParser.Parse(
            """
            enabled: true
            alias: Signal report
            mode: restart

            trigger:
              - text: "^!wx (.+)$"
                ignore_case: false
              - every: 4h

            condition:
              - scope: direct
              - channel: [LongFast, Backup]
              - snr_above: -12.5
              - between: "08:00-22:00"

            action:
              - delay: 30s
              - send:
                  to: "{from.id}"
                  text: "{cap1} — {snr} dB"
                  reply_link: true

            limits:
              cooldown: 5m
              per_node: false
              max_per_hour: 20
            """);

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);

        var script = result.Script!;
        Assert.True(script.Enabled);
        Assert.Equal("Signal report", script.Alias);
        Assert.Equal(ScriptMode.Restart, script.Mode);
        Assert.Equal(2, script.Triggers.Count);
        Assert.False(script.Triggers[0].IgnoreCase);
        Assert.Equal(TimeSpan.FromHours(4), script.Triggers[1].Interval);
        Assert.Equal(4, script.Conditions.Count);
        Assert.Equal(new TimeOnly(8, 0), script.Conditions[3].Start);
        Assert.Equal(new TimeOnly(22, 0), script.Conditions[3].End);
        Assert.Equal(TimeSpan.FromSeconds(30), script.Actions[0].Delay);
        Assert.Equal("{from.id}", script.Actions[1].To);
        Assert.True(script.Actions[1].ReplyLink);
        Assert.Equal(TimeSpan.FromMinutes(5), script.Limits.Cooldown);
        Assert.False(script.Limits.PerNode);
        Assert.Equal(20, script.Limits.MaxPerHour);
    }

    [Fact]
    public void A_Single_Mapping_Is_Accepted_Where_A_List_Is_Expected()
    {
        // A one-trigger script reads better without the dash, and this is the
        // shape people write first.
        var result = ScriptParser.Parse(
            """
            trigger:
              command: ping
            action:
              reply: "pong"
            """);

        Assert.True(result.IsValid);
        Assert.Single(result.Script!.Triggers);
    }

    [Fact]
    public void Missing_Trigger_Is_An_Error()
    {
        var result = ScriptParser.Parse("action:\n  - reply: \"hi\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("no trigger:", SingleError(result).Message);
    }

    [Fact]
    public void Missing_Action_Is_An_Error()
    {
        var result = ScriptParser.Parse("trigger:\n  - command: ping\n");

        Assert.False(result.IsValid);
        Assert.Contains("no action:", SingleError(result).Message);
    }

    [Fact]
    public void Empty_Script_Is_An_Error()
    {
        Assert.False(ScriptParser.Parse("   \n\n").IsValid);
        Assert.False(ScriptParser.Parse("# just a comment\n").IsValid);
    }

    [Fact]
    public void Misspelled_Top_Level_Key_Suggests_The_Right_One()
    {
        var result = ScriptParser.Parse("triggers:\n  - command: ping\naction:\n  - reply: \"x\"\n");

        Assert.False(result.IsValid);
        var problems = result.Problems.Where(p => p.Severity == ScriptProblemSeverity.Error).ToList();
        Assert.Contains(problems, p => p.Message.Contains("did you mean 'trigger'?"));
        // The position has to point at the offending key, not the file.
        Assert.Equal(1, problems[0].Line);
    }

    [Fact]
    public void Misspelled_Action_Kind_Suggests_The_Right_One()
    {
        var result = ScriptParser.Parse("trigger:\n  - command: ping\naction:\n  - relpy: \"x\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("did you mean 'reply'?", SingleError(result).Message);
    }

    [Fact]
    public void Unrelated_Key_Gets_No_Misleading_Suggestion()
    {
        var result = ScriptParser.Parse("banana:\n  - 1\ntrigger:\n  - command: ping\naction:\n  - reply: \"x\"\n");

        Assert.False(result.IsValid);
        Assert.DoesNotContain("did you mean", SingleError(result).Message);
    }

    [Fact]
    public void Tab_Indentation_Explains_Itself()
    {
        var result = ScriptParser.Parse("trigger:\n\t- command: ping\naction:\n  - reply: \"x\"\n");

        Assert.False(result.IsValid);
        // YamlDotNet's own wording talks about tokens; ours has to name tabs.
        Assert.Contains("tab", SingleError(result).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unclosed_Quote_Explains_Itself()
    {
        var result = ScriptParser.Parse("trigger:\n  - command: ping\naction:\n  - reply: \"unterminated\n");

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(SingleError(result).Message));
        Assert.True(SingleError(result).Line > 0);
    }

    [Fact]
    public void Invalid_Regex_Is_Rejected_With_The_Reason()
    {
        var result = ScriptParser.Parse("trigger:\n  - text: \"^!(unclosed\"\naction:\n  - reply: \"x\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("not a valid pattern", SingleError(result).Message);
    }

    [Fact]
    public void Two_Kinds_In_One_Entry_Are_Rejected()
    {
        var result = ScriptParser.Parse("trigger:\n  - command: ping\n    every: 5m\naction:\n  - reply: \"x\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("more than one kind", SingleError(result).Message);
    }

    [Fact]
    public void Send_Cannot_Address_A_Node_And_A_Channel_At_Once()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - send:
                  to: "!a1b2c3d4"
                  channel: LongFast
                  text: "hi"
            """);

        Assert.False(result.IsValid);
        Assert.Contains("not both", SingleError(result).Message);
    }

    [Fact]
    public void Send_Without_Text_Is_Rejected()
    {
        var result = ScriptParser.Parse(
            "trigger:\n  - command: ping\naction:\n  - send:\n      to: \"!a1b2c3d4\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("needs a text:", SingleError(result).Message);
    }

    [Fact]
    public void Bare_Node_Id_Is_Rejected_When_It_Is_Not_One()
    {
        var result = ScriptParser.Parse("trigger:\n  - command: ping\ncondition:\n  - from: [bob]\naction:\n  - reply: \"x\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("not a node id", SingleError(result).Message);
    }

    [Fact]
    public void Placeholder_In_To_Is_Allowed_Because_It_Resolves_At_Fire_Time()
    {
        var result = ScriptParser.Parse(
            "trigger:\n  - command: ping\naction:\n  - send:\n      to: \"{from.id}\"\n      text: \"hi\"\n");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Unknown_Placeholder_Warns_Without_Blocking_The_Save()
    {
        var result = ScriptParser.Parse("trigger:\n  - command: ping\naction:\n  - reply: \"hi {from.shrt}\"\n");

        // The whole point of the warning tier: this parses and would run, it
        // just would not say what the author meant.
        Assert.True(result.IsValid);
        Assert.False(result.HasErrors);
        var warning = Assert.Single(result.Problems);
        Assert.Equal(ScriptProblemSeverity.Warning, warning.Severity);
        Assert.Contains("did you mean 'from.short'?", warning.Message);
    }

    [Fact]
    public void Numbered_Placeholders_Are_Known()
    {
        var result = ScriptParser.Parse(
            "trigger:\n  - text: \"^!echo (.+)$\"\naction:\n  - reply: \"{cap1} {arg2}\"\n");

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Command_With_A_Leading_Bang_Warns_And_Strips_It()
    {
        var result = ScriptParser.Parse("trigger:\n  - command: \"!ping\"\naction:\n  - reply: \"x\"\n");

        Assert.True(result.IsValid);
        Assert.Equal("ping", result.Script!.Triggers[0].Pattern);
        Assert.Equal(ScriptProblemSeverity.Warning, Assert.Single(result.Problems).Severity);
    }

    [Fact]
    public void Every_Below_A_Minute_Is_Rejected()
    {
        var result = ScriptParser.Parse("trigger:\n  - every: 10s\naction:\n  - reply: \"x\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("at least 1m", SingleError(result).Message);
    }

    [Fact]
    public void Overlong_Message_Warns_About_Truncation()
    {
        var result = ScriptParser.Parse(
            $"trigger:\n  - command: ping\naction:\n  - reply: \"{new string('x', 250)}\"\n");

        Assert.True(result.IsValid);
        Assert.Contains(result.Problems, p => p.Message.Contains("truncates"));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("30", 30)]
    [InlineData("5m", 300)]
    [InlineData("4h", 14400)]
    [InlineData("1d", 86400)]
    [InlineData("1.5h", 5400)]
    [InlineData("90 minutes", 5400)]
    public void Durations_Parse(string text, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ScriptParser.ParseDuration(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("5 fortnights")]
    [InlineData("-5m")]
    public void Nonsense_Durations_Do_Not_Parse(string text)
    {
        Assert.Null(ScriptParser.ParseDuration(text));
    }

    [Theory]
    [InlineData("08:00", 8, 0)]
    [InlineData("8:00", 8, 0)]
    [InlineData("22:30", 22, 30)]
    public void Times_Parse(string text, int hour, int minute)
    {
        Assert.Equal(new TimeOnly(hour, minute), ScriptParser.ParseTime(text));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("noon")]
    [InlineData("8")]
    public void Nonsense_Times_Do_Not_Parse(string text)
    {
        Assert.Null(ScriptParser.ParseTime(text));
    }

    [Theory]
    [InlineData("any", ScriptScope.Any)]
    [InlineData("direct", ScriptScope.Direct)]
    [InlineData("channel", ScriptScope.Channel)]
    [InlineData("primary", ScriptScope.Primary)]
    public void Every_Scope_Parses(string text, ScriptScope expected)
    {
        var result = ScriptParser.Parse(
            $"trigger:\n  - command: ping\ncondition:\n  - scope: {text}\naction:\n  - reply: \"ok\"\n");

        Assert.True(result.IsValid, result.FirstError?.ToString());
        Assert.Equal(expected, Assert.Single(result.Script!.Conditions).Scope);
    }

    [Theory]
    [InlineData("channel", ScriptConditionKind.Channel)]
    [InlineData("not_channel", ScriptConditionKind.NotChannel)]
    public void A_Channel_Condition_Takes_One_Name_Or_A_List(string key, ScriptConditionKind expected)
    {
        var one = ScriptParser.Parse(
            $"trigger:\n  - command: ping\ncondition:\n  - {key}: Test\naction:\n  - reply: \"ok\"\n");
        Assert.True(one.IsValid, one.FirstError?.ToString());
        var condition = Assert.Single(one.Script!.Conditions);
        Assert.Equal(expected, condition.Kind);
        Assert.Equal(["Test"], condition.Values);

        var many = ScriptParser.Parse(
            $"trigger:\n  - command: ping\ncondition:\n  - {key}: [Test, Backup]\naction:\n  - reply: \"ok\"\n");
        Assert.True(many.IsValid, many.FirstError?.ToString());
        Assert.Equal(["Test", "Backup"], Assert.Single(many.Script!.Conditions).Values);
    }

    [Fact]
    public void An_Empty_Not_Channel_Is_Rejected()
    {
        // Excluding nothing is not a condition, and silently matching
        // everything is the wrong way to read a line someone meant to fill in.
        var result = ScriptParser.Parse(
            "trigger:\n  - command: ping\ncondition:\n  - not_channel: []\naction:\n  - reply: \"ok\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("needs at least one value", SingleError(result).Message);
    }

    [Fact]
    public void A_Mistyped_Filter_Warns_And_Suggests_The_Real_One()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - reply: "{snr|roudn:1} dB"
            """);

        // A warning, not an error: the script still sends, it just sends the
        // token as written, which is the mistake worth pointing at.
        Assert.True(result.IsValid, result.FirstError?.ToString());
        var warning = Assert.Single(result.Problems);
        Assert.Equal(ScriptProblemSeverity.Warning, warning.Severity);
        Assert.Contains("'roudn' is not a filter", warning.Message);
        Assert.Contains("did you mean 'round'", warning.Message);
    }

    [Fact]
    public void A_Filtered_Placeholder_Is_Still_Checked_For_Being_Real()
    {
        // The pipe must not become a way to smuggle a typo past the editor.
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - reply: "{from.shrt|upper}"
            """);

        var warning = Assert.Single(result.Problems);
        Assert.Contains("{from.shrt} is not a placeholder", warning.Message);
    }

    [Fact]
    public void An_Action_Can_Carry_A_When_Gate()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - reply: "close by"
                when:
                  value: "{hops}"
                  at_most: 1
              - reply: "a long way off"
                when:
                  value: "{hops}"
                  above: 1
            """);

        Assert.True(result.IsValid, result.FirstError?.ToString());
        var actions = result.Script!.Actions;
        Assert.Equal(2, actions.Count);
        Assert.Equal(ScriptComparison.AtMost, actions[0].When!.Comparison);
        Assert.Equal("{hops}", actions[0].When!.Value);
        Assert.Equal(ScriptComparison.Above, actions[1].When!.Comparison);
    }

    [Fact]
    public void A_When_Sits_Beside_An_Action_That_Nests_Its_Own_Options()
    {
        // The fiddly shape: when: is a sibling of send:, not one of its keys,
        // so it has to survive the nested action's own key check.
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - send:
                  to: "{from.id}"
                  text: "still there"
                when:
                  value: "{snr}"
                  below: -10
            """);

        Assert.True(result.IsValid, result.FirstError?.ToString());
        var action = Assert.Single(result.Script!.Actions);
        Assert.Equal(ScriptActionKind.Send, action.Kind);
        Assert.Equal(ScriptComparison.Below, action.When!.Comparison);
    }

    [Fact]
    public void A_When_On_A_Require_Is_Rejected()
    {
        // Two tests on one entry, one of which stops the sequence and one of
        // which skips it — no reading of that is the obvious one.
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - require:
                  value: "{http.body}"
                  not_empty: true
                when:
                  value: "{hops}"
                  equals: 0
            """);

        Assert.False(result.IsValid);
        Assert.Contains("require: cannot take a when:", SingleError(result).Message);
    }

    [Fact]
    public void A_Broken_When_Names_When_Rather_Than_Require()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - reply: "ok"
                when:
                  value: "{hops}"
            """);

        Assert.False(result.IsValid);
        var error = SingleError(result);
        Assert.StartsWith("when: needs one comparison", error.Message);
    }

    [Fact]
    public void Problems_Carry_A_Usable_Position()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - reply: "ok"
            limits:
              cooldown: whenever
            """);

        Assert.False(result.IsValid);
        var error = SingleError(result);
        Assert.Equal(6, error.Line);
        Assert.True(error.Column > 0);
        Assert.StartsWith("Line 6, column", error.ToString());
    }
}
