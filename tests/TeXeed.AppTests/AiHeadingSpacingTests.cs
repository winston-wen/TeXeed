using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiGraySpacingAsync(MainWindow owner, string output, bool enforce)
    {
        var turn = new AiTurn("合成问题", "合成正文") { SentAt = DateTimeOffset.UnixEpoch, AnsweredAt = DateTimeOffset.UnixEpoch.AddSeconds(5), Segments =
            [new() { Kind = "activity", Text = "已查看项目目录" }, new() { Kind = "activity", Text = "已完成源码检索" }, new() { Kind = "activity", Text = "已读取源码" },
             new() { Kind = "final", Text = "第一行正文。  \n正文末行：系数直接相乘。" }, new() { Kind = "activity", Text = "公式排版检查通过" }] };
        var view = new AiTurnView { TurnNumber = 6, Turn = turn };
        var host = new Window { Owner = owner, Content = view, Width = 760, Height = 520, Title = "展开轮次灰字间距 · 合成资料" };
        host.Show();
        try
        {
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            double[] Baselines(FrameworkElement element)
            {
                var lines = new List<double>();
                foreach (var visual in Descendants<Visual>(element).Prepend(element))
                {
                    void Visit(Drawing drawing, Matrix transform)
                    {
                        if (drawing is DrawingGroup group)
                        {
                            var next = group.Transform?.Value ?? Matrix.Identity; next.Append(transform);
                            foreach (var child in group.Children) Visit(child, next);
                        }
                        else if (drawing is GlyphRunDrawing glyph)
                            lines.Add(visual.TransformToAncestor(view).Transform(transform.Transform(glyph.GlyphRun.BaselineOrigin)).Y);
                    }
                    if (VisualTreeHelper.GetDrawing(visual) is { } drawing) Visit(drawing, Matrix.Identity);
                }
                return lines.Order().ToArray();
            }
            var markdown = Descendants<MarkdownView>(view).ToArray();
            var sent = Descendants<TextBlock>(view).Single(t => t.Text == turn.SentTime);
            var rows = Descendants<AiActivityView>(view).Select(r => (TextBlock)r.Header).ToArray();
            var reply = Descendants<AiReplyStatus>(view).Single();
            var question = Baselines(markdown.Single(m => !m.ChatMode)); var body = Baselines(markdown.Single(m => m.ChatMode));
            var gaps = new Dictionary<string, double> {
                ["question_to_sent"] = Baselines(sent).Min() - question.Max(),
                ["sent_to_activity"] = Baselines(rows[0]).Min() - Baselines(sent).Max(),
                ["activity_to_activity"] = Baselines(rows[1]).Min() - Baselines(rows[0]).Max(),
                ["activity_to_body"] = body.Min() - Baselines(rows[2]).Max(),
                ["body_to_check"] = Baselines(rows[3]).Min() - body.Max(),
                ["check_to_reply"] = Baselines(reply).Min() - Baselines(rows[3]).Max() };
            File.WriteAllText(Path.Combine(output, "gray-spacing.json"), JsonSerializer.Serialize(gaps));
            Console.WriteLine("Expanded gray glyph baseline gaps: " + JsonSerializer.Serialize(gaps));
            if (enforce && gaps.Where(g => g.Key != "activity_to_body").Any(g => Math.Abs(g.Value - gaps["body_to_check"]) > .5))
                throw new Exception("Expanded metadata glyph intervals diverge.");
            if (enforce && Math.Abs(gaps["activity_to_body"] - gaps["body_to_check"] - 17) > .5)
                throw new Exception("The answer divider does not have the expected height and margins.");
            Capture(host, Path.Combine(output, "gray-spacing.png"));
            var linked = turn with { Prompt = "", SentAnnotations = [new() { Number = 1, Question = "合成圈点说明" }] };
            view.Turn = linked; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var linkedSent = Descendants<TextBlock>(view).Single(t => t.Text == linked.SentTime);
            var lastQuestion = Descendants<MarkdownView>(view).Where(v => !v.ChatMode).SelectMany(Baselines).Max();
            var linkedGap = Baselines(linkedSent).Min() - lastQuestion;
            Console.WriteLine($"Annotation-only question to sent: {linkedGap:F3}");
            if (enforce && Math.Abs(linkedGap - gaps["body_to_check"]) > .5) throw new Exception("Annotation-only question has different metadata spacing.");
            var hanging = Descendants<TextBlock>(view).Single(t => t.Text == "[6]");
            var questionInk = Descendants<MarkdownView>(view).Where(v => !v.ChatMode).SelectMany(v => GlyphInk(v, view)).ToArray();
            var firstLine = questionInk.Where(i => Math.Abs(i.Baseline - questionInk.Min(g => g.Baseline)) < 1).Select(i => i.Bounds).Aggregate(Rect.Empty, (a, r) => { a.Union(r); return a; });
            var ordinalInk = GlyphInk(hanging, view).Select(i => i.Bounds).Aggregate(Rect.Empty, (a, r) => { a.Union(r); return a; });
            if (enforce && Math.Abs(firstLine.Top + firstLine.Height / 2 - ordinalInk.Top - ordinalInk.Height / 2) > .5) throw new Exception("Annotation-only question ordinal ink axis differs from its first line.");
            Capture(host, Path.Combine(output, "gray-annotation-spacing.png"));
        }
        finally { host.Close(); }
    }
    private static async Task CheckAiHeadingSpacingAsync(MainWindow owner, string output, bool enforce)
    {
        var view = new MarkdownView { ChatMode = true, TurnNumber = 7, FontSize = AiChatTypography.FontSize };
        var host = new Window { Owner = owner, Content = new ScrollViewer { Content = view }, Width = 760, Height = 540, Title = "标题与正文行距 · 合成资料" };
        var results = new List<object>();
        IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(b => b is Paragraph p ? [p] : b is Section s ? Paragraphs(s.Blocks) : []);
        IEnumerable<Run> Runs(InlineCollection inlines) => inlines.SelectMany(i => i is Run r ? [r] : i is Span s ? Runs(s.Inlines) : []);
        double Y(Paragraph p, string text) => Runs(p.Inlines).First(r => r.Text.Contains(text)).ContentStart.GetCharacterRect(LogicalDirection.Forward).Y;
        host.Show();
        try
        {
            foreach (var folded in new[] { false, true })
            foreach (var level in Enumerable.Range(1, 6))
            {
                if (folded) view.ConfigureFolding(new(), "spacing", () => { });
                view.Markdown = new string('#', level) + " 标题示例：用统一的行距阅读\n\n正文第一行，查看 `example.tex:12` 中的定义。  \n正文第二行，继续解释这项设置。  \n正文第三行，保持相同的阅读节奏。\n\n正文下一段，仍以既有段落间距分组。";
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var paragraphs = Paragraphs(view.Document.Blocks).ToArray();
                var heading = paragraphs[0]; var body = paragraphs[1];
                var titleToBody = Y(body, "正文第一行") - Y(heading, "标题示例");
                var bodyLine = Y(body, "正文第二行") - Y(body, "正文第一行");
                var bodyParagraph = Y(paragraphs[2], "正文下一段") - Y(body, "正文第三行");
                results.Add(new { level, folded, titleToBody, bodyLine, bodyParagraph });
                if (enforce && Math.Abs(titleToBody - bodyParagraph) > .5)
                    throw new Exception($"h{level} folded={folded}: heading/body spacing {titleToBody:F3} differs from body paragraphs {bodyParagraph:F3} DIP");
                if (folded)
                {
                    var toggle = (Hyperlink)heading.Inlines.FirstInline!;
                    toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                    toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
                    if (enforce && Math.Abs(Y(body, "正文第一行") - Y(heading, "标题示例") - bodyParagraph) > .5)
                        throw new Exception("Reopening a heading changed its paragraph spacing.");
                }
            }
            File.WriteAllText(Path.Combine(output, "heading-spacing.json"), JsonSerializer.Serialize(results));
            Capture(host, Path.Combine(output, "heading-spacing.png"));
            Console.WriteLine("Heading spacing: " + JsonSerializer.Serialize(results));
            view.Markdown = "第一段。  \n第一段第二行。\n\n第二段。\n\n$$\\frac{100+4}{8}=13$$\n\n公式之后。\n\n- 列表第一项\n- 列表第二项\n\n列表之后。";
            host.UpdateLayout(); await AwaitMathAsync(view); host.UpdateLayout();
            var rendered = Paragraphs(view.Document.Blocks).ToArray();
            var list = view.Document.Blocks.OfType<System.Windows.Documents.List>().Single();
            var itemParagraphs = list.ListItems.Select(i => (Paragraph)i.Blocks.FirstBlock!).ToArray();
            var firstLine = Y(rendered[0], "第一段。") ;
            var lineGap = Y(rendered[0], "第一段第二行") - firstLine;
            var paragraphGap = Y(rendered[1], "第二段") - Y(rendered[0], "第一段第二行");
            var itemGap = Y(itemParagraphs[1], "列表第二项") - Y(itemParagraphs[0], "列表第一项");
            var formulaParagraph = rendered[2];
            var formulaTop = formulaParagraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var formulaEnd = formulaParagraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            var beforeFormula = Y(rendered[1], "第二段");
            var afterFormula = Y(rendered[3], "公式之后");
            var beforeRect = rendered[1].ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var afterRect = rendered[3].ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var formula = Descendants<ChatFormula>(view).Single();
            var formulaOrigin = formula.TranslatePoint(new Point(0, 0), view);
            var bodyWhitespace = paragraphGap - beforeRect.Height;
            var formulaBeforeWhitespace = formulaOrigin.Y - beforeRect.Bottom;
            var formulaAfterWhitespace = afterRect.Top - formulaOrigin.Y - formula.RenderSize.Height;
            var listBeforeGap = Y(itemParagraphs[0], "列表第一项") - Y(rendered[3], "公式之后");
            var listAfterGap = Y(rendered[4], "列表之后") - Y(itemParagraphs[1], "列表第二项");
            Console.WriteLine("Paragraph/formula/list spacing: " + JsonSerializer.Serialize(new { lineGap, paragraphGap, itemGap, listBeforeGap, listAfterGap, bodyWhitespace, formulaBeforeWhitespace, formulaAfterWhitespace, beforeFormula, formulaTop, formulaEnd, afterFormula }));
            if (enforce && (Math.Abs(itemGap - lineGap) > .5 || Math.Abs(listBeforeGap - paragraphGap) > .5 || Math.Abs(listAfterGap - paragraphGap) > .5 ||
                Math.Abs(formulaBeforeWhitespace - bodyWhitespace) > 6 || Math.Abs(formulaAfterWhitespace - bodyWhitespace) > 6 ||
                formulaParagraph.Margin.Top != 0 || Math.Abs(formulaParagraph.Margin.Bottom - (paragraphGap - lineGap)) > .5 || formulaParagraph.Padding.Top != 0 || formulaParagraph.Padding.Bottom != 0))
                throw new Exception("Formula or list spacing differs from the paragraph spacing policy.");
            Capture(host, Path.Combine(output, "paragraph-formula-list-spacing.png"));
            view.Markdown = "## 标题与公式\n\n正文含有 $\\frac{a}{\\frac{b}{c}}$，后续仍须清晰。  \n下一行正文不能与公式重叠。";
            host.UpdateLayout(); await AwaitMathAsync(view); host.UpdateLayout();
            Capture(host, Path.Combine(output, "heading-inline-math.png"));
        }
        finally { host.Close(); }
    }
}
