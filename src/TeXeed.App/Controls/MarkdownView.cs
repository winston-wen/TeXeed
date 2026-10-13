using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using Block = System.Windows.Documents.Block;

namespace TeXeed.App.Controls;

/// <summary>Selectable native Markdown. No HTML execution or automatic remote resource loading.</summary>
public sealed class MarkdownView : RichTextBox
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseAutoLinks().DisableHtml().Build();
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render()));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    public MarkdownView()
    {
        IsReadOnly = true; IsReadOnlyCaretVisible = false; IsDocumentEnabled = true; IsUndoEnabled = false;
        BorderThickness = new Thickness(0); Background = Brushes.Transparent; Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        FontSize = 15; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(SelectionBrushProperty, "TextSelectionBackground");
        SetResourceReference(SelectionTextBrushProperty, "TextSelectionForeground");
        // RichTextBox always paints selection in an adorner, unlike TextBox/PasswordBox.
        // An opaque brush would cover the glyphs even with SelectionTextBrush set.
        SelectionOpacity = .35;
    }

    private void Render()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), ColumnWidth = double.PositiveInfinity,
            FontSize = FontSize, FontFamily = FontFamily, LineHeight = 25 };
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        AddBlocks(document.Blocks, Markdig.Markdown.Parse(Markdown ?? "", Pipeline), 0);
        Document = document;
    }

    private static void AddBlocks(BlockCollection target, ContainerBlock source, int depth)
    {
        // Bound nesting independently of model output, including malformed streaming fragments.
        if (depth > 32) return;
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var h = Paragraph(heading.Inline, depth);
                    h.FontSize = heading.Level switch { 1 => 24, 2 => 21, 3 => 18, _ => 16 };
                    h.FontWeight = FontWeights.SemiBold; h.Margin = new Thickness(0, 16, 0, 10);
                    target.Add(h); break;
                case ParagraphBlock paragraph:
                    target.Add(Paragraph(paragraph.Inline, depth)); break;
                case CodeBlock code:
                    var p = new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 14, LineHeight = 23, Padding = new Thickness(14), Margin = new Thickness(0, 6, 0, 14) };
                    p.SetResourceReference(TextElement.BackgroundProperty, "PanelBackground");
                    target.Add(p); break;
                case QuoteBlock quote:
                    var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(14, 2, 0, 2), Margin = new Thickness(0, 6, 0, 14) };
                    section.SetResourceReference(Block.BorderBrushProperty, "Accent");
                    AddBlocks(section.Blocks, quote, depth + 1); target.Add(section); break;
                case ListBlock list:
                    var items = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Padding = new Thickness(24, 0, 0, 0), Margin = new Thickness(0, 4, 0, 12) };
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) items.StartIndex = start;
                    foreach (var child in list.OfType<ListItemBlock>())
                    { var item = new ListItem(); AddBlocks(item.Blocks, child, depth + 1); items.ListItems.Add(item); }
                    target.Add(items); break;
                case MdTable table:
                    var result = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 16) };
                    var rows = new TableRowGroup(); result.RowGroups.Add(rows);
                    foreach (var row in table.OfType<MdRow>())
                    {
                        var r = new TableRow(); rows.Rows.Add(r);
                        foreach (var cell in row.OfType<MdCell>())
                        {
                            var c = new TableCell { Padding = new Thickness(9, 6, 9, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
                            c.SetResourceReference(TableCell.BorderBrushProperty, "Guide");
                            if (row.IsHeader) { c.FontWeight = FontWeights.SemiBold; c.SetResourceReference(TextElement.BackgroundProperty, "PanelBackground"); }
                            AddBlocks(c.Blocks, cell, depth + 1); r.Cells.Add(c);
                        }
                    }
                    target.Add(result); break;
                case ThematicBreakBlock:
                    var line = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 12, 0, 16) };
                    line.SetResourceReference(Block.BorderBrushProperty, "Guide"); target.Add(line); break;
                case ContainerBlock container:
                    AddBlocks(target, container, depth + 1); break;
                case LeafBlock leaf:
                    target.Add(Paragraph(leaf.Inline, depth)); break;
            }
        }
    }

    private static Paragraph Paragraph(ContainerInline? inline, int depth)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        if (inline is not null) AddInlines(paragraph.Inlines, inline, depth + 1);
        return paragraph;
    }

    private static void AddInlines(InlineCollection target, ContainerInline source, int depth)
    {
        if (depth > 32) return;
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code:
                    var run = new Run(code.Content) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14 };
                    run.SetResourceReference(TextElement.BackgroundProperty, "PanelBackground"); target.Add(run); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                    else span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis, depth + 1); target.Add(span); break;
                case LinkInline link:
                    if (!link.IsImage && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                    {
                        var hyperlink = Link(uri); AddInlines(hyperlink.Inlines, link, depth + 1); target.Add(hyperlink);
                    }
                    else { if (link.IsImage) target.Add(new Run("[图片] ")); AddInlines(target, link, depth + 1); }
                    break;
                case AutolinkInline auto:
                    if (Uri.TryCreate(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" or "mailto")
                    { var hyperlink = Link(address); hyperlink.Inlines.Add(new Run(auto.Url)); target.Add(hyperlink); }
                    else target.Add(new Run(auto.Url));
                    break;
                case ContainerInline container: AddInlines(target, container, depth + 1); break;
            }
        }
    }

    private static Hyperlink Link(Uri uri)
    {
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { link.ToolTip = "无法打开链接，可复制地址后打开。"; }
            e.Handled = true;
        };
        return link;
    }
}
