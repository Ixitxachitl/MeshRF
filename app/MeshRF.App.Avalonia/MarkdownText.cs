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
/// Unmatched markers stay literal. Port of MeshRF.App's Behaviors/MarkdownText,
/// using the same parsing rules so both apps render a message identically.
/// </summary>
public static class MarkdownText
{
    private static readonly Regex UrlRegex = new(
        @"(?:(?:https?|ftp)://|www\.)[^\s<>""]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(MarkdownText));

    public static void SetText(TextBlock target, string? value) => target.SetValue(TextProperty, value);
    public static string? GetText(TextBlock target) => target.GetValue(TextProperty);

    static MarkdownText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((tb, e) =>
            Render(tb, e.NewValue as string));
    }

    private static void Render(TextBlock target, string? text)
    {
        target.Inlines?.Clear();
        if (string.IsNullOrEmpty(text)) return;

        target.Inlines ??= new InlineCollection();
        foreach (var inline in Parse(text))
            target.Inlines.Add(inline);
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
