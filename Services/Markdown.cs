using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;

namespace FlankNote;

/// <summary>Markdown-in-editing styling shared by the note sheet and the
/// All Notes detail panel. Markers are revealed on the caret line and visually
/// collapsed elsewhere while the stored source remains unchanged.</summary>
static class Markdown
{
    const RegexOptions SmallRegex = RegexOptions.CultureInvariant;
    static readonly Regex Heading = new(@"^(?<marks>#{1,6})[ \t]+(?<text>.*?)(?:[ \t]+(?<closing>#+)[ \t]*)?$", SmallRegex);
    static readonly Regex Setext = new(@"^\s*(?<marks>=+|-+)\s*$", SmallRegex);
    static readonly Regex ThematicBreak = new(@"^\s*(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$", SmallRegex);
    static readonly Regex OrderedList = new(@"^(?<indent>[ \t]*)(?<marker>\d+[.)])[ \t]+", SmallRegex);
    static readonly Regex Bullet = new(@"^(?<indent>[ \t]*)(?<marker>[-*+])[ \t]+", SmallRegex);
    static readonly Regex Bold = new(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", SmallRegex);
    static readonly Regex Italic = new(@"(?<![\*_])([\*_])(?=[^\*_\s])(.+?)(?<=[^\*_\s])\1(?![\*_])", SmallRegex);
    static readonly Regex InlineCode = new(@"`([^`\r\n]+)`", SmallRegex);
    static readonly Regex Struck = new(@"~~(?=\S)(.+?)(?<=\S)~~", SmallRegex);
    static readonly Regex Quote = new(@"^(?<marks>>+)[ \t]?(?<text>.*)$", SmallRegex);
    static readonly Regex Link = new(@"(?<!!)\[([^\]\r\n]+)\]\(([^)\s]+)\)", SmallRegex);
    static readonly Regex Autolink = new(@"<(?<url>(?:https?://|mailto:)[^>\s]+)>", SmallRegex | RegexOptions.IgnoreCase);
    static readonly Regex FenceOpen = new(@"^\s*(?<ticks>`{3,})(?<info>[^`]*)$", SmallRegex);
    sealed record RenderedBullet(int Index, char SourceMarker);

    static TextRange Marker(Paragraph p, int start, int length)
        => new(PositionAtTextOffset(p, start), PositionAtTextOffset(p, start + length));

    // TextPointer offsets count document symbols (Run boundaries included),
    // whereas Regex offsets count text characters. Convert explicitly or every
    // styled range can drift by one or more positions as WPF splits Runs.
    public static TextPointer PositionAtTextOffset(Paragraph paragraph, int textOffset)
    {
        int remaining = Math.Max(0, textOffset);
        TextPointer? cursor = paragraph.ContentStart;
        while (cursor != null && cursor.CompareTo(paragraph.ContentEnd) < 0)
        {
            if (cursor.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                string run = cursor.GetTextInRun(LogicalDirection.Forward);
                if (remaining <= run.Length)
                    return cursor.GetPositionAtOffset(remaining, LogicalDirection.Forward) ?? paragraph.ContentEnd;
                remaining -= run.Length;
            }
            cursor = cursor.GetNextContextPosition(LogicalDirection.Forward);
        }
        return paragraph.ContentEnd;
    }

    public static Paragraph? ParagraphAt(TextPointer? position)
    {
        DependencyObject? current = position?.Parent;
        while (current != null)
        {
            if (current is Paragraph paragraph) return paragraph;
            current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    public static string SourceText(Paragraph paragraph)
    {
        string text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        if (paragraph.Tag is not RenderedBullet marker
            || marker.Index < 0 || marker.Index >= text.Length
            || text[marker.Index] != '\u2022') return text;
        return text[..marker.Index] + marker.SourceMarker + text[(marker.Index + 1)..];
    }

    public static void RestoreSourceMarkers(FlowDocument document)
    {
        foreach (var paragraph in document.Blocks.OfType<Paragraph>().ToArray())
            RestoreSourceMarker(paragraph);
        ClearBlockStyles(document);
    }

    static void RestoreSourceMarker(Paragraph paragraph)
    {
        if (paragraph.Tag is not RenderedBullet marker) return;
        string text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        if (marker.Index >= 0 && marker.Index < text.Length && text[marker.Index] == '\u2022')
            Marker(paragraph, marker.Index, 1).Text = marker.SourceMarker.ToString();
        paragraph.Tag = null;
    }

    public static bool HandleTaskReturn(RichTextBox editor, bool markdownEnabled)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || !editor.Selection.IsEmpty) return false;
        var paragraph = ParagraphAt(editor.CaretPosition);
        if (paragraph == null) return false;
        string text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        if (!markdownEnabled
            && !text.StartsWith(Tasks.Open + " ")
            && !text.StartsWith(Tasks.Done + " ")) return false;
        if (!Tasks.IsTask(text)) return false;

        editor.BeginChange();
        try
        {
            if (Tasks.Strip(text).Trim().Length == 0)
            {
                new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text = "";
                editor.CaretPosition = paragraph.ContentStart;
            }
            else
            {
                int offset = Math.Clamp(
                    new TextRange(paragraph.ContentStart, editor.CaretPosition).Text.Length,
                    0, text.Length);
                string before = text[..offset];
                string after = text[offset..];
                new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text = before;
                var next = new Paragraph(new Run(Tasks.Continuation(text, after))) { Margin = new Thickness(0) };
                editor.Document.Blocks.InsertAfter(paragraph, next);
                editor.CaretPosition = PositionAtTextOffset(next,
                    Tasks.ContentOffset(new TextRange(next.ContentStart, next.ContentEnd).Text));
            }
        }
        finally
        {
            editor.EndChange();
        }
        return true;
    }

    /// <summary>Styles a complete document so fenced code blocks can span
    /// paragraphs. Inactive bullet markers use a temporary display glyph;
    /// <see cref="SourceText"/> always returns their original Markdown.</summary>
    public static void StyleDocument(FlowDocument document, NoteColor pal, double baseSize,
                                     Paragraph? activeParagraph)
    {
        ResetDocument(document, pal.InkB, baseSize);

        // Applying TextRange properties can split or merge WPF Runs. That bumps
        // the document text-tree version and invalidates a live Blocks
        // enumerator even though the Paragraph objects themselves still exist.
        // Always style a stable snapshot.
        var paragraphs = document.Blocks.OfType<Paragraph>().ToArray();
        foreach (var paragraph in paragraphs)
            RestoreSourceMarker(paragraph);
        ClearBlockStyles(paragraphs);
        var texts = paragraphs
            .Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text)
            .ToArray();
        var setextHeadings = new bool[paragraphs.Length];
        var setextMarkers = new bool[paragraphs.Length];
        for (int i = 1; i < paragraphs.Length; i++)
        {
            if (CanBeSetextHeadingText(texts[i - 1]) && Setext.IsMatch(texts[i]))
            {
                setextHeadings[i - 1] = true;
                setextMarkers[i] = true;
            }
        }

        int fenceLength = 0;
        for (int i = 0; i < paragraphs.Length; i++)
        {
            var paragraph = paragraphs[i];
            var text = texts[i];
            if (fenceLength == 0)
            {
                var opening = FenceOpen.Match(text);
                if (opening.Success)
                {
                    fenceLength = opening.Groups["ticks"].Length;
                    if (!ReferenceEquals(paragraph, activeParagraph))
                        StyleFenceMarker(paragraph, pal, reveal: false);
                }
                else if (setextMarkers[i])
                {
                    if (!ReferenceEquals(paragraph, activeParagraph))
                        StyleSetextMarker(paragraph, pal, reveal: false);
                }
                else if (!ReferenceEquals(paragraph, activeParagraph))
                {
                    StyleParagraph(paragraph, pal, baseSize, revealMarkers: false,
                        setextHeadings[i]
                            ? (Setext.Match(texts[i + 1]).Groups["marks"].Value[0] == '=' ? 1 : 2)
                            : null);
                }
            }
            else if (IsFenceClosing(text, fenceLength))
            {
                if (!ReferenceEquals(paragraph, activeParagraph))
                    StyleFenceMarker(paragraph, pal, reveal: false);
                fenceLength = 0;
            }
            else if (!ReferenceEquals(paragraph, activeParagraph))
            {
                StyleCodeBlock(paragraph, pal);
            }
        }
    }

    public static void ResetDocument(FlowDocument document, Brush foreground, double baseSize)
    {
        var wholeDocument = new TextRange(document.ContentStart, document.ContentEnd);
        wholeDocument.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
        wholeDocument.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
        wholeDocument.ApplyPropertyValue(TextElement.FontFamilyProperty, UiTheme.Font);
        wholeDocument.ApplyPropertyValue(TextElement.ForegroundProperty, foreground);
        wholeDocument.ApplyPropertyValue(TextElement.FontSizeProperty, baseSize);
        wholeDocument.ApplyPropertyValue(Inline.TextDecorationsProperty, null);
        wholeDocument.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
    }

    public static void ClearBlockStyles(FlowDocument document)
        => ClearBlockStyles(document.Blocks.OfType<Paragraph>().ToArray());

    public static bool TryGetBullet(string text, out int markerIndex)
    {
        if (ThematicBreak.IsMatch(text))
        {
            markerIndex = -1;
            return false;
        }
        var match = Bullet.Match(text);
        markerIndex = match.Success ? match.Groups["marker"].Index : -1;
        return match.Success;
    }

    public static bool TryToggleTaskAtPoint(RichTextBox editor, MouseButtonEventArgs e, bool markdownEnabled)
    {
        var position = editor.GetPositionFromPoint(e.GetPosition(editor), true);
        var paragraph = ParagraphAt(position);
        if (position == null || paragraph == null) return false;
        var text = SourceText(paragraph);
        if (!markdownEnabled
            && !text.StartsWith(Tasks.Open + " ")
            && !text.StartsWith(Tasks.Done + " ")) return false;
        if (!Tasks.IsTask(text)) return false;
        int offset = new TextRange(paragraph.ContentStart, position).Text.Length;
        if (!Tasks.IsMarkerOffset(text, offset)) return false;
        paragraph.Tag = null;
        new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text = Tasks.Toggle(text);
        e.Handled = true;
        return true;
    }

    public static bool CanBeSetextHeadingText(string text)
        => !string.IsNullOrWhiteSpace(text)
            && !Heading.IsMatch(text)
            && !Bullet.IsMatch(text)
            && !OrderedList.IsMatch(text)
            && !Quote.IsMatch(text)
            && !FenceOpen.IsMatch(text)
            && !ThematicBreak.IsMatch(text)
            && !Tasks.IsTask(text);

    public static bool TryGetFenceOpening(string text, out int length)
    {
        var match = FenceOpen.Match(text);
        length = match.Success ? match.Groups["ticks"].Length : 0;
        return match.Success;
    }

    public static bool IsFenceClosing(string text, int length)
    {
        if (length < 3) return false;
        var value = text.AsSpan().Trim();
        if (value.Length < length) return false;
        foreach (char character in value)
            if (character != '`') return false;
        return true;
    }

    static void ClearBlockStyles(IEnumerable<Paragraph> paragraphs)
    {
        foreach (var paragraph in paragraphs)
        {
            paragraph.Margin = new Thickness(0);
            paragraph.Padding = new Thickness(0);
            paragraph.Background = Brushes.Transparent;
            paragraph.BorderBrush = Brushes.Transparent;
            paragraph.BorderThickness = new Thickness(0);
            paragraph.TextIndent = 0;
        }
    }

    static void StyleCodeBlock(Paragraph paragraph, NoteColor pal)
    {
        paragraph.Padding = new Thickness(7, 2, 7, 2);
        paragraph.Background = UiTheme.Tint(pal.Ink, 0.07);
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        range.ApplyPropertyValue(TextElement.FontFamilyProperty, UiTheme.MonospaceFont);
        range.ApplyPropertyValue(TextElement.ForegroundProperty, UiTheme.Tint(pal.Ink, 0.90));
    }

    static void StyleFenceMarker(Paragraph paragraph, NoteColor pal, bool reveal)
    {
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        range.ApplyPropertyValue(TextElement.FontFamilyProperty, UiTheme.MonospaceFont);
        if (reveal)
            range.ApplyPropertyValue(TextElement.ForegroundProperty, UiTheme.Tint(pal.Ink, 0.32));
        else
            Hide(range);
    }

    static void StyleSetextMarker(Paragraph paragraph, NoteColor pal, bool reveal)
    {
        paragraph.Padding = new Thickness(0, 3, 0, 3);
        paragraph.BorderBrush = UiTheme.Tint(pal.Dash, 0.45);
        paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        if (reveal) range.ApplyPropertyValue(TextElement.ForegroundProperty,
            UiTheme.Tint(pal.Ink, 0.30));
        else Hide(range);
    }

    public static void StyleParagraph(Paragraph p, NoteColor pal, double baseSize, bool revealMarkers,
                                      int? setextLevel = null)
    {
        p.Margin = new Thickness(0);   // keep the note dense — no paragraph gaps
        var text = new TextRange(p.ContentStart, p.ContentEnd).Text;
        var body = new TextRange(p.ContentStart, p.ContentEnd);

        if (ThematicBreak.IsMatch(text))
        {
            StyleThematicBreak(p, pal, revealMarkers);
            return;
        }

        // GFM/custom tasks: strike completed lines but continue styling their
        // list marker and inline formatting.
        if (Tasks.IsDone(text))
        {
            body.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Strikethrough);
            body.ApplyPropertyValue(TextElement.ForegroundProperty, UiTheme.Tint(pal.Ink, 0.55));
        }
        // ATX headings: levels 1-6
        var heading = Heading.Match(text);
        if (heading.Success)
        {
            int level = heading.Groups["marks"].Length;
            body.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
            double addition = level switch { 1 => 5, 2 => 4, 3 => 3, 4 => 2, 5 => 1, _ => 0.5 };
            body.ApplyPropertyValue(TextElement.FontSizeProperty, baseSize + addition);
            StyleMarker(Marker(p, 0, level), pal, revealMarkers);
            if (heading.Groups["closing"].Success)
                StyleMarker(Marker(p, heading.Groups["closing"].Index,
                    heading.Groups["closing"].Length), pal, revealMarkers);
        }
        else if (setextLevel is { } level)
        {
            body.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
            body.ApplyPropertyValue(TextElement.FontSizeProperty, baseSize + (level == 1 ? 5 : 3));
        }

        var quote = Quote.Match(text);
        if (quote.Success)
        {
            int depth = quote.Groups["marks"].Length;
            body.ApplyPropertyValue(TextElement.ForegroundProperty, UiTheme.Tint(pal.Ink, 0.62));
            body.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Italic);
            p.Margin = new Thickness(8 + (depth - 1) * 8, 1, 0, 1);
            p.Padding = new Thickness(10, 1, 0, 1);
            p.BorderBrush = UiTheme.Tint(pal.Dash, 0.55);
            p.BorderThickness = new Thickness(2, 0, 0, 0);
            StyleMarker(Marker(p, 0, depth), pal, revealMarkers);
        }

        var bullet = Bullet.Match(text);
        if (bullet.Success)
        {
            var marker = bullet.Groups["marker"];
            int indent = IndentColumns(bullet.Groups["indent"].Value);
            p.Margin = new Thickness(15 + indent * 3, 0, 0, 0);
            p.TextIndent = -10;
            if (revealMarkers)
            {
                StyleMarker(Marker(p, marker.Index, marker.Length), pal, reveal: true);
            }
            else
            {
                Marker(p, marker.Index, marker.Length).Text = "\u2022";
                p.Tag = new RenderedBullet(marker.Index, marker.Value[0]);
                var renderedMarker = Marker(p, marker.Index, 1);
                renderedMarker.ApplyPropertyValue(TextElement.ForegroundProperty, pal.DashB);
                renderedMarker.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
            }
        }

        // ordered lists: both "1. item" and "1) item"
        var ordered = OrderedList.Match(text);
        if (ordered.Success)
        {
            var marker = ordered.Groups["marker"];
            int indent = IndentColumns(ordered.Groups["indent"].Value);
            // Keep the marker in the gutter while reserving enough room for
            // multi-digit numbers; a fixed -15px indent overlaps at 100+.
            double markerGutter = 15 + Math.Max(0, marker.Length - 2) * 8;
            p.Margin = new Thickness(5 + markerGutter + indent * 3, 0, 0, 0);
            p.TextIndent = -markerGutter;
            var markerRange = Marker(p, marker.Index, marker.Length);
            markerRange.ApplyPropertyValue(TextElement.ForegroundProperty, pal.DashB);
            markerRange.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
        }

        StylePaired(p, text, Bold, 1, 2, pal, revealMarkers,
            range => range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold));
        StylePaired(p, text, Italic, 1, 2, pal, revealMarkers,
            range => range.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Italic));
        StylePaired(p, text, Struck, 2, 1, pal, revealMarkers,
            range => range.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Strikethrough));
        StylePaired(p, text, InlineCode, 1, 1, pal, revealMarkers,
            range =>
            {
                range.ApplyPropertyValue(TextElement.FontSizeProperty, baseSize);
                range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
                range.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
                range.ApplyPropertyValue(TextElement.ForegroundProperty, pal.InkB);
                range.ApplyPropertyValue(Inline.TextDecorationsProperty, null);
                range.ApplyPropertyValue(TextElement.FontFamilyProperty, UiTheme.MonospaceFont);
                range.ApplyPropertyValue(TextElement.BackgroundProperty, UiTheme.Tint(pal.Ink, 0.07));
            });
        // Run links last so Markdown-like characters inside a URL cannot make
        // its hidden destination visible again.
        StyleLinks(p, text, pal, revealMarkers);

        if (Tasks.TryGetMarkdownMarker(text, out int boxStart, out _, out _))
        {
            var boxRange = Marker(p, boxStart, 3);
            boxRange.ApplyPropertyValue(TextElement.ForegroundProperty, pal.DashB);
            boxRange.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
        }

        if (Tasks.IsDone(text))
            body.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Strikethrough);
    }

    static int IndentColumns(string value)
        => value.Aggregate(0, (count, c) => count + (c == '\t' ? 4 : 1));

    static void StyleThematicBreak(Paragraph paragraph, NoteColor pal, bool reveal)
    {
        paragraph.Padding = new Thickness(0, 4, 0, 4);
        paragraph.BorderBrush = UiTheme.Tint(pal.Dash, 0.42);
        paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        if (reveal)
            range.ApplyPropertyValue(TextElement.ForegroundProperty,
                UiTheme.Tint(pal.Ink, 0.30));
        else
            Hide(range);
    }

    static void StyleLinks(Paragraph paragraph, string text, NoteColor pal, bool revealMarkers)
    {
        foreach (Match match in Link.Matches(text))
        {
            var label = match.Groups[1];
            StyleMarker(Marker(paragraph, match.Index, 1), pal, revealMarkers);
            StyleMarker(Marker(paragraph, label.Index + label.Length,
                match.Index + match.Length - label.Index - label.Length), pal, revealMarkers);

            var labelRange = Marker(paragraph, label.Index, label.Length);
            labelRange.ApplyPropertyValue(TextElement.ForegroundProperty, pal.DashB);
            labelRange.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Medium);
            labelRange.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
        }

        foreach (Match match in Autolink.Matches(text))
        {
            var url = match.Groups["url"];
            StyleMarker(Marker(paragraph, match.Index, 1), pal, revealMarkers);
            StyleMarker(Marker(paragraph, match.Index + match.Length - 1, 1), pal, revealMarkers);
            var urlRange = Marker(paragraph, url.Index, url.Length);
            urlRange.ApplyPropertyValue(TextElement.ForegroundProperty, pal.DashB);
            urlRange.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Medium);
            urlRange.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
        }
    }

    static void StylePaired(Paragraph paragraph, string text, Regex expression,
                            int markerLength, int contentGroup, NoteColor pal,
                            bool revealMarkers, Action<TextRange> applyContent)
    {
        foreach (Match match in expression.Matches(text))
        {
            var content = match.Groups[contentGroup];
            int actualMarkerLength = expression == Bold || expression == Italic
                ? match.Groups[1].Length
                : markerLength;
            StyleMarker(Marker(paragraph, match.Index, actualMarkerLength), pal, revealMarkers);
            StyleMarker(Marker(paragraph, match.Index + match.Length - actualMarkerLength, actualMarkerLength),
                pal, revealMarkers);
            applyContent(Marker(paragraph, content.Index, content.Length));
        }
    }

    static void StyleMarker(TextRange range, NoteColor pal, bool reveal)
    {
        if (reveal)
            range.ApplyPropertyValue(TextElement.ForegroundProperty, UiTheme.Tint(pal.Ink, 0.32));
        else
            Hide(range);
    }

    static void Hide(TextRange range)
    {
        // WPF has no NSTextKit-style null glyph attribute. A near-zero font is
        // the stable editable equivalent: source remains in the document while
        // the marker occupies no perceptible space outside the caret line.
        range.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Transparent);
        range.ApplyPropertyValue(TextElement.FontSizeProperty, 0.1);
    }

    /// <summary>Windows equivalent of the upstream Command-click behavior.
    /// Plain clicks keep editing; Ctrl-click opens only safe web/mail schemes.</summary>
    public static bool TryOpenLink(RichTextBox editor, MouseButtonEventArgs e, bool markdownEnabled)
    {
        if (!markdownEnabled || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;
        var position = editor.GetPositionFromPoint(e.GetPosition(editor), true);
        var paragraph = ParagraphAt(position);
        if (position == null || paragraph == null) return false;

        var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        int offset = new TextRange(paragraph.ContentStart, position).Text.Length;
        foreach (Match match in Link.Matches(text))
        {
            var label = match.Groups[1];
            if (offset < label.Index || offset > label.Index + label.Length) continue;
            return OpenLink(match.Groups[2].Value, e);
        }
        foreach (Match match in Autolink.Matches(text))
        {
            var url = match.Groups["url"];
            if (offset < url.Index || offset > url.Index + url.Length) continue;
            return OpenLink(url.Value, e);
        }
        return false;
    }

    static bool OpenLink(string value, MouseButtonEventArgs e)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto")) return false;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.ReportError($"Opening link failed: {ex}");
            return false;
        }
        e.Handled = true;
        return true;
    }

}
