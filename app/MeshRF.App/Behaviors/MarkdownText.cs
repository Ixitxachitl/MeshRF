// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Text.RegularExpressions;

namespace MeshRF.App.Behaviors;

/// <summary>
/// Attached property for <see cref="TextBlock"/> and <see cref="RichTextBox"/>
/// that renders a small subset of
/// inline Markdown: <c>**bold**</c> (and <c>__bold__</c>) becomes bold, and
/// <c>*italic*</c> (and <c>_italic_</c>) becomes italic. Unmatched markers are
/// left as literal text. Bare URLs are emitted as hyperlinks.
/// </summary>
public static class MarkdownText
{
    private static readonly Regex UrlRegex = new(
        @"(?:(?:https?|ftp)://|www\.)[^\s<>""]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(MarkdownText),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = e.NewValue as string ?? string.Empty;

        if (d is TextBlock tb)
        {
            tb.Inlines.Clear();
            foreach (var inline in Parse(text))
                tb.Inlines.Add(inline);
            return;
        }

        if (d is not RichTextBox rtb) return;

        rtb.Document.Blocks.Clear();
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0),
        };

        foreach (var inline in Parse(text))
            paragraph.Inlines.Add(inline);

        rtb.Document.Blocks.Add(paragraph);
    }

    // Walk the text and emit Runs, toggling bold/italic on the emphasis markers.
    // ** / __ = bold, * / _ = italic. A marker only counts when its matching
    // closing marker is present later in the string; otherwise it is literal.
    private static IEnumerable<Inline> Parse(string text)
    {
        var result = new List<Inline>();
        var buffer = new System.Text.StringBuilder();
        bool bold = false, italic = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            AppendTextWithUrls(result, buffer.ToString(), bold, italic);
            buffer.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '*' || c == '_')
            {
                bool doubled = i + 1 < text.Length && text[i + 1] == c;
                char marker = c;

                if (doubled)
                {
                    // Toggle bold only if a closing "**"/"__" exists ahead.
                    if (bold || HasClosing(text, i + 2, marker, doubled: true))
                    {
                        Flush();
                        bold = !bold;
                        i++; // consume the second marker char
                        continue;
                    }
                }
                else
                {
                    // Toggle italic only if a closing single marker exists ahead.
                    if (italic || HasClosing(text, i + 1, marker, doubled: false))
                    {
                        Flush();
                        italic = !italic;
                        continue;
                    }
                }
            }
            buffer.Append(c);
        }

        Flush();
        return result;
    }

    // True if a matching emphasis marker appears later in the string.
    private static bool HasClosing(string text, int start, char marker, bool doubled)
    {
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] != marker) continue;
            bool isDouble = i + 1 < text.Length && text[i + 1] == marker;
            if (doubled && isDouble) return true;
            if (!doubled && !isDouble) return true;
            if (doubled && isDouble) i++;
        }
        return false;
    }

    private static void AppendTextWithUrls(ICollection<Inline> output, string text, bool bold, bool italic)
    {
        int cursor = 0;
        foreach (Match match in UrlRegex.Matches(text))
        {
            if (!match.Success || match.Length == 0) continue;

            var (start, end) = TrimUrlBounds(text, match.Index, match.Index + match.Length);
            if (start < cursor || end <= start) continue;

            if (start > cursor)
                output.Add(CreateRun(text[cursor..start], bold, italic));

            string linkText = text[start..end];
            if (Uri.TryCreate(NormalizeUrl(linkText), UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink(CreateRun(linkText, bold, italic)) { NavigateUri = uri };
                output.Add(link);
            }
            else
            {
                output.Add(CreateRun(linkText, bold, italic));
            }

            cursor = end;
        }

        if (cursor < text.Length)
            output.Add(CreateRun(text[cursor..], bold, italic));
    }

    private static Run CreateRun(string text, bool bold, bool italic)
    {
        var run = new Run(text);
        if (bold) run.FontWeight = FontWeights.Bold;
        if (italic) run.FontStyle = FontStyles.Italic;
        return run;
    }

    private static string NormalizeUrl(string raw)
    {
        if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return "https://" + raw;
        return raw;
    }

    private static (int Start, int End) TrimUrlBounds(string source, int start, int end)
    {
        while (end > start)
        {
            char tail = source[end - 1];
            if (tail is '.' or ',' or ';' or '!' or '?' or ':' or ']' or '}')
            {
                end--;
                continue;
            }

            if (tail == ')')
            {
                int openCount = 0;
                int closeCount = 0;
                for (int i = start; i < end; i++)
                {
                    if (source[i] == '(') openCount++;
                    else if (source[i] == ')') closeCount++;
                }

                if (closeCount > openCount)
                {
                    end--;
                    continue;
                }
            }

            break;
        }

        return (start, end);
    }
}
