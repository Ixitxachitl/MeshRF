// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;

namespace MeshRF.Scripting;

/// <summary>One script on disk, as the Scripts window lists it.</summary>
/// <param name="FileName">File name including extension. This is the script's
/// identity — there is no id: key inside the file.</param>
/// <param name="FullPath">Absolute path, for the "open in editor" button.</param>
/// <param name="Text">The file's raw contents, comments and all.</param>
/// <param name="Enabled">Read from the top-level <c>enabled:</c> line, even
/// when the rest of the file fails to parse.</param>
/// <param name="Parse">Validation outcome, so the list can flag broken files.</param>
public sealed record ScriptFile(
    string FileName,
    string FullPath,
    string Text,
    bool Enabled,
    ScriptParseResult Parse)
{
    /// <summary>Name without the .yaml, used as the list label when the script
    /// has no alias: (or can't be parsed to find one).</summary>
    public string DisplayName =>
        Parse.Script is { Alias.Length: > 0 } script ? script.Alias : Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>
/// The scripts directory: one YAML file per script, plus a small sidecar that
/// records the order they run in.
/// </summary>
/// <remarks>
/// <para>Two pieces of state live outside the YAML body itself, and they are
/// stored differently on purpose.</para>
/// <para><c>enabled:</c> stays inside the file, because a hand-editing user
/// expects to see and set it there. The list's toggle therefore splices just
/// that one value rather than re-serialising the document — round-tripping
/// through a YAML emitter would strip every comment the user wrote.</para>
/// <para>Order lives in a separate <c>.order</c> sidecar, because the
/// alternative (an <c>order:</c> key in every file) means a single drag rewrites
/// half the directory. Files missing from the sidecar sort last, alphabetically,
/// so a script dropped into the folder by hand still shows up.</para>
/// </remarks>
public sealed class ScriptLibrary
{
    private const string OrderFileName = ".order";

    private readonly string _directory;

    /// <summary>Uses the standard location under %APPDATA%\MeshRF, alongside
    /// settings.json and the node/message databases.</summary>
    public ScriptLibrary() : this(DefaultDirectory) { }

    /// <summary>Overridable for tests, which point at a temp directory.</summary>
    public ScriptLibrary(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public static string DefaultDirectory => AppData.SubdirectoryFor("scripts");

    public string DirectoryPath => _directory;

    /// <summary>Every script in the directory, in execution order. Unreadable
    /// files are skipped rather than throwing: one bad file shouldn't empty the
    /// list.</summary>
    public IReadOnlyList<ScriptFile> Load()
    {
        var files = new List<string>();
        foreach (var pattern in new[] { "*.yaml", "*.yml" })
        {
            try { files.AddRange(Directory.GetFiles(_directory, pattern)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files) byName[Path.GetFileName(path)] = path;

        var results = new List<ScriptFile>();
        foreach (var name in ApplyOrder(byName.Keys))
        {
            string text;
            try { text = File.ReadAllText(byName[name]); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            results.Add(new ScriptFile(
                FileName: name,
                FullPath: byName[name],
                Text: text,
                Enabled: ReadEnabled(text),
                Parse: ScriptParser.Parse(text)));
        }
        return results;
    }

    /// <summary>Marks a folder as already seeded, so the samples are written
    /// once and a deleted one stays deleted.</summary>
    private const string SamplesMarkerName = ".samples";

    /// <summary>
    /// Writes the sample scripts into an empty scripts folder, once. Returns
    /// the names installed, or nothing when the folder has already been seeded
    /// or already holds scripts.
    /// </summary>
    /// <remarks>
    /// <para>Samples that ship inert are worth installing rather than
    /// documenting: the vocabulary is easier to read from a working file than
    /// from a reference, and every one of them arrives <c>enabled: false</c> so
    /// nothing transmits because MeshRF was opened.</para>
    /// <para>Two guards keep this from fighting the user. A folder that already
    /// contains scripts is left completely alone — an upgrade must not drop six
    /// files into a set someone has curated — and the marker means a sample
    /// deleted on purpose is not restored on the next start.</para>
    /// </remarks>
    public IReadOnlyList<string> InstallSamples()
    {
        var marker = Path.Combine(_directory, SamplesMarkerName);
        if (File.Exists(marker)) return [];

        try
        {
            // Already has scripts: adopt the folder as set up rather than
            // adding to it, and record that so this never runs again.
            if (Directory.EnumerateFiles(_directory, "*.yaml").Any() ||
                Directory.EnumerateFiles(_directory, "*.yml").Any())
            {
                WriteMarker(marker);
                return [];
            }

            var installed = new List<string>();
            var assembly = typeof(ScriptLibrary).Assembly;

            foreach (var resource in assembly.GetManifestResourceNames()
                                             .Where(n => n.StartsWith("samples/", StringComparison.Ordinal))
                                             .OrderBy(n => n, StringComparer.Ordinal))
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) continue;

                using var reader = new StreamReader(stream);
                var fileName = resource["samples/".Length..];
                File.WriteAllText(Path.Combine(_directory, fileName), reader.ReadToEnd(), Utf8NoBom);
                installed.Add(fileName);
            }

            WriteMarker(marker);
            return installed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder we cannot write is not a reason to fail to start; the
            // Scripts window will simply show an empty list.
            return [];
        }
    }

    private static void WriteMarker(string path) =>
        File.WriteAllText(path,
            "# Written once, when MeshRF installed its sample scripts here.\n" +
            "# Delete this file to have them installed again on the next start.\n",
            Utf8NoBom);

    /// <summary>Creates a new script from the starter template and returns its
    /// file name. The name is sanitised and de-duplicated, so a user typing
    /// "Auto reply!" twice gets auto-reply.yaml and auto-reply-2.yaml.</summary>
    public string Create(string requestedName)
    {
        var baseName = Sanitize(requestedName);
        if (baseName.Length == 0) baseName = "script";

        var fileName = $"{baseName}.yaml";
        int suffix = 2;
        while (File.Exists(Path.Combine(_directory, fileName)))
            fileName = $"{baseName}-{suffix++}.yaml";

        File.WriteAllText(Path.Combine(_directory, fileName), StarterTemplate(baseName), Utf8NoBom);
        Append(fileName);
        return fileName;
    }

    public void Save(string fileName, string text) =>
        File.WriteAllText(Path.Combine(_directory, fileName), text, Utf8NoBom);

    public void Delete(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        if (File.Exists(path)) File.Delete(path);
        SaveOrder(ReadOrder().Where(n => !n.Equals(fileName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Flips the top-level <c>enabled:</c> value in place, leaving every other
    /// byte of the file — comments, blank lines, quoting style, line endings —
    /// exactly as the user wrote it. A file that never had the key gets one
    /// inserted below its leading comment block, which is where a reader would
    /// look for it.
    /// </summary>
    public void SetEnabled(string fileName, bool enabled)
    {
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path)) return;

        var text = File.ReadAllText(path);
        var match = s_enabledLine.Match(text);
        string updated;

        if (match.Success)
        {
            var value = match.Groups["value"];
            updated = string.Concat(
                text.AsSpan(0, value.Index),
                enabled ? "true" : "false",
                text.AsSpan(value.Index + value.Length));
        }
        else
        {
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            int insertAt = AfterLeadingComments(text, newline);
            updated = text[..insertAt] + $"enabled: {(enabled ? "true" : "false")}{newline}" + text[insertAt..];
        }

        File.WriteAllText(path, updated, Utf8NoBom);
    }

    /// <summary>Persists the execution order shown in the list.</summary>
    public void SetOrder(IEnumerable<string> fileNames) => SaveOrder(fileNames);

    // ----- enabled: splicing --------------------------------------------------

    /// <summary>
    /// The top-level <c>enabled:</c> line. Anchored at column 0 so a nested
    /// key of the same name (there is none today, but scripts grow) can't be
    /// mistaken for it, and the value is captured on its own so a trailing
    /// comment survives the splice.
    /// </summary>
    private static readonly Regex s_enabledLine = new(
        @"^enabled:[ \t]*(?<value>[^\s#]+)",
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool ReadEnabled(string text)
    {
        var match = s_enabledLine.Match(text);
        if (!match.Success) return false;
        return match.Groups["value"].Value.Trim().ToLowerInvariant() is "true" or "yes" or "on";
    }

    /// <summary>Index just past the file's leading run of comments and blank
    /// lines — where an inserted key belongs, under the header rather than
    /// above it.</summary>
    private static int AfterLeadingComments(string text, string newline)
    {
        int index = 0;
        while (index < text.Length)
        {
            int lineEnd = text.IndexOf('\n', index);
            if (lineEnd < 0) lineEnd = text.Length - 1;

            var line = text[index..Math.Min(lineEnd + 1, text.Length)].Trim();
            if (line.Length != 0 && !line.StartsWith('#')) break;

            index = lineEnd + 1;
        }
        // A file that is nothing but comments needs a separating blank line so
        // the inserted key doesn't look like part of the header.
        return index == text.Length && text.Length > 0 && !text.EndsWith(newline, StringComparison.Ordinal)
            ? text.Length
            : index;
    }

    // ----- order sidecar ------------------------------------------------------

    private string OrderPath => Path.Combine(_directory, OrderFileName);

    private List<string> ReadOrder()
    {
        try
        {
            if (!File.Exists(OrderPath)) return [];
            return File.ReadAllLines(OrderPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private void SaveOrder(IEnumerable<string> fileNames)
    {
        var body = new StringBuilder()
            .AppendLine("# Execution order for MeshRF automation scripts.")
            .AppendLine("# When one event triggers several scripts, they run top to bottom.")
            .AppendLine("# Managed by the Scripts window; files not listed here run last, in name order.");
        foreach (var name in fileNames) body.AppendLine(name);

        try { File.WriteAllText(OrderPath, body.ToString(), Utf8NoBom); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Append(string fileName)
    {
        var order = ReadOrder();
        if (!order.Contains(fileName, StringComparer.OrdinalIgnoreCase)) order.Add(fileName);
        SaveOrder(order);
    }

    /// <summary>Sidecar order first, then anything it doesn't mention, sorted by
    /// name so a hand-dropped file lands somewhere predictable.</summary>
    private List<string> ApplyOrder(IEnumerable<string> presentFiles)
    {
        var present = new HashSet<string>(presentFiles, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var name in ReadOrder())
        {
            if (present.Remove(name)) ordered.Add(name);
        }
        ordered.AddRange(present.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    // ----- naming -------------------------------------------------------------

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Folds a typed name down to something safe on every filesystem:
    /// lowercase, ASCII word characters and dashes only.</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '.') sb.Append('-');
        }
        // Collapse runs and trim the dashes that punctuation leaves behind.
        var collapsed = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return collapsed.Length > 64 ? collapsed[..64].TrimEnd('-') : collapsed;
    }

    /// <summary>The contents of a brand-new script. Disabled, and commented
    /// heavily enough to serve as the first thing a user reads — the Help
    /// window is the reference, but this is what they actually see first.</summary>
    internal static string StarterTemplate(string name) =>
        // $$ so that a single brace is literal: this template is mostly
        // {placeholder} text, and only the name is interpolated.
        $$"""
        # {{name}}
        #
        # A MeshRF automation script. Press Help in the Scripts window for the
        # full list of triggers, conditions, actions and {placeholders}.

        enabled: false

        alias: Describe what this script does

        # What wakes the script up. Any one trigger firing is enough.
        trigger:
          - command: ping

        # Every condition has to hold, or the actions are skipped.
        # Delete this whole block to answer anything the trigger matched.
        condition:
          - scope: direct

        # Run top to bottom when the script fires.
        action:
          - reply: "pong — {snr} dB over {hops} hops"

        # How often this script is allowed to answer. The engine also applies a
        # global budget across all scripts, so these are a ceiling, not a quota.
        limits:
          cooldown: 60s
          per_node: true
          max_per_hour: 6

        """;
}
