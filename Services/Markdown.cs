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
    static readonly Regex Heading = new(@"^(#{1,6})[ \t]+(.+)$", RegexOptions.Compiled);
    static readonly Regex OrderedList = new(@"^\s*(\d+[.)])\s+", RegexOptions.Compiled);
    static readonly Regex Bullet = new(@"^\s*([-*+])[ \t]+", RegexOptions.Compiled);
    static readonly Regex Bold = new(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled);
    static readonly Regex Italic = new(@"(?<![\*_])([\*_])(?=[^\*_\s])(.+?)(?<=[^\*_\s])\1(?![\*_])", RegexOptions.Compiled);
    static readonly Regex InlineCode = new(@"`([^`\r\n]+)`", RegexOptions.Compiled);
    static readonly Regex Struck = new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled);
    static readonly Regex Quote = new(@"^>[ \t]?(.*)$", RegexOptions.Compiled);
    static readonly Regex Link = new(@"(?<!!)\[([^\]\r\n]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);
    static readonly Regex FenceOpen = new(@"^\s*(?<ticks>`{3,})(?<info>[^`]*)$", RegexOptions.Compiled);

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

    public static bool HandleTaskReturn(RichTextBox editor)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || !editor.Selection.IsEmpty) return false;
        var paragraph = ParagraphAt(editor.CaretPosition);
        if (paragraph == null) return false;
        string text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
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
                var next = new Paragraph(new Run(Tasks.Open + " " + after)) { Margin = new Thickness(0) };
                editor.Document.Blocks.InsertAfter(paragraph, next);
                editor.CaretPosition = PositionAtTextOffset(next, 2);
            }
        }
        finally
        {
            editor.EndChange();
        }
        return true;
    }

    /// <summary>Styles a complete document so fenced code blocks can span
    /// paragraphs. The source text and Markdown markers are never replaced.</summary>
    public static void StyleDocument(FlowDocument document, NoteColor pal, double baseSize,
                                     Paragraph? activeParagraph)
    {
        // Applying TextRange properties can split or merge WPF Runs. That bumps
        // the document text-tree version and invalidates a live Blocks
        // enumerator even though the Paragraph objects themselves still exist.
        // Always style a stable snapshot.
        var paragraphs = document.Blocks.OfType<Paragraph>().ToArray();
        ClearBlockStyles(paragraphs);
        int fenceLength = 0;
        foreach (var paragraph in paragraphs)
        {
            var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
            if (fenceLength == 0)
            {
                var opening = FenceOpen.Match(text);
                if (opening.Success)
                {
                    fenceLength = opening.Groups["ticks"].Length;
                    StyleFenceMarker(paragraph, pal, ReferenceEquals(paragraph, activeParagraph));
                }
                else
                {
                    StyleParagraph(paragraph, pal, baseSize, ReferenceEquals(paragraph, activeParagraph));
                }
            }
            else if (Regex.IsMatch(text, $@"^\s*`{{{fenceLength},}}\s*$"))
            {
                StyleFenceMarker(paragraph, pal, ReferenceEquals(paragraph, activeParagraph));
                fenceLength = 0;
            }
            else
            {
                StyleCodeBlock(paragraph, pal);
            }
        }
    }

    public static void ClearBlockStyles(FlowDocument document)
        => ClearBlockStyles(document.Blocks.OfType<Paragraph>().ToArray());

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
        paragraph.Background = new SolidColorBrush(pal.Ink) { Opacity = 0.07 };
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        range.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas"));
        range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(pal.Ink) { Opacity = 0.90 });
    }

    static void StyleFenceMarker(Paragraph paragraph, NoteColor pal, bool reveal)
    {
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        range.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas"));
        if (reveal)
            range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(pal.Ink) { Opacity = 0.32 });
        else
            Hide(range);
    }

    public static void StyleParagraph(Paragraph p, NoteColor pal, double baseSize, bool revealMarkers)
    {
        p.Margin = new Thickness(0);   // keep the note dense — no paragraph gaps
        var text = new TextRange(p.ContentStart, p.ContentEnd).Text;
        var body = new TextRange(p.ContentStart, p.ContentEnd);

        // tasks: strike a done line and dim it
        if (Tasks.IsDone(text))
        {
            body.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Strikethrough);
            body.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(pal.Ink) { Opacity = 0.55 });
            return;
        }
        // ATX headings: levels 1-6
        var heading = Heading.Match(text);
        if (heading.Success)
        {
            int level = heading.Groups[1].Length;
            body.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
            double[] additions = [5, 4, 3, 2, 1, 0.5];
            body.ApplyPropertyValue(TextElement.FontSizeProperty, baseSize + additions[level - 1]);
            StyleMarker(Marker(p, 0, level), pal, revealMarkers);
        }

        var quote = Quote.Match(text);
        if (quote.Success)
        {
            body.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(pal.Ink) { Opacity = 0.62 });
            body.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Italic);
            p.Margin = new Thickness(8, 1, 0, 1);
            p.Padding = new Thickness(10, 1, 0, 1);
            p.BorderBrush = new SolidColorBrush(pal.Dash) { Opacity = 0.55 };
            p.BorderThickness = new Thickness(2, 0, 0, 0);
            StyleMarker(Marker(p, 0, 1), pal, revealMarkers);
        }

        var bullet = Bullet.Match(text);
        if (bullet.Success)
        {
            var marker = bullet.Groups[1];
            p.Margin = new Thickness(15, 0, 0, 0);
            p.TextIndent = -10;
            var markerRange = Marker(p, marker.Index, marker.Length);
            markerRange.ApplyPropertyValue(TextElement.ForegroundProperty, pal.DashB);
            markerRange.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
        }

        // ordered lists: both "1. item" and "1) item"
        var ordered = OrderedList.Match(text);
        if (ordered.Success)
        {
            var marker = ordered.Groups[1];
            p.Margin = new Thickness(20, 0, 0, 0);
            p.TextIndent = -15;
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
                range.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas"));
                range.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(pal.Ink) { Opacity = 0.07 });
            });
        // Run links last so Markdown-like characters inside a URL cannot make
        // its hidden destination visible again.
        StyleLinks(p, text, pal, revealMarkers);
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
            range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(pal.Ink) { Opacity = 0.32 });
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
    public static bool TryOpenLink(RichTextBox editor, MouseButtonEventArgs e)
    {
        if (!Settings.Markdown || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;
        var position = editor.GetPositionFromPoint(e.GetPosition(editor), true);
        var paragraph = ParagraphAt(position);
        if (position == null || paragraph == null) return false;

        var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        int offset = new TextRange(paragraph.ContentStart, position).Text.Length;
        foreach (Match match in Link.Matches(text))
        {
            var label = match.Groups[1];
            if (offset < label.Index || offset > label.Index + label.Length) continue;
            if (!Uri.TryCreate(match.Groups[2].Value, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme is not ("http" or "https" or "mailto")) return false;

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
        return false;
    }

}
