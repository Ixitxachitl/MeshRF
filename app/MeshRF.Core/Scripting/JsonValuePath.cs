// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace MeshRF.Scripting;

/// <summary>
/// Pulls a single value out of a JSON response by dotted path, e.g.
/// <c>current.temp_c</c> or <c>results[0].name</c>.
/// </summary>
/// <remarks>
/// A deliberately small subset of JSONPath: object members, array indices, and
/// nothing else — no wildcards, filters or recursion. Scripts want one number
/// or one string to put in a sentence, and the full query language would be a
/// dependency and an error surface out of all proportion to that.
/// </remarks>
public static class JsonValuePath
{
    /// <summary>Checks a path is well-formed, so the editor can reject a
    /// mistyped one rather than leaving it to fail at fire time.</summary>
    public static bool IsValid(string path, out string error)
    {
        error = string.Empty;
        if (path.Trim().Length == 0)
        {
            error = "the path is empty";
            return false;
        }

        foreach (var segment in path.Split('.'))
        {
            if (segment.Length == 0)
            {
                error = "there is an empty step (two dots in a row, or a leading/trailing dot)";
                return false;
            }
            if (!TrySplitIndices(segment, out _, out _, out var why))
            {
                error = why;
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Reads the value at <paramref name="path"/>, formatted as text. Returns
    /// null when the response is not JSON or the path does not exist, which the
    /// caller reports rather than broadcasting an empty message.
    /// </summary>
    public static string? Read(string json, string path, out string error)
    {
        error = string.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"the response is not valid JSON — {ex.Message}";
            return null;
        }

        using (document)
        {
            var current = document.RootElement;

            foreach (var segment in path.Split('.'))
            {
                if (!TrySplitIndices(segment, out var name, out var indices, out error)) return null;

                if (name.Length > 0)
                {
                    if (current.ValueKind != JsonValueKind.Object)
                    {
                        error = $"\"{name}\" was asked for, but that part of the response is not an object";
                        return null;
                    }
                    if (!current.TryGetProperty(name, out current))
                    {
                        error = $"the response has no \"{name}\"";
                        return null;
                    }
                }

                foreach (var index in indices)
                {
                    if (current.ValueKind != JsonValueKind.Array)
                    {
                        error = $"[{index}] was asked for, but that part of the response is not a list";
                        return null;
                    }
                    if (index >= current.GetArrayLength())
                    {
                        error = $"the list has {current.GetArrayLength()} item(s), so [{index}] does not exist";
                        return null;
                    }
                    current = current[index];
                }
            }

            return Format(current);
        }
    }

    /// <summary>Renders a leaf as the text a message would carry. Objects and
    /// arrays come back as compact JSON, which is rarely what a script wants
    /// but is better than an empty string.</summary>
    private static string Format(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => element.GetRawText(),
    };

    /// <summary>Splits <c>results[0][1]</c> into the member name and its
    /// trailing indices.</summary>
    private static bool TrySplitIndices(string segment, out string name, out List<int> indices, out string error)
    {
        indices = [];
        error = string.Empty;

        int bracket = segment.IndexOf('[');
        if (bracket < 0)
        {
            name = segment;
            return true;
        }

        name = segment[..bracket];
        var rest = segment[bracket..];

        while (rest.Length > 0)
        {
            if (rest[0] != '[')
            {
                error = $"\"{segment}\" has stray text after an index";
                name = string.Empty;
                return false;
            }
            int close = rest.IndexOf(']');
            if (close < 0)
            {
                error = $"\"{segment}\" is missing a closing ]";
                name = string.Empty;
                return false;
            }
            var inner = rest[1..close];
            if (!int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                error = $"\"{inner}\" is not a list position — use a number from 0";
                name = string.Empty;
                return false;
            }
            indices.Add(index);
            rest = rest[(close + 1)..];
        }
        return true;
    }
}
