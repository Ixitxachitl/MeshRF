// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Attached property that renders a message body as inline Markdown on a
/// TextBlock: <c>**bold**</c>/<c>__bold__</c> becomes bold, <c>*italic*</c>/
/// <c>_italic_</c> becomes italic, and bare URLs become clickable links.
/// Unmatched markers stay literal.
/// </summary>
public static class MarkdownText
{
    private static readonly Regex UrlRegex = new(
        @"(?:(?:https?|ftp)://|www\.)[^\s<>""]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(MarkdownText));

    /// <summary>Short trailing mark appended after the body in its own colour —
    /// the delivery status. It rides in these inlines rather than a TextBlock of
    /// its own so it stays glued to the last word of a wrapped message, which is
    /// where it sat when it was part of the text.</summary>
    public static readonly AttachedProperty<string?> SuffixProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Suffix", typeof(MarkdownText));

    /// <summary>Colour for <see cref="SuffixProperty"/>. Null leaves the mark in
    /// the TextBlock's own foreground.</summary>
    public static readonly AttachedProperty<IBrush?> SuffixBrushProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IBrush?>("SuffixBrush", typeof(MarkdownText));

    public static void SetText(TextBlock target, string? value) => target.SetValue(TextProperty, value);
    public static string? GetText(TextBlock target) => target.GetValue(TextProperty);

    public static void SetSuffix(TextBlock target, string? value) => target.SetValue(SuffixProperty, value);
    public static string? GetSuffix(TextBlock target) => target.GetValue(SuffixProperty);

    public static void SetSuffixBrush(TextBlock target, IBrush? value) => target.SetValue(SuffixBrushProperty, value);
    public static IBrush? GetSuffixBrush(TextBlock target) => target.GetValue(SuffixBrushProperty);

    static MarkdownText()
    {
        // All three rebuild the same inline collection, so any of them changing
        // re-renders from the current value of the other two. The suffix arrives
        // after the body on a recycled row, and its colour after that.
        TextProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Render(tb));
        SuffixProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Render(tb));
        SuffixBrushProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Render(tb));
    }

    private static void Render(TextBlock target)
    {
        var text = GetText(target);
        var suffix = GetSuffix(target);

        target.Inlines?.Clear();
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(suffix)) return;

        target.Inlines ??= new InlineCollection();
        if (!string.IsNullOrEmpty(text))
            foreach (var inline in Parse(text))
                target.Inlines.Add(inline);

        if (string.IsNullOrEmpty(suffix)) return;

        // Leading spaces are part of the run so the gap can't be trimmed away
        // or land on a line of its own when the body wraps tightly.
        var run = new Run($"  {suffix}");
        if (GetSuffixBrush(target) is { } brush)
            run.Foreground = brush;
        target.Inlines.Add(run);
    }

    // Walk the text and emit Runs, toggling bold/italic on the emphasis markers.
    // ** / __ = bold, * / _ = italic. A marker only counts when its matching
    // closing marker is present later in the string; otherwise it is literal.
    private static List<Inline> Parse(string text)
    {
        var result = new List<Inline>();
        var buffer = new StringBuilder();
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
            if (c is '*' or '_')
            {
                bool doubled = i + 1 < text.Length && text[i + 1] == c;
                char marker = c;

                if (doubled)
                {
                    if (bold || HasClosing(text, i + 2, marker, doubled: true))
                    {
                        Flush();
                        bold = !bold;
                        i++; // consume the second marker char
                        continue;
                    }
                }
                else if (italic || HasClosing(text, i + 1, marker, doubled: false))
                {
                    Flush();
                    italic = !italic;
                    continue;
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
            int start = match.Index, end = match.Index + match.Length;
            if (start > cursor)
                output.Add(CreateRun(text[cursor..start], bold, italic));

            string linkText = text[start..end];
            if (Uri.TryCreate(NormalizeUrl(linkText), UriKind.Absolute, out var uri))
                output.Add(CreateLink(linkText, uri, bold, italic));
            else
                output.Add(CreateRun(linkText, bold, italic));

            cursor = end;
        }
        if (cursor < text.Length)
            output.Add(CreateRun(text[cursor..], bold, italic));
    }

    private static Run CreateRun(string text, bool bold, bool italic) => new(text)
    {
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
    };

    /// <summary>Avalonia has no Hyperlink inline, and Run isn't an input
    /// element, so a link is a TextBlock hosted in an InlineUIContainer.</summary>
    private static Inline CreateLink(string text, Uri uri, bool bold, bool italic)
    {
        var link = new TextBlock
        {
            Text = text,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(0x35, 0xC8, 0xFF)),
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(link, uri.ToString());
        link.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenUrl(uri);
        };
        return new InlineUIContainer { Child = link };
    }

    private static string NormalizeUrl(string url) =>
        url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;

    private static void OpenUrl(Uri uri)
    {
        // Only http(s)/ftp reach here (the regex won't match anything else), so
        // this can't be coaxed into launching a local file or custom handler.
        if (uri.Scheme is not ("http" or "https" or "ftp")) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch
        {
            // No browser / blocked — nothing useful to do.
        }
    }
}
