using System.Text.RegularExpressions;
using System.Windows.Media;

namespace FlankNote;

/// <summary>Low-allocation Markdown styling for the native RichEdit editor.</summary>
static class NativeMarkdownStyler
{
    static readonly Regex Heading = new(@"^(?<marks>#{1,6})(?<space>[ \t]+)", RegexOptions.CultureInvariant);
    static readonly Regex Quote = new(@"^(?<marks>\s*>+)(?<space>[ \t]?)", RegexOptions.CultureInvariant);
    static readonly Regex Bullet = new(@"^(?<indent>\s*)(?<marker>[-*+])(?<space>[ \t]+)", RegexOptions.CultureInvariant);
    static readonly Regex Ordered = new(@"^(?<indent>\s*)(?<marker>\d+[.)])(?<space>[ \t]+)", RegexOptions.CultureInvariant);
    static readonly Regex Bold = new(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.CultureInvariant);
    static readonly Regex Italic = new(@"(?<![\*_])([\*_])(?=[^\*_\s])(.+?)(?<=[^\*_\s])\1(?![\*_])", RegexOptions.CultureInvariant);
    static readonly Regex Code = new(@"`([^`\r\n]+)`", RegexOptions.CultureInvariant);
    static readonly Regex Strike = new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.CultureInvariant);
    static readonly Regex Link = new(@"(?<open>\[)(?<label>[^\]\r\n]+)(?<close>\]\([^\)\s]+\))", RegexOptions.CultureInvariant);
    static readonly Regex Fence = new(@"^\s*`{3,}", RegexOptions.CultureInvariant);
    static readonly Regex Rule = new(@"^\s*(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})\s*$", RegexOptions.CultureInvariant);

    public static bool TryGetLinkAt(string text, int offset, out string url)
    {
        foreach (Match match in Link.Matches(text))
        {
            var label = match.Groups["label"];
            if (offset >= label.Index && offset <= label.Index + label.Length)
            {
                url = match.Groups["close"].Value[2..^1];
                return true;
            }
        }
        url = string.Empty;
        return false;
    }

    public static void OpenLinkAt(NativeRichEdit editor, int offset)
    {
        if (!TryGetLinkAt(editor.Text, offset, out var value)) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { App.ReportError($"Opening link failed: {ex}"); }
    }

    public static int ActiveLine(string text, int selectionStart)
    {
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        int line = 0;
        for (int i = 0; i < selectionStart; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    public static void Style(NativeRichEdit editor, bool markdown, Color ink, Color dash,
                             int activeLine, double baseSize)
    {
        string text = editor.Text;
        if (text.Length == 0) return;
        var savedStart = editor.SelectionStart;
        var savedLength = editor.SelectionLength;
        editor.ApplyBaseFormat();
        if (!markdown)
        {
            editor.Select(savedStart, savedLength);
            return;
        }

        int lineStart = 0;
        bool inFence = false;
        int lineNumber = 0;
        while (lineStart <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            int length = lineEnd - lineStart;
            string line = text.Substring(lineStart, length).TrimEnd('\r');
            bool active = lineNumber == activeLine;

            if (Fence.IsMatch(line))
            {
                if (!active) Hide(editor, lineStart, Math.Min(length, line.Length));
                inFence = !inFence;
            }
            else if (!active && inFence)
            {
                Format(editor, lineStart, length, NativeFormatKind.Code, ink, baseSize: baseSize);
            }
            else if (!active)
            {
                StyleLine(editor, lineStart, line, ink, dash, baseSize);
            }

            if (lineEnd == text.Length) break;
            lineStart = lineEnd + 1;
            lineNumber++;
        }
        editor.Select(savedStart, savedLength);
    }

    static void StyleLine(NativeRichEdit editor, int offset, string line,
                          Color ink, Color dash, double baseSize)
    {
        if (Rule.IsMatch(line))
        {
            Hide(editor, offset, line.Length);
            return;
        }

        var heading = Heading.Match(line);
        if (heading.Success)
        {
            Format(editor, offset, line.Length, NativeFormatKind.Heading, ink,
                Math.Max(1, 7 - heading.Groups["marks"].Length));
            Hide(editor, offset + heading.Groups["marks"].Index,
                heading.Groups["marks"].Length + heading.Groups["space"].Length);
        }

        var quote = Quote.Match(line);
        if (quote.Success)
        {
            Format(editor, offset, line.Length, NativeFormatKind.Quote, ink);
            Hide(editor, offset + quote.Groups["marks"].Index,
                quote.Groups["marks"].Length + quote.Groups["space"].Length);
        }

        var bullet = Bullet.Match(line);
        if (bullet.Success)
            Hide(editor, offset + bullet.Groups["marker"].Index,
                bullet.Groups["marker"].Length + bullet.Groups["space"].Length);

        var ordered = Ordered.Match(line);
        if (ordered.Success)
            Format(editor, offset + ordered.Groups["marker"].Index,
                ordered.Groups["marker"].Length, NativeFormatKind.Marker, dash);

        foreach (Match match in Bold.Matches(line))
        {
            Format(editor, offset + match.Groups[2].Index, match.Groups[2].Length,
                NativeFormatKind.Bold, ink);
            Hide(editor, offset + match.Index, match.Groups[1].Length);
            Hide(editor, offset + match.Index + match.Length - match.Groups[1].Length,
                match.Groups[1].Length);
        }
        foreach (Match match in Italic.Matches(line))
        {
            Format(editor, offset + match.Groups[2].Index, match.Groups[2].Length,
                NativeFormatKind.Italic, ink);
            Hide(editor, offset + match.Index, 1);
            Hide(editor, offset + match.Index + match.Length - 1, 1);
        }
        foreach (Match match in Strike.Matches(line))
        {
            Format(editor, offset + match.Groups[1].Index, match.Groups[1].Length,
                NativeFormatKind.Strike, ink);
            Hide(editor, offset + match.Index, 2);
            Hide(editor, offset + match.Index + match.Length - 2, 2);
        }
        foreach (Match match in Code.Matches(line))
        {
            Format(editor, offset + match.Groups[1].Index, match.Groups[1].Length,
                NativeFormatKind.Code, ink);
            Hide(editor, offset + match.Index, 1);
            Hide(editor, offset + match.Index + match.Length - 1, 1);
        }
        foreach (Match match in Link.Matches(line))
        {
            Format(editor, offset + match.Groups["label"].Index,
                match.Groups["label"].Length, NativeFormatKind.Link, dash);
            Hide(editor, offset + match.Groups["open"].Index, 1);
            Hide(editor, offset + match.Groups["close"].Index,
                match.Groups["close"].Length);
        }
    }

    static void Hide(NativeRichEdit editor, int start, int length)
        => Format(editor, start, length, NativeFormatKind.Hidden, Colors.Transparent);

    static void Format(NativeRichEdit editor, int start, int length,
                       NativeFormatKind kind, Color colour, int level = 0,
                       double baseSize = 14)
    {
        if (length <= 0) return;
        var format = NativeRichEdit.NativeCharFormat.Default(Rgb(colour), baseSize);
        format.Native.dwEffects = kind switch
        {
            NativeFormatKind.Bold => NativeRichEdit.NativeFormat.CFE_BOLD,
            NativeFormatKind.Italic => NativeRichEdit.NativeFormat.CFE_ITALIC,
            NativeFormatKind.Strike => NativeRichEdit.NativeFormat.CFE_STRIKEOUT,
            NativeFormatKind.Hidden => NativeRichEdit.NativeFormat.CFE_HIDDEN,
            _ => 0,
        };
        format.Native.dwMask |= kind switch
        {
            NativeFormatKind.Bold => NativeRichEdit.NativeFormat.CFM_BOLD,
            NativeFormatKind.Italic => NativeRichEdit.NativeFormat.CFM_ITALIC,
            NativeFormatKind.Strike => NativeRichEdit.NativeFormat.CFM_STRIKEOUT,
            NativeFormatKind.Hidden => NativeRichEdit.NativeFormat.CFM_HIDDEN,
            _ => 0u,
        };
        if (kind == NativeFormatKind.Heading)
            format.Native.yHeight = (int)Math.Round((baseSize + Math.Max(0, 7 - level)) * 20.0);
        if (kind == NativeFormatKind.Code)
            format.Native.szFaceName = "Cascadia Mono";
        format.Native.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeRichEdit.CHARFORMAT2>();
        editor.ApplyFormat(start, length, format);
    }

    static int Rgb(Color color) => color.R | (color.G << 8) | (color.B << 16);
    enum NativeFormatKind { Bold, Italic, Strike, Code, Link, Marker, Heading, Quote, Hidden }
}
