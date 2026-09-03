using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace FlankNote;

/// <summary>Read-only WPF rendering for Markdown supplied by notes and GitHub Releases.</summary>
static class MarkdownPreview
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static FlowDocument CreateDocument(string? markdown, NoteColor palette, double fontSize = 13)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = UiTheme.Font,
            FontSize = fontSize,
            Foreground = palette.InkB,
        };

        var source = markdown ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            document.Blocks.Add(new Paragraph(new Run(Loc.T("Empty note", "空便签")))
            {
                Foreground = new SolidColorBrush(palette.Ink) { Opacity = 0.58 },
                Margin = new Thickness(0),
            });
            return document;
        }

        var parsed = Markdig.Markdown.Parse(source, Pipeline);
        foreach (var block in parsed)
            AddBlock(document.Blocks, block, palette, fontSize);
        return document;
    }

    public static FlowDocument CreatePlainTextDocument(string? text, NoteColor palette, double fontSize = 13)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = UiTheme.Font,
            FontSize = fontSize,
            Foreground = palette.InkB,
        };

        var source = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        if (source.Length == 0)
        {
            document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
            return document;
        }

        foreach (var line in source.Split('\n'))
        {
            document.Blocks.Add(new Paragraph(new Run(line))
            {
                Margin = new Thickness(0),
                LineHeight = fontSize * 1.5,
            });
        }
        return document;
    }

    static void AddBlock(BlockCollection blocks, MdBlock block, NoteColor palette, double fontSize)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = new Paragraph
                {
                    Margin = new Thickness(0, heading.Level == 1 ? 8 : 5, 0, 5),
                    FontSize = HeadingSize(heading.Level, fontSize),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = palette.InkB,
                };
                AddInlines(title.Inlines, heading.Inline?.FirstChild, palette, fontSize);
                blocks.Add(title);
                break;

            case ParagraphBlock paragraph:
                var text = new Paragraph { Margin = new Thickness(0, 0, 0, 7), LineHeight = fontSize * 1.5 };
                AddInlines(text.Inlines, paragraph.Inline?.FirstChild, palette, fontSize);
                blocks.Add(text);
                break;

            case ListBlock list:
                var wpfList = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(18, 0, 0, 7),
                    Padding = new Thickness(0),
                };
                foreach (var item in list.OfType<ListItemBlock>())
                    wpfList.ListItems.Add(CreateListItem(item, palette, fontSize));
                blocks.Add(wpfList);
                break;

            case QuoteBlock quote:
                var quoteSection = new Section
                {
                    BorderBrush = new SolidColorBrush(palette.Dash) { Opacity = 0.70 },
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 1, 0, 1),
                    Margin = new Thickness(0, 2, 0, 8),
                    Foreground = new SolidColorBrush(palette.Ink) { Opacity = 0.78 },
                };
                foreach (var child in quote)
                    AddBlock(quoteSection.Blocks, child, palette, fontSize);
                blocks.Add(quoteSection);
                break;

            case CodeBlock code:
                var codeParagraph = new Paragraph
                {
                    Margin = new Thickness(0, 2, 0, 8),
                    Padding = new Thickness(9, 7, 9, 7),
                    Background = new SolidColorBrush(palette.Ink) { Opacity = 0.075 },
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = Math.Max(11, fontSize - 1),
                    LineHeight = (fontSize - 1) * 1.45,
                };
                codeParagraph.Inlines.Add(new Run(CodeText(code)));
                blocks.Add(codeParagraph);
                break;

            case ThematicBreakBlock:
                blocks.Add(new Paragraph
                {
                    BorderBrush = new SolidColorBrush(palette.Ink) { Opacity = 0.18 },
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 7, 0, 9),
                });
                break;

            case MdTable table:
                blocks.Add(CreateTable(table, palette, fontSize));
                break;

            case ContainerBlock container:
                foreach (var child in container)
                    AddBlock(blocks, child, palette, fontSize);
                break;

            case LeafBlock leaf:
                var fallback = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
                AddInlines(fallback.Inlines, leaf.Inline?.FirstChild, palette, fontSize);
                if (fallback.Inlines.Count == 0 && leaf.Lines.Count > 0)
                    fallback.Inlines.Add(new Run(LinesText(leaf.Lines)));
                if (fallback.Inlines.Count > 0) blocks.Add(fallback);
                break;
        }
    }

    static ListItem CreateListItem(ListItemBlock item, NoteColor palette, double fontSize)
    {
        var result = new ListItem { Margin = new Thickness(0, 0, 0, 2) };
        bool taskAdded = false;
        foreach (var block in item)
        {
            if (block is ParagraphBlock paragraph && paragraph.Inline?.FirstChild is TaskList task)
            {
                var taskText = new Paragraph { Margin = new Thickness(0, 0, 0, 2), LineHeight = fontSize * 1.5 };
                taskText.Inlines.Add(new Run(task.Checked ? "☑  " : "☐  ")
                {
                    Foreground = palette.DashB,
                    FontWeight = FontWeights.SemiBold,
                });
                AddInlines(taskText.Inlines, task.NextSibling, palette, fontSize);
                result.Blocks.Add(taskText);
                taskAdded = true;
            }
            else AddBlock(result.Blocks, block, palette, fontSize);
        }
        if (!taskAdded && result.Blocks.Count == 0)
            result.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        return result;
    }

    static WpfTable CreateTable(MdTable table, NoteColor palette, double fontSize)
    {
        int columns = table.OfType<MdTableRow>().Select(row => row.Count).DefaultIfEmpty(1).Max();
        var result = new WpfTable
        {
            CellSpacing = 0,
            BorderBrush = new SolidColorBrush(palette.Ink) { Opacity = 0.20 },
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 2, 0, 8),
        };
        for (int i = 0; i < columns; i++) result.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        foreach (var row in table.OfType<MdTableRow>())
        {
            var wpfRow = new WpfTableRow();
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var wpfCell = new WpfTableCell
                {
                    BorderBrush = new SolidColorBrush(palette.Ink) { Opacity = 0.16 },
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(7, 4, 7, 4),
                    Background = row.IsHeader ? new SolidColorBrush(palette.Dash) { Opacity = 0.13 } : Brushes.Transparent,
                    ColumnSpan = Math.Max(1, cell.ColumnSpan),
                    RowSpan = Math.Max(1, cell.RowSpan),
                };
                foreach (var cellBlock in cell)
                    AddBlock(wpfCell.Blocks, cellBlock, palette, fontSize);
                if (wpfCell.Blocks.Count == 0)
                    wpfCell.Blocks.Add(new Paragraph { Margin = new Thickness(0), FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal });
                wpfRow.Cells.Add(wpfCell);
            }
            group.Rows.Add(wpfRow);
        }
        result.RowGroups.Add(group);
        return result;
    }

    static void AddInlines(InlineCollection target, MdInline? current, NoteColor palette, double fontSize)
    {
        while (current != null)
        {
            switch (current)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case CodeInline code:
                    target.Add(new Span(new Run(code.Content.ToString()))
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        Background = new SolidColorBrush(palette.Ink) { Opacity = 0.09 },
                    });
                    break;

                case EmphasisInline emphasis:
                    var styled = new Span();
                    if (emphasis.DelimiterChar == '~') styled.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) styled.FontWeight = FontWeights.SemiBold;
                    else styled.FontStyle = FontStyles.Italic;
                    AddInlines(styled.Inlines, emphasis.FirstChild, palette, fontSize);
                    target.Add(styled);
                    break;

                case LinkInline link when !link.IsImage:
                    var hyperlink = new Hyperlink
                    {
                        Foreground = palette.DashB,
                        TextDecorations = TextDecorations.Underline,
                        Cursor = System.Windows.Input.Cursors.Hand,
                    };
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var linkUri))
                    {
                        hyperlink.NavigateUri = linkUri;
                        hyperlink.RequestNavigate += (_, e) =>
                        {
                            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                            catch { }
                            e.Handled = true;
                        };
                    }
                    AddInlines(hyperlink.Inlines, link.FirstChild, palette, fontSize);
                    if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new Run(link.Url ?? string.Empty));
                    target.Add(hyperlink);
                    break;

                case LinkInline image when image.IsImage:
                    target.Add(new Run(InlineText(image.FirstChild)));
                    break;

                case AutolinkInline autolink:
                    var value = autolink.Url ?? string.Empty;
                    target.Add(new Hyperlink(new Run(value))
                    {
                        NavigateUri = Uri.TryCreate(autolink.IsEmail ? $"mailto:{value}" : value, UriKind.Absolute, out var autoUri) ? autoUri : null,
                        Foreground = palette.DashB,
                        TextDecorations = TextDecorations.Underline,
                    });
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;

                case ContainerInline container:
                    AddInlines(target, container.FirstChild, palette, fontSize);
                    break;
            }
            current = current.NextSibling;
        }
    }

    static string InlineText(MdInline? current)
    {
        var text = new System.Text.StringBuilder();
        while (current != null)
        {
            if (current is LiteralInline literal) text.Append(literal.Content);
            else if (current is CodeInline code) text.Append(code.Content.ToString());
            else if (current is ContainerInline container) text.Append(InlineText(container.FirstChild));
            current = current.NextSibling;
        }
        return text.ToString();
    }

    static string CodeText(CodeBlock block) => LinesText(block.Lines);

    static string LinesText(Markdig.Helpers.StringLineGroup lines)
    {
        var text = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(line.ToString());
        }
        return text.ToString();
    }

    static double HeadingSize(int level, double baseSize)
        => level switch
        {
            1 => baseSize + 8,
            2 => baseSize + 5,
            3 => baseSize + 3,
            _ => baseSize + 1,
        };
}
