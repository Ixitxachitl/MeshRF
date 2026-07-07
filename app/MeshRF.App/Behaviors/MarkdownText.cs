// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Text.RegularExpressions;
using System.Text;

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
    private static readonly FontFamily EmojiFontFamily = new("Segoe UI Emoji");

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
                AppendFormattedText(output, text[cursor..start], bold, italic);

            string linkText = text[start..end];
            if (Uri.TryCreate(NormalizeUrl(linkText), UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink();
                foreach (var inline in CreateFormattedInlines(linkText, bold, italic))
                    link.Inlines.Add(inline);
                link.NavigateUri = uri;
                output.Add(link);
            }
            else
            {
                AppendFormattedText(output, linkText, bold, italic);
            }

            cursor = end;
        }

        if (cursor < text.Length)
            AppendFormattedText(output, text[cursor..], bold, italic);
    }

    private static IEnumerable<Inline> CreateFormattedInlines(string text, bool bold, bool italic)
    {
        var result = new List<Inline>();
        AppendFormattedText(result, text, bold, italic);
        return result;
    }

    private static void AppendFormattedText(ICollection<Inline> output, string text, bool bold, bool italic)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var buffer = new StringBuilder();

        void FlushBuffer()
        {
            if (buffer.Length == 0) return;
            output.Add(CreateRun(buffer.ToString(), bold, italic));
            buffer.Clear();
        }

        for (int i = 0; i < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            int runeLength = rune.Utf16SequenceLength;

            // An emoji sequence starts when the current rune is an intrinsic emoji,
            // OR when it is a keycap base char (digit, #, *) immediately followed
            // by a continuation (variation selector, combining enclosing keycap, etc.).
            bool isEmojiStart = IsEmojiStarter(rune)
                || (IsKeycapBase(rune)
                    && i + runeLength < text.Length
                    && IsEmojiContinuation(Rune.GetRuneAt(text, i + runeLength)));

            if (isEmojiStart)
            {
                FlushBuffer();

                var emojiText = new StringBuilder();
                emojiText.Append(text, i, runeLength);
                i += runeLength;

                while (i < text.Length)
                {
                    var nextRune = Rune.GetRuneAt(text, i);
                    int nextLen = nextRune.Utf16SequenceLength;
                    if (!IsEmojiContinuation(nextRune))
                        break;

                    emojiText.Append(text, i, nextLen);
                    i += nextLen;

                    // After ZWJ consume the next rune as part of the sequence too.
                    if (nextRune.Value == 0x200D && i < text.Length)
                    {
                        var afterZwj = Rune.GetRuneAt(text, i);
                        emojiText.Append(text, i, afterZwj.Utf16SequenceLength);
                        i += afterZwj.Utf16SequenceLength;
                    }
                }

                output.Add(CreateEmojiContainer(emojiText.ToString(), bold, italic));
                continue;
            }

            buffer.Append(text, i, runeLength);
            i += runeLength;
        }

        FlushBuffer();
    }

    private static Run CreateRun(string text, bool bold, bool italic)
    {
        var run = new Run(text);
        if (bold) run.FontWeight = FontWeights.Bold;
        if (italic) run.FontStyle = FontStyles.Italic;
        return run;
    }

    private static InlineUIContainer CreateEmojiContainer(string text, bool bold, bool italic)
    {
        var emojiText = new Emoji.Wpf.TextBlock
        {
            Text = text,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            IsHitTestVisible = false,
        };

        return new InlineUIContainer(emojiText)
        {
            BaselineAlignment = BaselineAlignment.Center,
        };
    }

    // Runes that are emoji by themselves (no modifier needed to trigger emoji rendering).
    private static bool IsEmojiStarter(Rune rune)
    {
        int value = rune.Value;
        return (value >= 0x1F000 && value <= 0x1FAFF)  // Misc symbols, emoticons, transport…
            || (value >= 0x2600 && value <= 0x27BF)     // Misc symbols, dingbats
            || (value >= 0x2300 && value <= 0x23FF)     // Misc technical
            || (value >= 0x2B00 && value <= 0x2BFF)     // Misc symbols & arrows
            || (value >= 0x1F1E6 && value <= 0x1F1FF)   // Regional indicator letters
            || (value >= 0x1F3FB && value <= 0x1F3FF);  // Skin-tone modifiers
    }

    // Digits 0-9, #, * — plain ASCII that can become emoji via keycap sequences.
    private static bool IsKeycapBase(Rune rune)
    {
        int value = rune.Value;
        return (value >= 0x30 && value <= 0x39) || value == 0x23 || value == 0x2A;
    }

    // Runes that extend/modify the preceding emoji rune(s).
    private static bool IsEmojiContinuation(Rune rune)
    {
        int value = rune.Value;
        return value == 0x200D           // ZWJ
            || value == 0xFE0F           // variation selector-16 (emoji presentation)
            || value == 0xFE0E           // variation selector-15 (text presentation)
            || value == 0x20E3           // combining enclosing keycap
            || (value >= 0x1F3FB && value <= 0x1F3FF)   // skin-tone modifiers
            || (value >= 0xE0020 && value <= 0xE007F);  // tag chars (flag sequences)
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
