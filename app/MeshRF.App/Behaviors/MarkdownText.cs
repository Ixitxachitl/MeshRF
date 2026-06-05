// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MeshRF.App.Behaviors;

/// <summary>
/// Attached property for <see cref="TextBlock"/> that renders a small subset of
/// inline Markdown: <c>**bold**</c> (and <c>__bold__</c>) becomes bold, and
/// <c>*italic*</c> (and <c>_italic_</c>) becomes italic. Unmatched markers are
/// left as literal text. Used so chat messages show emphasis instead of the raw
/// asterisks.
/// </summary>
public static class MarkdownText
{
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
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        foreach (var inline in Parse(e.NewValue as string ?? string.Empty))
            tb.Inlines.Add(inline);
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
            var run = new Run(buffer.ToString());
            if (bold) run.FontWeight = FontWeights.Bold;
            if (italic) run.FontStyle = FontStyles.Italic;
            result.Add(run);
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
}
