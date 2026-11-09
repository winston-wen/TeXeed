using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckMarkdownCodeAsync(MainWindow window, string output)
    {
        const string source = "## 章节与文字\n用 `\\section{标题}` 创建章节，`\\subsection{小标题}` 创建小节。\n\n`\\textbf{文字}` 表示粗体，`\\emph{文字}` 表示强调。\n\n```tex\n\\documentclass{ctexart}\n\\begin{document}\n\\section{你好，LaTeX}\n这是我的第一份文档。\n\\[ E = mc^2 \\]\n\\end{document}\n```\n\n后面的 `main.tex:12` 仍然可以打开源码。";
        var ai = new MarkdownView { Markdown = source, Margin = new Thickness(24) };
        var tutorial = new MarkdownView { UseSystemFont = true, FontFamily = SystemFonts.MessageFontFamily, Markdown = source, Margin = new Thickness(24) };
        var stack = new StackPanel(); stack.Children.Add(ai); stack.Children.Add(tutorial);
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var host = new Window { Owner = window, Content = scroll, Width = 820, Height = 1000, Title = "代码排版 · AI / 教程" };
        host.SetResourceReference(Window.BackgroundProperty, "EditorBackground"); host.Show();
        var checks = 0; void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        async Task Settle() { host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); host.UpdateLayout(); }
        IEnumerable<Run> Runs(InlineCollection inlines) => inlines.SelectMany(inline => inline is Run run ? new[] { run } : inline is Span span ? Runs(span.Inlines) : []);
        try
        {
            foreach (var theme in new[] { "ristretto", "github-light-default" })
            {
                ThemeManager.Apply(theme); await Settle(); Capture(host, Path.Combine(output, "markdown-code-" + theme + ".png"));
                foreach (var view in new[] { ai, tutorial })
                {
                    var paragraphs = view.Document.Blocks.OfType<Paragraph>().ToArray();
                    var code = paragraphs.SelectMany(p => Runs(p.Inlines)).First(run => run.Text == @"\section{标题}");
                    Check(code.FontFamily.Source.Contains("Consolas"), "Inline code inherited the prose/system font");
                    var dpi = VisualTreeHelper.GetDpi(view).PixelsPerDip;
                    double Width(string text) => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(code.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), code.FontSize, Brushes.Black, dpi).Width;
                    Check(Math.Abs(Width("iiiiiiii") - Width("WWWWWWWW")) < .01, "Code font is not monospaced");
                    Check(((SolidColorBrush)code.Foreground).Color == ThemeManager.Brush("Ink").Color, "Code foreground color changed");
                    var surfaces = (List<Rect>)Field(view, "_inlineCodeBounds")!;
                    var glyph = code.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                    Check(surfaces.Count == 5 && surfaces.All(rect => Math.Abs(rect.Height - 25) < .1), "Inline backgrounds do not fill the existing 25-DIP line height");
                    var bounds = surfaces[0];
                    var expectedPadding = (25 - glyph.Height) / 2;
                    Check(Math.Abs(glyph.X - bounds.X - expectedPadding) < .1 && Math.Abs(glyph.Y - bounds.Y - expectedPadding) < .1 && Math.Abs(bounds.Height - glyph.Height - 2 * expectedPadding) < .1, "Inline code padding is not equal on all sides");
                    var padding = ((Span)code.Parent).Inlines.FirstInline!;
                    Check(Math.Abs(code.ContentStart.GetCharacterRect(LogicalDirection.Forward).X - padding.ContentStart.GetCharacterRect(LogicalDirection.Forward).X - expectedPadding) < .1, "Inline padding has no actual layout space");
                    var blocks = (List<(Paragraph Paragraph, RectangleGeometry Shape)>)Field(view, "_blockCodeSurfaces")!;
                    Check(blocks.Count == 1 && blocks[0].Paragraph.FontFamily.Source.Contains("Consolas") && blocks[0].Shape.RadiusX == 8 && blocks[0].Shape.RadiusY == 8, "Fenced code lacks a monospaced font or rounded background");
                    var copied = view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd);
                    Check(!copied.Contains('\u2002') && copied.Contains("\\section{标题} 创建章节") && copied.Contains("\\[ E = mc^2 \\]"), "Code decoration changed copied text or interpreted TeX");
                    Check(view.CopyForClipboard(code.ContentStart.GetPositionAtOffset(1)!, code.ContentStart.GetPositionAtOffset(8)!) == code.Text[1..8], "Code lost native partial selection/copy");
                    var canonical = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
                    var pointer = view.AnnotationPointer("code-anchor", new AiAnswerAnchor("turn", "answer", 0, 4, "创建小节", "revision") { TextOffset = canonical.IndexOf("创建小节", StringComparison.Ordinal) });
                    Check(pointer is not null && new TextRange(pointer, pointer.GetPositionAtOffset(4)!).Text == "创建小节", "Code padding shifted canonical annotation offsets");
                }
            }
            ai.SourceLink = _ => { }; ai.Markdown = "查看 `main.tex:12`。"; await Settle();
            var link = ((Paragraph)ai.Document.Blocks.FirstBlock!).Inlines.OfType<Span>().Single().Inlines.OfType<Hyperlink>().Single();
            Check(link.FontFamily.Source.Contains("Consolas") && ((SolidColorBrush)link.Foreground).Color == ThemeManager.Brush("Accent").Color, "Code source link lost its monospaced font or existing link color");
            var oldFile = new Run("main.tex:12"); var oldFileDocument = new FlowDocument(new Paragraph(new Hyperlink(oldFile)));
            ai.Markdown = "`main.tex:12`"; await Settle();
            var legacyFileAnchor = new AiAnswerAnchor("turn", "answer", oldFileDocument.ContentStart.GetOffsetToPosition(oldFile.ContentStart.GetPositionAtOffset(2)!), 2, "in", "revision");
            var filePointer = ai.AnnotationPointer("legacy-file-link", legacyFileAnchor);
            Check(filePointer is not null && new TextRange(filePointer, filePointer.GetPositionAtOffset(2)!).Text == "in", "Moving decorative padding changed a legacy file-link anchor");
            Check(ai.CopyForClipboard(ai.Document.ContentStart, ai.Document.ContentEnd).TrimEnd('\r', '\n') == "main.tex:12", "File-link padding leaked into copied text");
            tutorial.Markdown = "正文 `" + string.Join(" ", Enumerable.Repeat("long_code_identifier", 18)) + "` 后文。"; host.Width = 360; await Settle();
            Check(((List<Rect>)Field(tutorial, "_inlineCodeBounds")!).Count > 1, "Long inline code did not follow native wrapping");
            Capture(host, Path.Combine(output, "markdown-code-narrow.png"));
            var legacyCode = new Run("inline"); var legacyTail = new Run("后文");
            var legacyParagraph = new Paragraph(); legacyParagraph.Inlines.Add(new Run("前")); legacyParagraph.Inlines.Add(legacyCode); legacyParagraph.Inlines.Add(legacyTail);
            var legacyDocument = new FlowDocument(legacyParagraph);
            ai.Markdown = "前`inline`后文"; await Settle();
            foreach (var original in new[] { legacyCode.ContentStart.GetPositionAtOffset(2)!, legacyTail.ContentStart })
            {
                var anchor = new AiAnswerAnchor("turn", "answer", legacyDocument.ContentStart.GetOffsetToPosition(original), 2, "quote", "revision");
                var restored = ai.AnnotationPointer("legacy-" + anchor.Offset, anchor);
                Check(restored is not null && new TextRange(restored, restored.GetPositionAtOffset(2)!).Text == new TextRange(original, original.GetPositionAtOffset(2)!).Text, "Decorative code spans shifted a legacy raw-symbol annotation");
            }
            ai.ConfigureFolding(new Dictionary<string, bool>(), "code-folds", () => { }); ai.Markdown = "## 标题\n\n`code`\n\n```tex\ncode block\n```"; await Settle();
            var heading = (Paragraph)((Section)ai.Document.Blocks.FirstBlock!).Blocks.FirstBlock!;
            var toggle = heading.Inlines.OfType<Hyperlink>().Single(); toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); await Settle();
            Check(((List<Rect>)Field(ai, "_inlineCodeBounds")!).Count == 0, "Folded code left a painted background behind");
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); await Settle();
            Check(((List<Rect>)Field(ai, "_inlineCodeBounds")!).Count == 1, "Reopening a heading did not restore code decoration");
            Console.WriteLine($"PASS: Markdown code {checks} checks: both palettes and prose fonts, native selection/copy, equal inline padding, line height, rounded blocks, code links, wrapping and stable annotation offsets.");
        }
        finally { host.Close(); ThemeManager.Apply("ristretto"); }
    }
}
