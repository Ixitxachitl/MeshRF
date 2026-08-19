// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MeshRF.Scripting;

/// <summary>
/// Turns a script's YAML text into a <see cref="MeshScript"/>, or into the list
/// of reasons it can't be one.
/// </summary>
/// <remarks>
/// Walks YamlDotNet's representation model by hand rather than using its
/// reflection-based deserializer. That costs more code but buys the two things
/// the Scripts editor is built around: every node carries its source mark, so
/// each problem can name the line and column that caused it; and unknown keys
/// become a "did you mean" instead of being silently dropped on the floor.
/// </remarks>
public static class ScriptParser
{
    private static readonly string[] TopLevelKeys =
        ["enabled", "alias", "mode", "trigger", "condition", "action", "limits"];

    private static readonly string[] TriggerKinds =
        ["text", "command", "new_node", "reaction", "every", "at"];

    private static readonly string[] ConditionKinds =
        ["scope", "channel", "from", "not_from", "snr_above", "hops_below", "between", "favorite", "has_key"];

    private static readonly string[] ActionKinds =
        ["reply", "send", "react", "position", "nodeinfo", "traceroute", "http", "waypoint", "require",
         "delay", "log", "ring"];

    private static readonly string[] RingKeys = ["tune", "volume"];

    private static readonly string[] WaypointKeys =
        ["lat", "lon", "name", "description", "icon", "radius", "expires",
         "notify_on_enter", "notify_on_exit", "to", "channel", "lock_to_me"];

    /// <summary>Comparators a require: may use. Exactly one per entry.</summary>
    private static readonly string[] RequireComparisons =
        ["equals", "not_equals", "above", "below", "at_least", "at_most", "between",
         "contains", "matches", "is_empty", "not_empty", "within"];

    private static readonly string[] RequireKeys =
        [.. RequireComparisons, "value", "ignore_case"];

    private static readonly string[] SendKeys = ["to", "channel", "text", "reply_link"];

    private static readonly string[] HttpKeys =
        ["url", "method", "credential", "json", "save_as", "timeout", "body", "content_type", "optional", "headers"];

    /// <summary>Longest a script may wait on one request. Long enough for a slow
    /// API, short enough that a hung endpoint cannot pin a script's run open.</summary>
    private static readonly TimeSpan MaxHttpTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] LimitKeys = ["cooldown", "per_node", "max_per_hour"];

    /// <summary>Regexes in scripts are user-supplied and run on the decode path,
    /// so they are compiled with a match timeout and validated at parse time.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static ScriptParseResult Parse(string yamlText)
    {
        var problems = new List<ScriptProblem>();

        if (string.IsNullOrWhiteSpace(yamlText))
        {
            problems.Add(ScriptProblem.Error(0, 0,
                "the script is empty — it needs at least a trigger: and an action:"));
            return new ScriptParseResult(null, problems);
        }

        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(yamlText));
        }
        catch (YamlException ex)
        {
            problems.Add(ScriptProblem.Error(ex.Start.Line, ex.Start.Column, DescribeYamlError(ex)));
            return new ScriptParseResult(null, problems);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is null)
        {
            problems.Add(ScriptProblem.Error(0, 0,
                "the script is empty — it needs at least a trigger: and an action:"));
            return new ScriptParseResult(null, problems);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            var start = stream.Documents[0].RootNode.Start;
            problems.Add(ScriptProblem.Error(start.Line, start.Column,
                "a script has to be a set of 'key: value' entries at the top level"));
            return new ScriptParseResult(null, problems);
        }

        // A file is one kind or the other. Deciding here rather than by
        // filename keeps both in one folder, one list and one editor.
        if (TryGet(root, "sync", out _, out _)) return ParseSync(root, problems);

        RejectUnknownKeys(root, TopLevelKeys, "key", problems);

        bool enabled = ReadBool(root, "enabled", problems) ?? false;
        string alias = ReadString(root, "alias", problems) ?? string.Empty;
        var mode = ReadMode(root, problems);

        var triggers = ReadItems(root, "trigger", TriggerKinds, ParseTrigger, problems, required: true);
        var conditions = ReadItems(root, "condition", ConditionKinds, ParseCondition, problems, required: false);
        var actions = ReadItems(root, "action", ActionKinds, ParseAction, problems, required: true);
        var limits = ReadLimits(root, problems);

        // Ordering only affects readability, so it is a warning: a script whose
        // actions run before its conditions are declared still behaves the same.
        if (actions.Count == 0 && !problems.Any(p => p.Severity == ScriptProblemSeverity.Error))
        {
            problems.Add(ScriptProblem.Error(0, 0, "action: is empty — the script would do nothing"));
        }

        if (problems.Any(p => p.Severity == ScriptProblemSeverity.Error))
            return new ScriptParseResult(null, problems);

        var script = new MeshScript
        {
            Enabled = enabled,
            Alias = alias,
            Mode = mode,
            Triggers = triggers,
            Conditions = conditions,
            Actions = actions,
            Limits = limits,
        };
        return new ScriptParseResult(script, problems);
    }

    private static readonly string[] SyncTopLevelKeys = ["enabled", "alias", "sync"];

    private static readonly string[] SyncKeys =
        ["every", "url", "credential", "headers", "timeout", "items", "id", "active",
         "lat", "lon", "within", "watch", "waypoint"];

    private static readonly string[] SyncWaypointKeys =
        ["name", "description", "icon", "radius", "expires",
         "notify_on_enter", "notify_on_exit", "to", "channel", "lock_to_me"];

    /// <summary>
    /// Parses a feed sync. Shares enabled:/alias: with a script so the library,
    /// the enable toggle and the list all work on either without knowing which
    /// they hold.
    /// </summary>
    private static ScriptParseResult ParseSync(YamlMappingNode root, List<ScriptProblem> problems)
    {
        RejectUnknownKeys(root, SyncTopLevelKeys, "key", problems);

        bool enabled = ReadBool(root, "enabled", problems) ?? false;
        string alias = ReadString(root, "alias", problems) ?? string.Empty;

        TryGet(root, "sync", out var syncKey, out var syncValue);
        if (syncValue is not YamlMappingNode sync)
        {
            problems.Add(ScriptProblem.Error(syncKey.Start.Line, syncKey.Start.Column,
                "sync: needs indented url:/id:/lat: entries under it"));
            return new ScriptParseResult(null, problems);
        }
        RejectUnknownKeys(sync, SyncKeys, "sync key", problems);

        int line = (int)syncKey.Start.Line, column = (int)syncKey.Start.Column;

        var url = ReadString(sync, "url", problems) ?? string.Empty;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(ScriptProblem.Error(line, column,
                "sync: url: has to start with https:// or http://"));
            return new ScriptParseResult(null, problems);
        }
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(ScriptProblem.Warning(line, column,
                "sync: this URL is plain http, so the request and any credential travel unencrypted"));
        }

        var every = TimeSpan.FromMinutes(5);
        if (TryGet(sync, "every", out var everyKey, out var everyValue))
        {
            var parsed = ReadDuration(everyKey, everyValue, problems, "every");
            if (parsed is null) return new ScriptParseResult(null, problems);
            if (parsed.Value < TimeSpan.FromMinutes(1))
            {
                problems.Add(ScriptProblem.Error(everyKey.Start.Line, everyKey.Start.Column,
                    "sync: every: has to be at least 1m"));
                return new ScriptParseResult(null, problems);
            }
            every = parsed.Value;
        }

        var timeout = TimeSpan.FromSeconds(20);
        if (TryGet(sync, "timeout", out var timeoutKey, out var timeoutValue))
        {
            var parsed = ReadDuration(timeoutKey, timeoutValue, problems, "timeout");
            if (parsed is null || parsed.Value > MaxHttpTimeout)
            {
                problems.Add(ScriptProblem.Error(line, column,
                    $"sync: timeout: has to be a duration no longer than {MaxHttpTimeout.TotalSeconds:0}s"));
                return new ScriptParseResult(null, problems);
            }
            timeout = parsed.Value;
        }

        var credentials = new List<string>();
        if (TryGet(sync, "credential", out var credKey, out var credValue))
        {
            credentials = AsStringList(credValue, problems, "credential",
                (int)credKey.Start.Line, (int)credKey.Start.Column);
        }

        var headers = new List<ScriptHttpHeader>();
        if (TryGet(sync, "headers", out var headersKey, out var headersValue))
        {
            if (headersValue is not YamlMappingNode headerMap)
            {
                problems.Add(ScriptProblem.Error(headersKey.Start.Line, headersKey.Start.Column,
                    "sync: headers: needs indented name: value entries under it"));
                return new ScriptParseResult(null, problems);
            }
            foreach (var entry in headerMap.Children)
            {
                var headerName = Key(entry.Key);
                if (headerName.Length == 0 || headerName.Any(c => char.IsWhiteSpace(c) || c == ':'))
                {
                    problems.Add(ScriptProblem.Error(entry.Key.Start.Line, entry.Key.Start.Column,
                        $"sync: headers: \"{headerName}\" is not a header name"));
                    return new ScriptParseResult(null, problems);
                }
                headers.Add(new ScriptHttpHeader(headerName, AsScalar(entry.Value, problems, headerName) ?? string.Empty));
            }
        }

        // items: is allowed to be empty — a feed answering with the array
        // itself is common, and Watch Duty's does exactly that.
        var items = (ReadString(sync, "items", problems) ?? string.Empty).Trim();
        if (items.Length > 0 && !JsonValuePath.IsValid(items, out var itemsError))
        {
            problems.Add(ScriptProblem.Error(line, column, $"sync: items: {itemsError}"));
            return new ScriptParseResult(null, problems);
        }

        string? RequiredPath(string key, string fallback)
        {
            var path = (ReadString(sync, key, problems) ?? fallback).Trim();
            if (path.Length == 0)
            {
                problems.Add(ScriptProblem.Error(line, column, $"sync: needs {key}:"));
                return null;
            }
            if (!JsonValuePath.IsValid(path, out var error))
            {
                problems.Add(ScriptProblem.Error(line, column, $"sync: {key}: {error}"));
                return null;
            }
            return path;
        }

        var idPath = RequiredPath("id", "id");
        var latPath = RequiredPath("lat", string.Empty);
        var lonPath = RequiredPath("lon", string.Empty);
        if (idPath is null || latPath is null || lonPath is null) return new ScriptParseResult(null, problems);

        var activePath = (ReadString(sync, "active", problems) ?? string.Empty).Trim();
        if (activePath.Length > 0 && !JsonValuePath.IsValid(activePath, out var activeError))
        {
            problems.Add(ScriptProblem.Error(line, column, $"sync: active: {activeError}"));
            return new ScriptParseResult(null, problems);
        }

        double? within = null;
        if (TryGet(sync, "within", out var withinKey, out var withinValue))
        {
            var text = AsScalar(withinValue, problems, "within");
            var metres = text is null ? null : ParseDistanceMetres(text);
            if (metres is null)
            {
                problems.Add(ScriptProblem.Error(withinKey.Start.Line, withinKey.Start.Column,
                    $"sync: within: has to be a distance like 30mi, 50km or 500m, not '{text}'"));
                return new ScriptParseResult(null, problems);
            }
            within = metres.Value;
        }

        var watch = new List<string>();
        bool watchGiven = TryGet(sync, "watch", out var watchKey, out var watchValue);
        if (watchGiven)
        {
            watch = AsStringList(watchValue, problems, "watch",
                (int)watchKey.Start.Line, (int)watchKey.Start.Column);
            foreach (var path in watch)
            {
                if (JsonValuePath.IsValid(path, out var watchError)) continue;
                problems.Add(ScriptProblem.Error(watchKey.Start.Line, watchKey.Start.Column,
                    $"sync: watch: {path}: {watchError}"));
                return new ScriptParseResult(null, problems);
            }
        }
        // Only when the key is absent. An explicit empty list is how a feed of
        // immutable records — a lightning strike never changes — says it meant
        // to have nothing to watch.
        if (!watchGiven)
        {
            problems.Add(ScriptProblem.Warning(line, column,
                "sync: has no watch:, so a marker is only ever placed and retired, never updated. " +
                "List the fields whose changes are worth resending for, or watch: [] if there are none."));
        }

        var waypoint = ParseSyncWaypoint(sync, line, column, problems);
        if (waypoint is null) return new ScriptParseResult(null, problems);
        WarnAboutMovingTargets(waypoint, line, column, problems);

        if (problems.Any(p => p.Severity == ScriptProblemSeverity.Error))
            return new ScriptParseResult(null, problems);

        return new ScriptParseResult(null, problems, new MeshFeedSync
        {
            Enabled = enabled,
            Alias = alias,
            Every = every,
            Request = new ScriptHttpRequest
            {
                Url = url,
                CredentialNames = credentials,
                Headers = headers,
                Timeout = timeout,
                Optional = true,
            },
            ItemsPath = items,
            IdPath = idPath,
            ActivePath = activePath,
            LatitudePath = latPath,
            LongitudePath = lonPath,
            WithinMetres = within,
            WatchPaths = watch,
            // An explicit empty list is a statement that the records never
            // change, which is different from not having said.
            Immutable = watchGiven && watch.Count == 0,
            Waypoint = waypoint,
            Expires = waypoint.Expires,
        });
    }

    /// <summary>
    /// Placeholders that change on their own, independent of the record.
    /// </summary>
    /// <remarks>
    /// One of these in a mirrored marker's text re-renders on every poll, so
    /// every record looks changed and the whole set goes back on the air each
    /// time — the exact thing watch: exists to prevent, arriving by the other
    /// door. Only text derived from the record itself can be stable.
    /// </remarks>
    private static readonly string[] MovingPlaceholders = ["{time}", "{date}", "{node.battery}"];

    private static void WarnAboutMovingTargets(
        ScriptWaypoint waypoint, int line, int column, List<ScriptProblem> problems)
    {
        foreach (var token in MovingPlaceholders)
        {
            if (!waypoint.Name.Contains(token, StringComparison.Ordinal) &&
                !waypoint.Description.Contains(token, StringComparison.Ordinal)) continue;

            problems.Add(ScriptProblem.Warning(line, column,
                $"sync: waypoint: {token} changes on its own, so every record will look different on every " +
                "poll and the whole set will be resent each time. Use a field of the record instead."));
        }
    }

    private static ScriptWaypoint? ParseSyncWaypoint(
        YamlMappingNode sync, int line, int column, List<ScriptProblem> problems)
    {
        if (!TryGet(sync, "waypoint", out var key, out var value))
        {
            problems.Add(ScriptProblem.Error(line, column, "sync: needs a waypoint: block"));
            return null;
        }
        if (value is not YamlMappingNode map)
        {
            problems.Add(ScriptProblem.Error(key.Start.Line, key.Start.Column,
                "sync: waypoint: needs indented name:/icon: entries under it"));
            return null;
        }
        RejectUnknownKeys(map, SyncWaypointKeys, "waypoint option", problems);

        var name = ReadString(map, "name", problems) ?? string.Empty;
        if (name.Trim().Length == 0)
        {
            problems.Add(ScriptProblem.Error(key.Start.Line, key.Start.Column,
                "sync: waypoint: needs a name:, e.g. name: \"Fire: {item.name}\""));
            return null;
        }

        uint radiusM = 0;
        if (TryGet(map, "radius", out var radiusKey, out var radiusValue))
        {
            var text = AsScalar(radiusValue, problems, "radius");
            var metres = text is null ? null : ParseDistanceMetres(text);
            if (metres is null)
            {
                problems.Add(ScriptProblem.Error(radiusKey.Start.Line, radiusKey.Start.Column,
                    $"sync: waypoint: radius: has to be a distance like 10mi, not '{text}'"));
                return null;
            }
            radiusM = (uint)Math.Round(metres.Value);
        }

        // Zero means never, which is what a mirrored marker usually wants: it is
        // retired when the record it stands for goes, not on a clock.
        var expires = TimeSpan.Zero;
        if (TryGet(map, "expires", out var expiresKey, out var expiresValue))
        {
            var parsed = ReadDuration(expiresKey, expiresValue, problems, "expires");
            if (parsed is null) return null;
            expires = parsed.Value;
        }

        bool notifyEnter = ReadBool(map, "notify_on_enter", problems) ?? false;
        bool notifyExit = ReadBool(map, "notify_on_exit", problems) ?? false;
        if ((notifyEnter || notifyExit) && radiusM == 0)
        {
            problems.Add(ScriptProblem.Error(key.Start.Line, key.Start.Column,
                "sync: waypoint: notify_on_enter/notify_on_exit need a radius:"));
            return null;
        }

        // Unlocked by default, unlike a script's waypoint: these are placed
        // unattended and may outlive this node's interest in them, so whoever
        // receives one should be able to clear it.
        bool lockToMe = ReadBool(map, "lock_to_me", problems) ?? false;

        if (!TryReadDestination(map, (int)key.Start.Line, (int)key.Start.Column, "sync: waypoint",
                                allowPlaceholder: false, problems, out var to, out var channel))
            return null;

        return new ScriptWaypoint
        {
            Name = name,
            Description = ReadString(map, "description", problems) ?? string.Empty,
            Icon = (ReadString(map, "icon", problems) ?? string.Empty).Trim(),
            RadiusM = radiusM,
            Expires = expires,
            NotifyOnEnter = notifyEnter,
            NotifyOnExit = notifyExit,
            To = to,
            Channel = channel,
            LockToMe = lockToMe,
        };
    }

    // ----- top-level scalars -------------------------------------------------

    private static ScriptMode ReadMode(YamlMappingNode root, List<ScriptProblem> problems)
    {
        if (!TryGet(root, "mode", out var key, out var value)) return ScriptMode.Single;
        var text = AsScalar(value, problems, "mode");
        if (text is null) return ScriptMode.Single;

        switch (text.Trim().ToLowerInvariant())
        {
            case "single": return ScriptMode.Single;
            case "restart": return ScriptMode.Restart;
            case "queued": return ScriptMode.Queued;
            default:
                problems.Add(ScriptProblem.Error(key.Start.Line, key.Start.Column,
                    $"mode: has to be single, restart or queued, not '{text}'"));
                return ScriptMode.Single;
        }
    }

    private static ScriptLimits ReadLimits(YamlMappingNode root, List<ScriptProblem> problems)
    {
        var defaults = new ScriptLimits();
        if (!TryGet(root, "limits", out var limitsKey, out var value)) return defaults;

        if (value is not YamlMappingNode map)
        {
            problems.Add(ScriptProblem.Error(limitsKey.Start.Line, limitsKey.Start.Column,
                "limits: has to be a set of 'key: value' entries (cooldown, per_node, max_per_hour)"));
            return defaults;
        }

        RejectUnknownKeys(map, LimitKeys, "limits key", problems);

        var cooldown = defaults.Cooldown;
        if (TryGet(map, "cooldown", out var cooldownKey, out var cooldownValue))
        {
            var parsed = ReadDuration(cooldownKey, cooldownValue, problems, "cooldown");
            if (parsed is not null) cooldown = parsed.Value;
        }

        int maxPerHour = defaults.MaxPerHour;
        if (TryGet(map, "max_per_hour", out var maxKey, out var maxValue))
        {
            var text = AsScalar(maxValue, problems, "max_per_hour");
            if (text is not null)
            {
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxPerHour))
                {
                    problems.Add(ScriptProblem.Error(maxKey.Start.Line, maxKey.Start.Column,
                        $"max_per_hour: has to be a whole number, not '{text}'"));
                    maxPerHour = defaults.MaxPerHour;
                }
                else if (maxPerHour <= 0)
                {
                    problems.Add(ScriptProblem.Warning(maxKey.Start.Line, maxKey.Start.Column,
                        "max_per_hour: is 0 or less, which turns this script's hourly ceiling off " +
                        "(the engine's global budget still applies)"));
                }
            }
        }

        return new ScriptLimits
        {
            Cooldown = cooldown,
            PerNode = ReadBool(map, "per_node", problems) ?? defaults.PerNode,
            MaxPerHour = maxPerHour,
        };
    }

    // ----- list sections -----------------------------------------------------

    /// <summary>Reads trigger:/condition:/action:, each of which is a list of
    /// single-kind mappings. A bare mapping is accepted as a list of one, since
    /// a one-trigger script reads better without the dash.</summary>
    private static List<T> ReadItems<T>(
        YamlMappingNode root,
        string section,
        string[] kinds,
        Func<YamlMappingNode, YamlNode, string, string[], List<ScriptProblem>, T?> parse,
        List<ScriptProblem> problems,
        bool required) where T : class
    {
        var results = new List<T>();

        if (!TryGet(root, section, out var sectionKey, out var value))
        {
            if (required)
            {
                problems.Add(ScriptProblem.Error(0, 0,
                    $"the script has no {section}: — every script needs at least one"));
            }
            return results;
        }

        var entries = new List<YamlNode>();
        switch (value)
        {
            case YamlSequenceNode seq:
                entries.AddRange(seq.Children);
                break;
            case YamlMappingNode single:
                entries.Add(single);
                break;
            default:
                problems.Add(ScriptProblem.Error(sectionKey.Start.Line, sectionKey.Start.Column,
                    $"{section}: has to be a list, with each entry starting with '- '"));
                return results;
        }

        if (entries.Count == 0 && required)
        {
            problems.Add(ScriptProblem.Error(sectionKey.Start.Line, sectionKey.Start.Column,
                $"{section}: is empty — every script needs at least one"));
            return results;
        }

        foreach (var entry in entries)
        {
            if (entry is not YamlMappingNode map)
            {
                problems.Add(ScriptProblem.Error(entry.Start.Line, entry.Start.Column,
                    $"each {section} entry has to be a 'kind: value' pair, e.g. '- {kinds[0]}: …'"));
                continue;
            }

            var kind = FindKind(map, kinds, section, problems);
            if (kind is null) continue;

            var parsed = parse(map, map.Children.Keys.First(k => Key(k) == kind), kind, kinds, problems);
            if (parsed is not null) results.Add(parsed);
        }

        return results;
    }

    /// <summary>Finds the one key in an entry that names a kind. Zero or more
    /// than one is an error — that ambiguity is exactly the mistake worth
    /// catching, since silently picking the first would hide a typo.</summary>
    private static string? FindKind(YamlMappingNode map, string[] kinds, string section, List<ScriptProblem> problems)
    {
        var found = map.Children.Keys.Select(Key).Where(k => kinds.Contains(k, StringComparer.Ordinal)).ToList();

        if (found.Count == 1) return found[0];

        if (found.Count > 1)
        {
            problems.Add(ScriptProblem.Error(map.Start.Line, map.Start.Column,
                $"this {section} entry names more than one kind ({string.Join(", ", found)}) — " +
                $"give each its own '- ' entry"));
            return null;
        }

        var first = map.Children.Keys.Select(Key).FirstOrDefault() ?? string.Empty;
        problems.Add(ScriptProblem.Error(map.Start.Line, map.Start.Column,
            $"'{first}' is not a {section} kind{Suggest(first, kinds)}. " +
            $"Valid: {string.Join(", ", kinds)}"));
        return null;
    }

    // ----- triggers ----------------------------------------------------------

    private static ScriptTrigger? ParseTrigger(
        YamlMappingNode map, YamlNode key, string kind, string[] kinds, List<ScriptProblem> problems)
    {
        var value = map.Children[key];
        int line = (int)key.Start.Line, column = (int)key.Start.Column;

        switch (kind)
        {
            case "text":
            {
                RejectUnknownKeys(map, [.. kinds, "ignore_case"], "trigger option", problems);
                var pattern = AsScalar(value, problems, "text") ?? string.Empty;
                bool ignoreCase = ReadBool(map, "ignore_case", problems) ?? true;
                if (!ValidateRegex(pattern, line, column, problems)) return null;
                return new ScriptTrigger
                {
                    Kind = ScriptTriggerKind.Text,
                    Pattern = pattern,
                    IgnoreCase = ignoreCase,
                    Line = line,
                };
            }

            case "command":
            {
                RejectUnknownKeys(map, kinds, "trigger option", problems);
                var name = (AsScalar(value, problems, "command") ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        "command: needs a word, e.g. 'command: ping' to answer !ping"));
                    return null;
                }
                if (name.StartsWith('!'))
                {
                    problems.Add(ScriptProblem.Warning(line, column,
                        $"command: '{name}' — drop the leading '!', it is added for you"));
                    name = name.TrimStart('!');
                }
                if (name.Any(char.IsWhiteSpace))
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        $"command: '{name}' cannot contain spaces — the rest of the message becomes {{args}}"));
                    return null;
                }
                return new ScriptTrigger { Kind = ScriptTriggerKind.Command, Pattern = name, Line = line };
            }

            case "new_node":
                RejectUnknownKeys(map, kinds, "trigger option", problems);
                return new ScriptTrigger { Kind = ScriptTriggerKind.NewNode, Line = line };

            case "reaction":
            {
                RejectUnknownKeys(map, kinds, "trigger option", problems);
                var emoji = (AsScalar(value, problems, "reaction") ?? string.Empty).Trim();
                if (string.Equals(emoji, "any", StringComparison.OrdinalIgnoreCase)) emoji = string.Empty;
                return new ScriptTrigger { Kind = ScriptTriggerKind.Reaction, Pattern = emoji, Line = line };
            }

            case "every":
            {
                RejectUnknownKeys(map, kinds, "trigger option", problems);
                var interval = ReadDuration(key, value, problems, "every");
                if (interval is null) return null;
                if (interval.Value < TimeSpan.FromMinutes(1))
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        "every: has to be at least 1m — anything faster would flood the channel"));
                    return null;
                }
                return new ScriptTrigger { Kind = ScriptTriggerKind.Every, Interval = interval.Value, Line = line };
            }

            case "at":
            {
                RejectUnknownKeys(map, kinds, "trigger option", problems);
                var text = AsScalar(value, problems, "at");
                if (text is null) return null;
                var time = ParseTime(text);
                if (time is null)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        $"at: has to be a 24-hour time like 08:00, not '{text}'"));
                    return null;
                }
                return new ScriptTrigger { Kind = ScriptTriggerKind.At, TimeOfDay = time.Value, Line = line };
            }
        }

        return null;
    }

    // ----- conditions --------------------------------------------------------

    private static ScriptCondition? ParseCondition(
        YamlMappingNode map, YamlNode key, string kind, string[] kinds, List<ScriptProblem> problems)
    {
        RejectUnknownKeys(map, kinds, "condition option", problems);
        var value = map.Children[key];
        int line = (int)key.Start.Line, column = (int)key.Start.Column;

        switch (kind)
        {
            case "scope":
            {
                var text = (AsScalar(value, problems, "scope") ?? string.Empty).Trim().ToLowerInvariant();
                var scope = text switch
                {
                    "any" => ScriptScope.Any,
                    "direct" => ScriptScope.Direct,
                    "channel" => ScriptScope.Channel,
                    "primary" => ScriptScope.Primary,
                    _ => (ScriptScope?)null,
                };
                if (scope is null)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        $"scope: has to be any, direct, channel or primary, not '{text}'"));
                    return null;
                }
                return new ScriptCondition { Kind = ScriptConditionKind.Scope, Scope = scope.Value, Line = line };
            }

            case "channel":
            case "from":
            case "not_from":
            {
                var values = AsStringList(value, problems, kind, line, column);
                if (values.Count == 0)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        $"{kind}: needs at least one value"));
                    return null;
                }
                if (kind is "from" or "not_from")
                {
                    foreach (var v in values)
                    {
                        if (LooksLikeNodeId(v)) continue;
                        problems.Add(ScriptProblem.Error(line, column,
                            $"{kind}: '{v}' is not a node id — use the !a1b2c3d4 form shown in the Nodes table"));
                        return null;
                    }
                }
                var conditionKind = kind switch
                {
                    "channel" => ScriptConditionKind.Channel,
                    "from" => ScriptConditionKind.From,
                    _ => ScriptConditionKind.NotFrom,
                };
                return new ScriptCondition { Kind = conditionKind, Values = values, Line = line };
            }

            case "snr_above":
            case "hops_below":
            {
                var text = AsScalar(value, problems, kind);
                if (text is null) return null;
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        $"{kind}: has to be a number, not '{text}'"));
                    return null;
                }
                if (kind == "hops_below" && (number < 0 || number > 7))
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        $"hops_below: has to be between 0 and 7, not {number}"));
                    return null;
                }
                return new ScriptCondition
                {
                    Kind = kind == "snr_above" ? ScriptConditionKind.SnrAbove : ScriptConditionKind.HopsBelow,
                    Number = number,
                    Line = line,
                };
            }

            case "between":
            {
                var text = (AsScalar(value, problems, "between") ?? string.Empty).Trim();
                var parts = text.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || ParseTime(parts[0]) is not { } start || ParseTime(parts[1]) is not { } end)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        $"between: has to look like \"08:00-22:00\", not '{text}'"));
                    return null;
                }
                if (start == end)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        "between: start and end are the same time, so the window is empty"));
                    return null;
                }
                return new ScriptCondition
                {
                    Kind = ScriptConditionKind.Between, Start = start, End = end, Line = line,
                };
            }

            case "favorite":
            case "has_key":
            {
                var flag = ReadBoolValue(key, value, problems, kind);
                if (flag is null) return null;
                return new ScriptCondition
                {
                    Kind = kind == "favorite" ? ScriptConditionKind.Favorite : ScriptConditionKind.HasKey,
                    Flag = flag.Value,
                    Line = line,
                };
            }
        }

        return null;
    }

    // ----- actions -----------------------------------------------------------

    /// <summary>Parses one action entry, plus the <c>when:</c> gate any of them
    /// may carry.</summary>
    private static ScriptAction? ParseAction(
        YamlMappingNode map, YamlNode key, string kind, string[] kinds, List<ScriptProblem> problems)
    {
        var action = ParseActionKind(map, key, kind, kinds, problems);
        if (action is null) return null;
        if (!TryGet(map, "when", out var whenKey, out var whenValue)) return action;

        int whenLine = (int)whenKey.Start.Line, whenColumn = (int)whenKey.Start.Column;
        if (kind == "require")
        {
            problems.Add(ScriptProblem.Error(whenLine, whenColumn,
                "require: cannot take a when: — it is already a test. " +
                "Put the second test in its own require:, or use when: on the action you meant to gate"));
            return null;
        }

        var gate = ParseRequirement(whenValue, whenLine, whenColumn, "when", problems);
        return gate is null ? null : action with { When = gate };
    }

    private static ScriptAction? ParseActionKind(
        YamlMappingNode map, YamlNode key, string kind, string[] kinds, List<ScriptProblem> problems)
    {
        var value = map.Children[key];
        int line = (int)key.Start.Line, column = (int)key.Start.Column;

        // These carry their options in a nested mapping; every other action
        // takes a bare scalar, so siblings are always a mistake there — except
        // when:, which any action may carry.
        if (kind is not ("send" or "http" or "waypoint" or "require" or "ring"))
            RejectUnknownKeys(map, [.. kinds, "when"], "action option", problems);

        switch (kind)
        {
            case "ring":
                return ParseRing(value, line, column, problems);

            case "reply":
            case "log":
            {
                var text = AsScalar(value, problems, kind);
                if (text is null) return null;
                if (text.Trim().Length == 0)
                {
                    problems.Add(ScriptProblem.Error(line, column, $"{kind}: needs some text"));
                    return null;
                }
                WarnUnknownPlaceholders(text, line, column, problems);
                if (kind == "reply") WarnLongMessage(text, line, column, problems);
                return new ScriptAction
                {
                    Kind = kind == "reply" ? ScriptActionKind.Reply : ScriptActionKind.Log,
                    Text = text,
                    ReplyLink = kind == "reply",
                    Line = line,
                };
            }

            case "send":
            {
                if (value is not YamlMappingNode send)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        "send: needs indented to:/channel:/text: entries under it — " +
                        "for a plain answer to the sender, use reply: instead"));
                    return null;
                }
                RejectUnknownKeys(send, SendKeys, "send option", problems);

                var text = ReadString(send, "text", problems);
                if (text is null || text.Trim().Length == 0)
                {
                    problems.Add(ScriptProblem.Error(line, column, "send: needs a text: entry"));
                    return null;
                }
                var to = ReadString(send, "to", problems) ?? string.Empty;
                var channel = ReadString(send, "channel", problems) ?? string.Empty;
                if (to.Length > 0 && channel.Length > 0)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        "send: has both to: and channel: — a message goes to one node or one channel, not both"));
                    return null;
                }
                // A literal id is checked now; a placeholder like {from.id} can
                // only be checked when it is expanded at fire time.
                if (to.Length > 0 && !to.Contains('{') && !LooksLikeNodeId(to))
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        $"send: to: '{to}' is not a node id — use the !a1b2c3d4 form, or a placeholder like {{from.id}}"));
                    return null;
                }
                WarnUnknownPlaceholders(text, line, column, problems);
                WarnUnknownPlaceholders(to, line, column, problems);
                WarnLongMessage(text, line, column, problems);
                return new ScriptAction
                {
                    Kind = ScriptActionKind.Send,
                    Text = text,
                    To = to,
                    Channel = channel,
                    ReplyLink = ReadBool(send, "reply_link", problems) ?? false,
                    Line = line,
                };
            }

            case "react":
            {
                var emoji = (AsScalar(value, problems, "react") ?? string.Empty).Trim();
                if (emoji.Length == 0)
                {
                    problems.Add(ScriptProblem.Error(line, column, "react: needs an emoji"));
                    return null;
                }
                return new ScriptAction { Kind = ScriptActionKind.React, Text = emoji, Line = line };
            }

            case "position":
            case "nodeinfo":
            case "traceroute":
            {
                var flag = ReadBoolValue(key, value, problems, kind);
                if (flag is null) return null;
                if (!flag.Value)
                {
                    problems.Add(ScriptProblem.Warning(line, column,
                        $"{kind}: false does nothing — delete the line instead"));
                }
                var actionKind = kind switch
                {
                    "position" => ScriptActionKind.Position,
                    "nodeinfo" => ScriptActionKind.NodeInfo,
                    _ => ScriptActionKind.Traceroute,
                };
                return new ScriptAction { Kind = actionKind, Line = line };
            }

            case "http":
                return ParseHttp(value, line, column, problems);

            case "waypoint":
                return ParseWaypoint(value, line, column, problems);

            case "require":
                return ParseRequire(value, line, column, problems);

            case "delay":
            {
                var delay = ReadDuration(key, value, problems, "delay");
                if (delay is null) return null;
                if (delay.Value > TimeSpan.FromHours(1))
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        "delay: cannot be longer than 1h — use an 'every:' trigger for anything slower"));
                    return null;
                }
                return new ScriptAction { Kind = ScriptActionKind.Delay, Delay = delay.Value, Line = line };
            }
        }

        return null;
    }

    /// <summary>
    /// Parses an <c>http:</c> action. Validates as much as can be known without
    /// making the request, so a mistyped URL or JSON path is a red line in the
    /// editor rather than a silent failure at three in the morning.
    /// </summary>
    private static ScriptAction? ParseHttp(
        YamlNode value, int line, int column, List<ScriptProblem> problems)
    {
        if (value is not YamlMappingNode http)
        {
            problems.Add(ScriptProblem.Error(line, column,
                "http: needs indented url:/method:/json: entries under it"));
            return null;
        }
        RejectUnknownKeys(http, HttpKeys, "http option", problems);

        var url = ReadString(http, "url", problems) ?? string.Empty;
        if (url.Trim().Length == 0)
        {
            problems.Add(ScriptProblem.Error(line, column, "http: needs a url:"));
            return null;
        }
        // Checked on the literal text: a placeholder may fill in the host or the
        // path, but letting one supply the scheme would mean a message off the
        // air could choose file:// or something stranger.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(ScriptProblem.Error(line, column,
                "http: url: has to start with https:// or http://"));
            return null;
        }
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(ScriptProblem.Warning(line, column,
                "http: this URL is plain http, so the request and any credential travel unencrypted"));
        }
        WarnUnknownPlaceholders(url, line, column, problems);

        var method = ScriptHttpMethod.Get;
        if (ReadString(http, "method", problems) is { } methodText)
        {
            switch (methodText.Trim().ToUpperInvariant())
            {
                case "GET": method = ScriptHttpMethod.Get; break;
                case "POST": method = ScriptHttpMethod.Post; break;
                case "PUT": method = ScriptHttpMethod.Put; break;
                default:
                    problems.Add(ScriptProblem.Error(line, column,
                        $"http: method: has to be GET, POST or PUT, not '{methodText}'"));
                    return null;
            }
        }

        var body = ReadString(http, "body", problems) ?? string.Empty;
        if (body.Length > 0 && method == ScriptHttpMethod.Get)
        {
            problems.Add(ScriptProblem.Error(line, column,
                "http: body: only applies to POST or PUT — a GET carries its input in the url:"));
            return null;
        }
        WarnUnknownPlaceholders(body, line, column, problems);

        // json: takes either one path (stored under save_as) or a mapping of
        // name -> path, which is how several values from the same response are
        // kept together.
        var extractions = new List<ScriptHttpExtraction>();
        if (TryGet(http, "json", out var jsonKey, out var jsonValue))
        {
            if (jsonValue is YamlMappingNode jsonMap)
            {
                foreach (var entry in jsonMap.Children)
                {
                    var name = Key(entry.Key);
                    if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    {
                        problems.Add(ScriptProblem.Error(entry.Key.Start.Line, entry.Key.Start.Column,
                            $"http: json: \"{name}\" — use letters, digits and underscores, e.g. lat: report[0].loc.lat"));
                        return null;
                    }
                    if (string.Equals(name, "status", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add(ScriptProblem.Error(entry.Key.Start.Line, entry.Key.Start.Column,
                            "http: json: \"status\" is taken — {http.status} always holds the response code"));
                        return null;
                    }
                    var path = (AsScalar(entry.Value, problems, name) ?? string.Empty).Trim();
                    if (!JsonValuePath.IsValid(path, out var mapError))
                    {
                        problems.Add(ScriptProblem.Error(entry.Value.Start.Line, entry.Value.Start.Column,
                            $"http: json: {name}: {mapError}"));
                        return null;
                    }
                    extractions.Add(new ScriptHttpExtraction(name, path));
                }
                if (extractions.Count == 0)
                {
                    problems.Add(ScriptProblem.Error(jsonKey.Start.Line, jsonKey.Start.Column,
                        "http: json: is empty — give it a path, or a set of name: path entries"));
                    return null;
                }
            }
            else
            {
                var jsonPath = (AsScalar(jsonValue, problems, "json") ?? string.Empty).Trim();
                if (jsonPath.Length > 0 && !JsonValuePath.IsValid(jsonPath, out var pathError))
                {
                    problems.Add(ScriptProblem.Error(line, column, $"http: json: {pathError}"));
                    return null;
                }
                if (jsonPath.Length > 0)
                    extractions.Add(new ScriptHttpExtraction(string.Empty, jsonPath));
            }
        }

        var saveAs = (ReadString(http, "save_as", problems) ?? "body").Trim();
        if (saveAs.Length == 0 || !saveAs.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            problems.Add(ScriptProblem.Error(line, column,
                $"http: save_as: \"{saveAs}\" — use letters, digits and underscores, e.g. save_as: temp"));
            return null;
        }
        if (string.Equals(saveAs, "status", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(ScriptProblem.Error(line, column,
                "http: save_as: \"status\" is taken — {http.status} always holds the response code"));
            return null;
        }

        var timeout = TimeSpan.FromSeconds(10);
        if (TryGet(http, "timeout", out var timeoutKey, out var timeoutValue))
        {
            var parsed = ReadDuration(timeoutKey, timeoutValue, problems, "timeout");
            if (parsed is null) return null;
            if (parsed.Value > MaxHttpTimeout)
            {
                problems.Add(ScriptProblem.Error(line, column,
                    $"http: timeout: cannot be longer than {MaxHttpTimeout.TotalSeconds:0}s"));
                return null;
            }
            timeout = parsed.Value;
        }

        // Extra headers, for an API expecting a particular client. Values are
        // templates; names are not, since a placeholder deciding a header name
        // is a way to confuse a request rather than a use.
        var headers = new List<ScriptHttpHeader>();
        if (TryGet(http, "headers", out var headersKey, out var headersValue))
        {
            if (headersValue is not YamlMappingNode headerMap)
            {
                problems.Add(ScriptProblem.Error(headersKey.Start.Line, headersKey.Start.Column,
                    "http: headers: needs indented name: value entries under it"));
                return null;
            }
            foreach (var entry in headerMap.Children)
            {
                var headerName = Key(entry.Key);
                if (headerName.Length == 0 || headerName.Any(c => char.IsWhiteSpace(c) || c == ':'))
                {
                    problems.Add(ScriptProblem.Error(entry.Key.Start.Line, entry.Key.Start.Column,
                        $"http: headers: \"{headerName}\" is not a header name"));
                    return null;
                }
                var headerValue = AsScalar(entry.Value, problems, headerName);
                if (headerValue is null) return null;
                WarnUnknownPlaceholders(headerValue, line, column, problems);
                headers.Add(new ScriptHttpHeader(headerName, headerValue));
            }
        }

        // One name or a list: an id/secret pair is two separate credentials,
        // each knowing where it attaches.
        var credentialNames = new List<string>();
        if (TryGet(http, "credential", out var credentialKey, out var credentialValue))
        {
            credentialNames = AsStringList(credentialValue, problems, "credential",
                (int)credentialKey.Start.Line, (int)credentialKey.Start.Column);
            if (credentialNames.Count == 0)
            {
                problems.Add(ScriptProblem.Error(credentialKey.Start.Line, credentialKey.Start.Column,
                    "http: credential: needs a name, or a list of names"));
                return null;
            }
        }

        // The single-path form names itself from save_as, which the mapping
        // form has no use for.
        for (int i = 0; i < extractions.Count; i++)
        {
            if (extractions[i].SaveAs.Length == 0)
                extractions[i] = extractions[i] with { SaveAs = saveAs };
        }

        return new ScriptAction
        {
            Kind = ScriptActionKind.Http,
            Line = line,
            Http = new ScriptHttpRequest
            {
                Url = url,
                Method = method,
                CredentialNames = credentialNames,
                Extractions = extractions,
                SaveAs = saveAs,
                Timeout = timeout,
                Body = body,
                ContentType = (ReadString(http, "content_type", problems) ?? "application/json").Trim(),
                Optional = ReadBool(http, "optional", problems) ?? false,
                Headers = headers,
            },
        };
    }

    /// <summary>Parses a <c>waypoint:</c> action.</summary>
    /// <summary>
    /// Parses a <c>ring:</c> action in either shape: a bare scalar naming the
    /// tune, or a mapping adding volume. Both readings are natural, and the
    /// scalar form keeps the common "just make a noise" case to one line.
    /// </summary>
    private static ScriptAction? ParseRing(
        YamlNode value, int line, int column, List<ScriptProblem> problems)
    {
        string tune = string.Empty;
        int? volume = null;

        if (value is YamlScalarNode)
        {
            var text = AsScalar(value, problems, "ring");
            if (text is null) return null;
            tune = NormalizeRingTune(text);
        }
        else if (value is YamlMappingNode map)
        {
            RejectUnknownKeys(map, RingKeys, "ring option", problems);

            if (ReadString(map, "tune", problems) is { } t)
                tune = NormalizeRingTune(t);

            if (ReadString(map, "volume", problems) is { } v)
            {
                if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct)
                    || pct < 0 || pct > 100)
                {
                    problems.Add(ScriptProblem.Error(line, column,
                        "ring: volume must be a whole number from 0 to 100"));
                    return null;
                }
                volume = pct;
            }
        }
        else
        {
            problems.Add(ScriptProblem.Error(line, column,
                "ring: takes a tune, 'default', or indented tune:/volume: entries"));
            return null;
        }

        return new ScriptAction
        {
            Kind = ScriptActionKind.Ring,
            Ringtone = new ScriptRingtone { Tune = tune, VolumePercent = volume },
            Line = line,
        };
    }

    /// <summary>
    /// Empty for the app's configured ringtone, otherwise the RTTTL as written.
    /// "default" is accepted as a word so a script can ask for the configured
    /// tune out loud rather than by omission — and so `ring: default` reads as
    /// an instruction rather than an oversight.
    /// </summary>
    private static string NormalizeRingTune(string text)
    {
        var trimmed = text.Trim();
        return string.Equals(trimmed, "default", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    private static ScriptAction? ParseWaypoint(
        YamlNode value, int line, int column, List<ScriptProblem> problems)
    {
        if (value is not YamlMappingNode map)
        {
            problems.Add(ScriptProblem.Error(line, column,
                "waypoint: needs indented lat:/lon:/name: entries under it"));
            return null;
        }
        RejectUnknownKeys(map, WaypointKeys, "waypoint option", problems);

        var lat = (ReadString(map, "lat", problems) ?? string.Empty).Trim();
        var lon = (ReadString(map, "lon", problems) ?? string.Empty).Trim();

        // "home" is the common case for a script marking local conditions, and
        // saves repeating coordinates the app already knows.
        bool useHome = string.Equals(lat, "home", StringComparison.OrdinalIgnoreCase);
        if (!useHome)
        {
            if (lat.Length == 0 || lon.Length == 0)
            {
                problems.Add(ScriptProblem.Error(line, column,
                    "waypoint: needs both lat: and lon:, or lat: home to use this node's home location"));
                return null;
            }
            // A literal can be checked now; a placeholder only resolves when the
            // script runs.
            if (!lat.Contains('{') && !IsCoordinate(lat, 90))
            {
                problems.Add(ScriptProblem.Error(line, column,
                    $"waypoint: lat: \"{lat}\" is not a latitude between -90 and 90"));
                return null;
            }
            if (!lon.Contains('{') && !IsCoordinate(lon, 180))
            {
                problems.Add(ScriptProblem.Error(line, column,
                    $"waypoint: lon: \"{lon}\" is not a longitude between -180 and 180"));
                return null;
            }
        }

        var name = ReadString(map, "name", problems) ?? string.Empty;
        var description = ReadString(map, "description", problems) ?? string.Empty;
        WarnUnknownPlaceholders(name, line, column, problems);
        WarnUnknownPlaceholders(description, line, column, problems);

        uint radiusM = 0;
        if (TryGet(map, "radius", out var radiusKey, out var radiusValue))
        {
            var text = AsScalar(radiusValue, problems, "radius");
            if (text is null) return null;
            var metres = ParseDistanceMetres(text);
            if (metres is null)
            {
                problems.Add(ScriptProblem.Error(radiusKey.Start.Line, radiusKey.Start.Column,
                    $"waypoint: radius: has to be a distance like 30mi, 50km or 500m, not '{text}'"));
                return null;
            }
            radiusM = (uint)Math.Round(metres.Value);
        }

        var expires = TimeSpan.Zero;
        if (TryGet(map, "expires", out var expiresKey, out var expiresValue))
        {
            var parsed = ReadDuration(expiresKey, expiresValue, problems, "expires");
            if (parsed is null) return null;
            expires = parsed.Value;
        }
        else
        {
            problems.Add(ScriptProblem.Warning(line, column,
                "waypoint: has no expires:, so this marker stays on everyone's map until it is cleared by hand. " +
                "Set lock_to_me: false if you want them to be able to."));
        }

        bool notifyEnter = ReadBool(map, "notify_on_enter", problems) ?? false;
        bool notifyExit = ReadBool(map, "notify_on_exit", problems) ?? false;
        if ((notifyEnter || notifyExit) && radiusM == 0)
        {
            problems.Add(ScriptProblem.Error(line, column,
                "waypoint: notify_on_enter/notify_on_exit need a radius: — there is no fence to cross without one"));
            return null;
        }

        if (!TryReadDestination(map, line, column, "waypoint", allowPlaceholder: true, problems,
                                out var to, out var channel))
            return null;

        return new ScriptAction
        {
            Kind = ScriptActionKind.Waypoint,
            Line = line,
            Waypoint = new ScriptWaypoint
            {
                Latitude = lat,
                Longitude = lon,
                UseHome = useHome,
                Name = name,
                Description = description,
                Icon = (ReadString(map, "icon", problems) ?? string.Empty).Trim(),
                RadiusM = radiusM,
                Expires = expires,
                NotifyOnEnter = notifyEnter,
                NotifyOnExit = notifyExit,
                To = to,
                Channel = channel,
                LockToMe = ReadBool(map, "lock_to_me", problems) ?? true,
            },
        };
    }

    /// <summary>
    /// Reads the <c>to:</c>/<c>channel:</c> pair off a waypoint mapping.
    /// </summary>
    /// <remarks>
    /// A marker goes to one node or out on one channel, never both — the same
    /// rule <c>send:</c> follows, and for the same reason: a frame carries one
    /// destination and naming two says nothing about which was meant.
    /// </remarks>
    /// <param name="what">Which block is being parsed, so a message names the
    /// key the user actually wrote.</param>
    /// <param name="allowPlaceholder">Whether <c>to:</c> may hold a
    /// placeholder. A script's waypoint resolves one when it fires; a feed's
    /// markers are placed unprompted, so there is no message to read one from.
    /// </param>
    private static bool TryReadDestination(
        YamlMappingNode map, int line, int column, string what, bool allowPlaceholder,
        List<ScriptProblem> problems, out string to, out string channel)
    {
        to = (ReadString(map, "to", problems) ?? string.Empty).Trim();
        channel = (ReadString(map, "channel", problems) ?? string.Empty).Trim();

        if (to.Length > 0 && channel.Length > 0)
        {
            problems.Add(ScriptProblem.Error(line, column,
                $"{what}: has both to: and channel: — a marker goes to one node or out on one channel, not both"));
            return false;
        }
        if (to.Length == 0) return true;

        if (to.Contains('{'))
        {
            if (!allowPlaceholder)
            {
                problems.Add(ScriptProblem.Error(line, column,
                    $"{what}: to: has to be a literal node id like !a1b2c3d4 — a feed places its markers " +
                    "unprompted, so there is no message for a placeholder to come from"));
                return false;
            }
            WarnUnknownPlaceholders(to, line, column, problems);
            return true;
        }
        if (!LooksLikeNodeId(to))
        {
            problems.Add(ScriptProblem.Error(line, column,
                $"{what}: to: '{to}' is not a node id — use the !a1b2c3d4 form" +
                (allowPlaceholder ? ", or a placeholder like {from.id}" : string.Empty)));
            return false;
        }
        return true;
    }

    /// <summary>Parses a <c>require:</c> action.</summary>
    private static ScriptAction? ParseRequire(
        YamlNode value, int line, int column, List<ScriptProblem> problems)
    {
        var requirement = ParseRequirement(value, line, column, "require", problems);
        return requirement is null
            ? null
            : new ScriptAction { Kind = ScriptActionKind.Require, Line = line, Require = requirement };
    }

    /// <summary>
    /// Parses the shared test body behind <c>require:</c> and an action's
    /// <c>when:</c> — one value, one comparison.
    /// </summary>
    /// <param name="what">Which key is being parsed, so the messages name the
    /// one the user actually wrote.</param>
    private static ScriptRequirement? ParseRequirement(
        YamlNode value, int line, int column, string what, List<ScriptProblem> problems)
    {
        if (value is not YamlMappingNode map)
        {
            problems.Add(ScriptProblem.Error(line, column,
                $"{what}: needs an indented value: and one comparison, e.g. above: 30"));
            return null;
        }
        RejectUnknownKeys(map, RequireKeys, $"{what} option", problems);

        var tested = ReadString(map, "value", problems) ?? string.Empty;
        if (tested.Trim().Length == 0)
        {
            problems.Add(ScriptProblem.Error(line, column,
                $"{what}: needs a value: to test, e.g. value: \"{{http.code}}\""));
            return null;
        }
        WarnUnknownPlaceholders(tested, line, column, problems);

        var used = map.Children.Keys.Select(Key)
            .Where(k => RequireComparisons.Contains(k, StringComparer.Ordinal)).ToList();
        if (used.Count == 0)
        {
            problems.Add(ScriptProblem.Error(line, column,
                $"{what}: needs one comparison. Valid: {string.Join(", ", RequireComparisons)}"));
            return null;
        }
        if (used.Count > 1)
        {
            problems.Add(ScriptProblem.Error(line, column,
                $"{what}: names more than one comparison ({string.Join(", ", used)}) — give each its own entry"));
            return null;
        }

        var name = used[0];
        TryGet(map, name, out var comparisonKey, out var comparisonValue);
        bool ignoreCase = ReadBool(map, "ignore_case", problems) ?? true;

        var comparison = name switch
        {
            "equals" => ScriptComparison.Equals,
            "not_equals" => ScriptComparison.NotEquals,
            "above" => ScriptComparison.Above,
            "below" => ScriptComparison.Below,
            "at_least" => ScriptComparison.AtLeast,
            "at_most" => ScriptComparison.AtMost,
            "between" => ScriptComparison.Between,
            "contains" => ScriptComparison.Contains,
            "matches" => ScriptComparison.Matches,
            "is_empty" => ScriptComparison.IsEmpty,
            "within" => ScriptComparison.Within,
            _ => ScriptComparison.NotEmpty,
        };

        string operand = string.Empty, operand2 = string.Empty;
        double rangeMetres = 0;
        Regex? pattern = null;

        switch (comparison)
        {
            case ScriptComparison.IsEmpty:
            case ScriptComparison.NotEmpty:
                // The value carries the whole meaning; "is_empty: true" reads
                // naturally but there is nothing to compare against.
                break;

            case ScriptComparison.Between:
            {
                var bounds = AsStringList(comparisonValue, problems, "between",
                    (int)comparisonKey.Start.Line, (int)comparisonKey.Start.Column);
                if (bounds.Count != 2)
                {
                    problems.Add(ScriptProblem.Error(comparisonKey.Start.Line, comparisonKey.Start.Column,
                        $"{what}: between: needs exactly two bounds, e.g. between: [200, 232]"));
                    return null;
                }
                operand = bounds[0];
                operand2 = bounds[1];
                break;
            }

            case ScriptComparison.Within:
            {
                var text = AsScalar(comparisonValue, problems, "within");
                if (text is null) return null;
                var metres = ParseDistanceMetres(text);
                if (metres is null)
                {
                    problems.Add(ScriptProblem.Error(comparisonKey.Start.Line, comparisonKey.Start.Column,
                        $"{what}: within: has to be a distance like 30mi, 50km or 500m, not '{text}'"));
                    return null;
                }
                rangeMetres = metres.Value;
                break;
            }

            case ScriptComparison.Matches:
            {
                operand = AsScalar(comparisonValue, problems, "matches") ?? string.Empty;
                try
                {
                    pattern = new Regex(operand,
                        RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                        RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    problems.Add(ScriptProblem.Error(comparisonKey.Start.Line, comparisonKey.Start.Column,
                        $"{what}: matches: is not a valid pattern — {ex.Message.TrimEnd('.')}"));
                    return null;
                }
                break;
            }

            default:
                operand = AsScalar(comparisonValue, problems, name) ?? string.Empty;
                if (operand.Trim().Length == 0)
                {
                    problems.Add(ScriptProblem.Error(comparisonKey.Start.Line, comparisonKey.Start.Column,
                        $"{what}: {name}: needs something to compare against"));
                    return null;
                }
                break;
        }

        return new ScriptRequirement
        {
            Value = tested,
            Comparison = comparison,
            Operand = operand,
            Operand2 = operand2,
            IgnoreCase = ignoreCase,
            Pattern = pattern,
            RangeMetres = rangeMetres,
        };
    }

    private static bool IsCoordinate(string text, double limit) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && Math.Abs(value) <= limit;

    private static readonly Regex s_distance = new(
        @"^(?<n>\d+(?:\.\d+)?)\s*(?<u>m|meter|meters|metre|metres|km|kilometer|kilometers|kilometre|kilometres|mi|mile|miles|nmi)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Parses <c>30mi</c>, <c>50km</c>, <c>500m</c> or a bare number
    /// meaning metres, returning metres. Public so the help window and the
    /// tests describe exactly what is accepted.</summary>
    public static double? ParseDistanceMetres(string text)
    {
        var match = s_distance.Match(text.Trim());
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return null;

        return match.Groups["u"].Value.ToLowerInvariant() switch
        {
            "" or "m" or "meter" or "meters" or "metre" or "metres" => n,
            "km" or "kilometer" or "kilometers" or "kilometre" or "kilometres" => n * 1000.0,
            "nmi" => n * 1852.0,
            _ => n * 1609.344,
        };
    }

    /// <summary>Meshtastic's text payload caps at 200-odd bytes after framing;
    /// a longer body is silently truncated by the radio, so it is worth saying
    /// so before it goes out rather than after.</summary>
    private static void WarnLongMessage(string text, int line, int column, List<ScriptProblem> problems)
    {
        // Measured with the placeholders taken out. What they expand to is not
        // knowable here, and counting "{http.humidity}" as fifteen bytes of
        // message would flag a report that actually sends eighty — a warning
        // that fires on correct scripts is one people learn to ignore. The
        // literal text alone being over the limit is a real mistake, since not
        // even an empty expansion could fit it.
        int bytes = System.Text.Encoding.UTF8.GetByteCount(ScriptTemplate.Token.Replace(text, string.Empty));
        if (bytes <= 200) return;
        problems.Add(ScriptProblem.Warning(line, column,
            $"the wording around the placeholders is {bytes} bytes on its own, and the radio truncates around 200"));
    }

    private static void WarnUnknownPlaceholders(string text, int line, int column, List<ScriptProblem> problems)
    {
        foreach (var token in ScriptPlaceholders.UnknownTokens(text).Distinct(StringComparer.Ordinal))
        {
            problems.Add(ScriptProblem.Warning(line, column,
                $"{{{token}}} is not a placeholder{Suggest(token, ScriptPlaceholders.All.Select(p => p.Token))} " +
                "— it will be sent as literal text"));
        }

        foreach (var filter in ScriptPlaceholders.UnknownFilters(text).Distinct(StringComparer.Ordinal))
        {
            problems.Add(ScriptProblem.Warning(line, column,
                $"'{filter}' is not a filter{Suggest(filter, ScriptFilters.Names)} " +
                "— the placeholder will be sent as literal text"));
        }
    }

    // ----- node helpers ------------------------------------------------------

    private static string Key(YamlNode node) =>
        node is YamlScalarNode { Value: { } v } ? v : string.Empty;

    private static bool TryGet(YamlMappingNode map, string key, out YamlNode keyNode, out YamlNode value)
    {
        foreach (var child in map.Children)
        {
            if (Key(child.Key) != key) continue;
            keyNode = child.Key;
            value = child.Value;
            return true;
        }
        keyNode = null!;
        value = null!;
        return false;
    }

    private static void RejectUnknownKeys(
        YamlMappingNode map, IReadOnlyCollection<string> known, string what, List<ScriptProblem> problems)
    {
        foreach (var child in map.Children)
        {
            var name = Key(child.Key);
            if (name.Length == 0 || known.Contains(name, StringComparer.Ordinal)) continue;
            problems.Add(ScriptProblem.Error(child.Key.Start.Line, child.Key.Start.Column,
                $"'{name}' is not a {what}{Suggest(name, known)}"));
        }
    }

    private static string? AsScalar(YamlNode node, List<ScriptProblem> problems, string what)
    {
        if (node is YamlScalarNode { Value: { } v }) return v;
        problems.Add(ScriptProblem.Error(node.Start.Line, node.Start.Column,
            $"{what}: needs a single value on the same line"));
        return null;
    }

    /// <summary>Accepts either one value or a YAML list, so both
    /// <c>channel: LongFast</c> and <c>channel: [A, B]</c> work.</summary>
    private static List<string> AsStringList(
        YamlNode node, List<ScriptProblem> problems, string what, int line, int column)
    {
        var results = new List<string>();
        switch (node)
        {
            case YamlScalarNode { Value: { } single } when single.Trim().Length > 0:
                results.Add(single.Trim());
                break;
            case YamlSequenceNode seq:
                foreach (var item in seq.Children)
                {
                    if (item is YamlScalarNode { Value: { } v } && v.Trim().Length > 0)
                    {
                        results.Add(v.Trim());
                        continue;
                    }
                    problems.Add(ScriptProblem.Error(item.Start.Line, item.Start.Column,
                        $"{what}: entries have to be plain values"));
                }
                break;
            default:
                problems.Add(ScriptProblem.Error(line, column,
                    $"{what}: has to be one value or a list like [A, B]"));
                break;
        }
        return results;
    }

    private static string? ReadString(YamlMappingNode map, string key, List<ScriptProblem> problems) =>
        TryGet(map, key, out _, out var value) ? AsScalar(value, problems, key) : null;

    private static bool? ReadBool(YamlMappingNode map, string key, List<ScriptProblem> problems) =>
        TryGet(map, key, out var keyNode, out var value) ? ReadBoolValue(keyNode, value, problems, key) : null;

    private static bool? ReadBoolValue(YamlNode keyNode, YamlNode value, List<ScriptProblem> problems, string what)
    {
        var text = AsScalar(value, problems, what);
        if (text is null) return null;
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "on": return true;
            case "false" or "no" or "off": return false;
            default:
                problems.Add(ScriptProblem.Error(keyNode.Start.Line, keyNode.Start.Column,
                    $"{what}: has to be true or false, not '{text}'"));
                return null;
        }
    }

    private static TimeSpan? ReadDuration(
        YamlNode keyNode, YamlNode value, List<ScriptProblem> problems, string what)
    {
        var text = AsScalar(value, problems, what);
        if (text is null) return null;
        var parsed = ParseDuration(text);
        if (parsed is null)
        {
            problems.Add(ScriptProblem.Error(keyNode.Start.Line, keyNode.Start.Column,
                $"{what}: has to be a duration like 30s, 5m or 4h, not '{text}'"));
            return null;
        }
        if (parsed.Value <= TimeSpan.Zero)
        {
            problems.Add(ScriptProblem.Error(keyNode.Start.Line, keyNode.Start.Column,
                $"{what}: has to be longer than zero"));
            return null;
        }
        return parsed;
    }

    // ----- scalar formats ----------------------------------------------------

    private static readonly Regex s_duration = new(
        @"^(?<n>\d+(?:\.\d+)?)\s*(?<u>s|sec|secs|second|seconds|m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Parses <c>30s</c>, <c>5m</c>, <c>4h</c>, <c>1d</c>, or a bare
    /// number meaning seconds. Public so the help window and the tests describe
    /// exactly what the parser accepts.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        var match = s_duration.Match(text.Trim());
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return null;

        return match.Groups["u"].Value.ToLowerInvariant() switch
        {
            "" or "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(n),
            "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(n),
            "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(n),
            _ => TimeSpan.FromDays(n),
        };
    }

    /// <summary>Parses a 24-hour <c>HH:mm</c> time. YAML would happily read
    /// <c>08:00</c> as a sexagesimal number in some dialects, so times are
    /// always taken from the raw scalar text rather than a typed node.</summary>
    public static TimeOnly? ParseTime(string text) =>
        TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;

    private static bool LooksLikeNodeId(string text)
    {
        var id = text.Trim();
        if (id.StartsWith('!')) id = id[1..];
        return id.Length == 8 && id.All(Uri.IsHexDigit);
    }

    private static bool ValidateRegex(string pattern, int line, int column, List<ScriptProblem> problems)
    {
        if (pattern.Trim().Length == 0)
        {
            problems.Add(ScriptProblem.Error(line, column,
                "text: needs a pattern — use '.' to match any message"));
            return false;
        }
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            return true;
        }
        catch (ArgumentException ex)
        {
            problems.Add(ScriptProblem.Error(line, column,
                $"text: is not a valid pattern — {ex.Message.TrimEnd('.')}"));
            return false;
        }
    }

    // ----- diagnostics -------------------------------------------------------

    /// <summary>Rewrites YamlDotNet's parser messages into something a person
    /// editing a config file can act on. The library's own wording talks about
    /// tokens and block mappings, which does not help someone who has simply
    /// mixed tabs into their indentation.</summary>
    private static string DescribeYamlError(YamlException ex)
    {
        var raw = ex.Message;

        if (raw.Contains("tab", StringComparison.OrdinalIgnoreCase))
            return "YAML does not allow tabs for indentation — use spaces (the editor's Tab key inserts two)";

        if (raw.Contains("could not find expected ':'", StringComparison.OrdinalIgnoreCase))
            return "a 'key: value' pair is missing its colon, or a value containing ':' needs quotes around it";

        if (raw.Contains("mapping values are not allowed", StringComparison.OrdinalIgnoreCase))
            return "there is an extra ':' here — if the text itself contains a colon, wrap the whole value in quotes";

        if (raw.Contains("while scanning a quoted scalar", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("found unexpected end of stream", StringComparison.OrdinalIgnoreCase))
            return "a quoted value is never closed — check for a missing \" or '";

        if (raw.Contains("did not find expected key", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("expected <block end>", StringComparison.OrdinalIgnoreCase))
            return "the indentation does not line up — every entry in a list needs the same indent as its siblings";

        if (raw.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            return "this key appears twice in the same block — the second one would silently win";

        // Strip the library's "(Line: 3, Col: 5, Idx: 42) - " prefix; the
        // problem carries the position separately.
        int dash = raw.IndexOf(") - ", StringComparison.Ordinal);
        return dash >= 0 ? raw[(dash + 4)..] : raw;
    }

    /// <summary>" — did you mean 'trigger'?" for a near-miss key. Only fires on
    /// a close match, so an entirely wrong word doesn't get a misleading
    /// suggestion pinned to it.</summary>
    private static string Suggest(string unknown, IEnumerable<string> known)
    {
        if (unknown.Length == 0) return string.Empty;

        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var candidate in known)
        {
            int distance = Distance(unknown.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = candidate;
        }

        // A third of the word may differ before the suggestion stops being
        // plausible; below that, offering one is noise.
        int tolerance = Math.Max(1, unknown.Length / 3);
        return best is not null && bestDistance <= tolerance ? $" — did you mean '{best}'?" : string.Empty;
    }

    /// <summary>
    /// Optimal string alignment distance: Levenshtein, except that swapping two
    /// adjacent characters costs 1 rather than 2. Plain Levenshtein scores
    /// "relpy" two edits away from "reply" and so declines to suggest it, which
    /// misses the most common typo there is.
    /// </summary>
    private static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }
}
