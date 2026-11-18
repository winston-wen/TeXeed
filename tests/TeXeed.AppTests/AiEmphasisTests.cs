using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Syntax;
using MdEmphasisInline = Markdig.Syntax.Inlines.EmphasisInline;
using MdLinkInline = Markdig.Syntax.Inlines.LinkInline;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private sealed class CountedSourceSpans(SourceSpan[] spans) : IReadOnlyList<SourceSpan>
    {
        public int Reads { get; private set; }
        public int Count => spans.Length;
        public SourceSpan this[int index] { get { Reads++; return spans[index]; } }
        public IEnumerator<SourceSpan> GetEnumerator() => ((IEnumerable<SourceSpan>)spans).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task CheckAiEmphasisAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        IEnumerable<Inline> InlineTree(InlineCollection items) => items.SelectMany(i => i is Span span ? new[] { i }.Concat(InlineTree(span.Inlines)) : [i]);
        Span[] Strong(MarkdownView view) => view.Document.Blocks.OfType<Paragraph>().SelectMany(p => InlineTree(p.Inlines)).OfType<Span>()
            .Where(s => ReferenceEquals(s.Tag, MarkdownView.StrongEmphasisMarker)).ToArray();
        var view = new MarkdownView { ChatMode = true, Padding = new Thickness(0), Markdown = "普通文字与 **主题强调**。\n\n**出处也正在这里： **项目附带论文，查看 [**链接内强调**](https://example.com) 或 **[强调中的链接](https://example.com)**。\n\n**公式 $a+b$ 后的重点**。\n\n1. 列表内 **重点内容**\n2. 普通列表文字" };
        var host = new Window { Owner = owner, Width = 600, Height = 420, Content = view, Title = "强调颜色验证" };
        if (!owner.ShowActivated) { host.ShowActivated = false; host.WindowStartupLocation = WindowStartupLocation.Manual; host.Left = owner.Left; host.Top = owner.Top; }
        host.Show();
        try
        {
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            await AwaitMathAsync(view); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var strong = Strong(view);
            Check(strong.Length >= 5, "Strong semantic spans were lost");
            Check(strong.All(s => s.FontWeight == FontWeights.Normal), "Emphasis changed font weight");
            var plainStrong = strong.Single(s => new TextRange(s.ContentStart, s.ContentEnd).Text == "主题强调");
            Check(((SolidColorBrush)plainStrong.Foreground).Color == ThemeManager.Brush("ChatEmphasis").Color, "Dark theme emphasis color is wrong");
            Check(plainStrong.TextDecorations is null or { Count: 0 }, "Emphasis still has an underline");
            var links = view.Document.Blocks.OfType<Paragraph>().SelectMany(p => InlineTree(p.Inlines)).OfType<Hyperlink>().ToArray();
            Check(links.Length == 2 && links.All(l => ((SolidColorBrush)l.Foreground).Color == ThemeManager.Brush("Accent").Color), "Links lost their existing theme color");
            var innerStrong = links.SelectMany(l => InlineTree(l.Inlines)).OfType<Span>().First(s => ReferenceEquals(s.Tag, MarkdownView.StrongEmphasisMarker));
            Check(((SolidColorBrush)innerStrong.Foreground).Color == ThemeManager.Brush("Accent").Color, "Emphasis inside a link changed its link color");
            Check(Descendants<ChatFormula>(view).Any(f => f.Source == "a+b"), "Inline formula was lost inside emphasis");
            var copied = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            Check(copied.Contains("主题强调") && copied.Contains("链接内强调") && copied.Contains("$a+b$"), "Copying lost text or formula source");
            Capture(host, Path.Combine(output, "ai-emphasis-dark.png"));
            ThemeManager.Apply("github-light-default"); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(((SolidColorBrush)plainStrong.Foreground).Color == ThemeManager.Brush("ChatEmphasis").Color, "Emphasis did not follow theme changes");
            Check(((SolidColorBrush)innerStrong.Foreground).Color == ThemeManager.Brush("Accent").Color, "Link color did not follow theme changes");
            Capture(host, Path.Combine(output, "ai-emphasis-light.png"));
            var themesPath = ThemeManager.DirectoryPath; Directory.CreateDirectory(themesPath);
            var customLight = Path.Combine(themesPath, "emphasis-test-light.json");
            var customDark = Path.Combine(themesPath, "emphasis-test-dark.json");
            try
            {
                File.WriteAllText(customLight, "{\"id\":\"emphasis-test-light\",\"name\":\"Test light\",\"isDark\":false,\"colors\":{}}");
                File.WriteAllText(customDark, "{\"id\":\"emphasis-test-dark\",\"name\":\"Test dark\",\"isDark\":true,\"colors\":{}}");
                Check(ThemeManager.Load("emphasis-test-light").Colors["ChatEmphasis"] == "#006B66", "Light custom theme did not receive a readable fallback");
                Check(ThemeManager.Load("emphasis-test-dark").Colors["ChatEmphasis"] == "#55C7EE", "Dark custom theme did not receive the cyan fallback");
                File.WriteAllText(customDark, "{\"id\":\"emphasis-test-dark\",\"name\":\"Test dark\",\"isDark\":true,\"colors\":{\"ChatEmphasis\":\"#123456\"}}");
                Check(ThemeManager.Load("emphasis-test-dark").Colors["ChatEmphasis"] == "#123456", "Explicit custom theme emphasis color was overwritten");
            }
            finally { File.Delete(customLight); File.Delete(customDark); }
            ThemeManager.Apply("ristretto");
            const string cjkSource = "**出处也正在这里：**项目附带论文。\n\n前文**括号（测试）。**后文。\n\n**公式 $x+y$：**后文。\n\n**[链接](https://example.com)：**后文。\n\n代码 `**原样：**文字` 与转义 \\*\\*原样\\*\\*。";
            var cjkView = new MarkdownView { ChatMode = true, Markdown = cjkSource };
            var cjkInlines = cjkView.Document.Blocks.OfType<Paragraph>().SelectMany(p => InlineTree(p.Inlines)).ToArray();
            var cjkStrong = cjkInlines.OfType<Span>().Where(s => ReferenceEquals(s.Tag, MarkdownView.StrongEmphasisMarker)).ToArray();
            Check(cjkStrong.Length == 4, "CJK punctuation, formula or link broke strong emphasis");
            Check(cjkStrong.Any(s => new TextRange(s.ContentStart, s.ContentEnd).Text.Contains("出处也正在这里：")), "CJK closing punctuation was not emphasized");
            Check(cjkStrong.Any(s => InlineTree(s.Inlines).OfType<Hyperlink>().Any()), "CJK emphasis lost its nested link");
            var cjkCopied = cjkView.CopySource(cjkView.Document.ContentStart, cjkView.Document.ContentEnd);
            Check(cjkCopied.Contains("**原样：**文字") && cjkCopied.Contains("**原样**"), "Code or escaped delimiters were changed");
            cjkView.IsStreaming = true; cjkView.Markdown = cjkSource; cjkView.IsStreaming = false;
            Check(cjkView.Document.Blocks.OfType<Paragraph>().SelectMany(p => InlineTree(p.Inlines)).OfType<Span>()
                .Count(s => ReferenceEquals(s.Tag, MarkdownView.StrongEmphasisMarker)) == 4, "Completed streaming answer did not use the same emphasis grammar");
            const string spacedSource = "**出处也正在这里： **项目。\n\n**公式 $x+y$： **后文。\n\n$x$**重要： **后文。\n\n**重要**$x$。\n\n`**代码： **文字` 和 \\*\\*转义： **文字。\n\n**无标点 **后文，以及 **未闭合。";
            var spacedView = new MarkdownView { ChatMode = true, Markdown = spacedSource };
            var spacedInlines = spacedView.Document.Blocks.OfType<Paragraph>().SelectMany(p => InlineTree(p.Inlines)).ToArray();
            var spacedStrong = spacedInlines.OfType<Span>().Where(s => ReferenceEquals(s.Tag, MarkdownView.StrongEmphasisMarker)).ToArray();
            Check(spacedStrong.Length == 4, "Spaced closing delimiter or adjacent math changed emphasis semantics");
            var spacedCopy = spacedView.CopySource(spacedView.Document.ContentStart, spacedView.Document.ContentEnd);
            Check(spacedCopy.Contains("出处也正在这里： 项目") && spacedCopy.Contains("**代码： **文字") && spacedCopy.Contains("**无标点 **后文") && !spacedCopy.Contains('\uE000'), "Spaced emphasis changed copied text or leaked its parser sentinel");
            Check(MarkdownView.BookmarkFragments("# **标题： **后文").Single().Description.Contains("标题： 后文"), "Spaced emphasis changed a heading bookmark");
            var privateUse = new MarkdownView { ChatMode = true, Markdown = "私用字符 \uE000 保留" };
            Check(privateUse.CopySource(privateUse.Document.ContentStart, privateUse.Document.ContentEnd).Contains('\uE000'), "An original private-use character was changed");
            const string formSource = "**填写 {{text:answer}}： **后文。";
            var formTurn = AiForms.WithSubmission(new AiTurn("练习", formSource), null);
            Check(AiForms.Snapshot(formTurn)?.Values.ContainsKey("text:answer") == true, "Spaced emphasis hid a form field from submission");
            var formView = new MarkdownView { ChatMode = true };
            formView.ConfigureForms(new AiFormContext("turn", (_, _) => "", (_, _, _) => { }, () => false));
            formView.Markdown = formSource;
            Check(((List<Control>)Field(formView, "_formControls")!).OfType<TextBox>().Any(), "Spaced emphasis hid a rendered form field");
            Check(!formView.CopySource(formView.Document.ContentStart, formView.Document.ContentEnd).Contains('\uE000'), "Spaced form copy leaked parser sentinel");
            var tableView = new MarkdownView { ChatMode = true, Markdown = "| 标题 |\n| --- |\n| **表格： **后文 |" };
            host.Content = tableView; host.UpdateLayout();
            var tableText = Descendants<MarkdownView>(tableView).Select(v => v.CopySource(v.Document.ContentStart, v.Document.ContentEnd)).ToArray();
            Check(tableText.Any(t => t.Contains("表格： 后文")) && tableText.All(t => !t.Contains('\uE000')), "Spaced table emphasis lost text or leaked parser sentinel");
            var spans = new CountedSourceSpans(Enumerable.Range(0, 4096).Select(i => new SourceSpan(i * 4, i * 4 + 1)).ToArray());
            Check(ChatMarkdown.LiteralAt(spans, 4095 * 4) && !ChatMarkdown.LiteralAt(spans, 4095 * 4 + 1) && spans.Reads <= 26,
                "Literal boundary lookup did not preserve two-character bounds with logarithmic indexing");
            const int scaleCount = 512;
            var scaleSource = string.Join(" ", Enumerable.Repeat("**标准** **重点： **后文 [链接](https://example.com)", scaleCount));
            var scalePipeline = new MarkdownPipelineBuilder().UseCustomContainers().UsePipeTables().UseEmphasisExtras().UseCjkFriendlyEmphasis().UseAutoLinks().DisableHtml().Build();
            var scaleDocument = ChatMarkdown.Parse(scaleSource, scalePipeline, out var scaleRepaired);
            var scaleInline = scaleDocument.Descendants<LeafBlock>().Where(b => b.Inline is not null).SelectMany(b => b.Inline!.Descendants()).ToArray();
            Check(scaleRepaired && scaleInline.OfType<MdEmphasisInline>().Count(e => e.DelimiterCount == 2) == scaleCount * 2 &&
                scaleInline.OfType<MdLinkInline>().Count() == scaleCount && scaleDocument.Span.End == scaleSource.Length - 1,
                "Large mixed emphasis/link paragraph lost structure or source extent");
            var question = new MarkdownView { UseChatEmphasis = true, Markdown = "用户的 **强调内容**" };
            var questionSpan = Strong(question).Single();
            Check(((SolidColorBrush)questionSpan.Foreground).Color == ThemeManager.Brush("ChatEmphasis").Color && questionSpan.TextDecorations is null or { Count: 0 }, "Question did not use theme-colored emphasis");
            var generic = new MarkdownView { Markdown = "其他页面的 **强调内容**" };
            var genericSpan = ((Paragraph)generic.Document.Blocks.FirstBlock!).Inlines.OfType<Span>().Single();
            Check(genericSpan.FontWeight == FontWeights.Bold, "AI-specific emphasis leaked into other views");
            var live = new MarkdownView { ChatMode = true, IsStreaming = true, Markdown = "正在生成 **重要内容**" };
            Check(new TextRange(live.Document.ContentStart, live.Document.ContentEnd).Text.Contains("**重要内容**"), "Streaming source was not visible");
            live.IsStreaming = false;
            Check(Strong(live).Length == 1 && ((SolidColorBrush)Strong(live)[0].Foreground).Color == ThemeManager.Brush("ChatEmphasis").Color, "Completed response did not render theme emphasis");
            Console.WriteLine($"AI theme emphasis: {checks} checks passed (themes, links, formulas, streaming, source and parser).");
        }
        finally { ThemeManager.Apply("ristretto"); host.Close(); }
    }
}
