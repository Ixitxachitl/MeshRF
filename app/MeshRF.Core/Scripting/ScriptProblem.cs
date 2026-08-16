// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

public enum ScriptProblemSeverity
{
    /// <summary>The script cannot run, and the editor refuses to save it.</summary>
    Error,
    /// <summary>The script runs, but probably not as intended (an unknown
    /// placeholder, a limit that disables throttling). Saving is allowed.</summary>
    Warning,
}

/// <summary>
/// One thing wrong with a script, carrying the position that produced it so the
/// editor can jump the caret there. Line and column are 1-based, matching what
/// YamlDotNet's marks report and what a text editor shows.
/// </summary>
/// <param name="Severity">Whether this blocks saving.</param>
/// <param name="Line">1-based line, or 0 when the problem is file-wide.</param>
/// <param name="Column">1-based column, or 0 when unknown.</param>
/// <param name="Message">Plain-language description, ending without a period so
/// the UI can append context.</param>
public readonly record struct ScriptProblem(
    ScriptProblemSeverity Severity,
    int Line,
    int Column,
    string Message)
{
    // long overloads: YamlDotNet's source marks are long, and every call site
    // here passes one straight through. Narrowing once, here, keeps the casts
    // out of the parser's error paths.
    public static ScriptProblem Error(long line, long column, string message) =>
        new(ScriptProblemSeverity.Error, (int)line, (int)column, message);

    public static ScriptProblem Warning(long line, long column, string message) =>
        new(ScriptProblemSeverity.Warning, (int)line, (int)column, message);

    /// <summary>"Line 7, column 3: unknown trigger 'txt'" — the form shown in
    /// the editor's problem list.</summary>
    public override string ToString() =>
        Line > 0
            ? Column > 0
                ? $"Line {Line}, column {Column}: {Message}"
                : $"Line {Line}: {Message}"
            : Message;
}

/// <summary>The outcome of parsing one script file: the script when it is
/// valid, plus every problem found. A result with any
/// <see cref="ScriptProblemSeverity.Error"/> has a null
/// <see cref="Script"/>.</summary>
/// <param name="Script">The parsed script, or null if it had errors or the file
/// is a feed sync rather than a script.</param>
/// <param name="Problems">Everything found, errors and warnings alike.</param>
/// <param name="Sync">The parsed feed sync, for a file whose top level is
/// <c>sync:</c>. Exactly one of this and <paramref name="Script"/> is set on a
/// valid file.</param>
public sealed record ScriptParseResult(
    MeshScript? Script,
    IReadOnlyList<ScriptProblem> Problems,
    MeshFeedSync? Sync = null)
{
    public bool IsValid => Script is not null || Sync is not null;

    /// <summary>Whether this file mirrors a feed rather than answering events.</summary>
    public bool IsSync => Sync is not null;

    /// <summary>Name for the list, whichever kind the file turned out to be.</summary>
    public string Alias =>
        Script?.Alias is { Length: > 0 } scriptAlias ? scriptAlias
        : Sync?.Alias is { Length: > 0 } syncAlias ? syncAlias
        : string.Empty;

    public bool Enabled => Script?.Enabled ?? Sync?.Enabled ?? false;

    public bool HasErrors => Problems.Any(p => p.Severity == ScriptProblemSeverity.Error);

    public bool HasWarnings => Problems.Any(p => p.Severity == ScriptProblemSeverity.Warning);

    /// <summary>The first error, for the one-line summary in the script list.</summary>
    public ScriptProblem? FirstError
    {
        get
        {
            foreach (var p in Problems)
                if (p.Severity == ScriptProblemSeverity.Error) return p;
            return null;
        }
    }
}
