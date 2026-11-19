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
    private static IEnumerable<GlyphRun> GlyphRuns(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
        else if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var run in GlyphRuns(child)) yield return run;
    }

    private static GlyphRun[] DrawReadingGlyphs(string text, bool setFour)
    {
        var block = new TextBlock { Text = text, FontFamily = ChatReadingTypography.Family, FontSize = 40 };
        Typography.SetStylisticSet4(block, setFour);
        block.Measure(new Size(900, 120)); block.Arrange(new Rect(0, 0, 900, 120)); block.UpdateLayout();
        return GlyphRuns(VisualTreeHelper.GetDrawing(block)).ToArray();
    }

    private static async Task CheckAiChatReadingFontAsync(MainWindow owner, string output)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "IBMPlexSans-Regular.otf");
        Require(File.Exists(fontPath) && File.Exists(Path.Combine(Path.GetDirectoryName(fontPath)!, "LICENSE-IBM-Plex.txt")), "Plex font or license missing from application output");
        const string semiboldFace = "IBMPlexSans-SemiBold.otf";
        Require(File.Exists(Path.Combine(Path.GetDirectoryName(fontPath)!, semiboldFace)), "Plex semibold font missing from application output");
        var ordinary = DrawReadingGlyphs("Il1 O0 中文", false);
        var dotted = DrawReadingGlyphs("Il1 O0 中文", true);
        var latin = dotted.FirstOrDefault(run => run.GlyphTypeface.FontUri.LocalPath.EndsWith("IBMPlexSans-Regular.otf", StringComparison.OrdinalIgnoreCase));
        var latinOriginal = ordinary.FirstOrDefault(run => run.GlyphTypeface.FontUri.LocalPath.EndsWith("IBMPlexSans-Regular.otf", StringComparison.OrdinalIgnoreCase));
        Require(latin is not null && latinOriginal is not null, "Latin glyphs did not use the packaged IBM Plex Sans Regular");
        Require(latin!.GlyphIndices.Count == latinOriginal!.GlyphIndices.Count &&
            latin.GlyphIndices.Zip(latinOriginal.GlyphIndices).Count(pair => pair.First != pair.Second) == 1,
            "ss04 should change the zero glyph and preserve the other Latin glyphs");
        if (Environment.GetEnvironmentVariable("TEXEED_REQUIRE_HIRAGINO") == "1")
            Require(ChatReadingTypography.HasHiragino, "Required Hiragino W3 was not found at the actual application font path: " + ChatReadingTypography.LocalFontDirectory);
        if (ChatReadingTypography.HasHiragino)
        {
            var chinese = dotted.FirstOrDefault(run => run.GlyphTypeface.FontUri.LocalPath.EndsWith(ChatReadingTypography.HiraginoFile, StringComparison.OrdinalIgnoreCase));
            Require(chinese is not null && chinese.GlyphTypeface.FaceNames.Values.Contains("W3", StringComparer.OrdinalIgnoreCase), "Chinese glyphs did not use Hiragino W3 from the installed local TTC");
        }
        else Console.WriteLine("SKIP Hiragino glyph check: local user font is unavailable; fallback remains enabled.");

        var finished = new MarkdownView { ChatMode = true, Markdown = "# 标题 O0\n\n中文 Il1 O0 `code 0` 与 $x^2$。\n\n| 项目 | O0 |\n|---|---|\n| 中文 | 值 |" };
        var streaming = new MarkdownView { ChatMode = true, IsStreaming = true, Markdown = "中文 Il1 O0" };
        Require(ReferenceEquals(finished.FontFamily, ChatReadingTypography.Family) && ReferenceEquals(streaming.FontFamily, ChatReadingTypography.Family), "Finished and streaming answers use different reading fonts");
        Require(Typography.GetStylisticSet4(finished.Document) && Typography.GetStylisticSet4(streaming.Document), "ss04 is missing from a finished or streaming answer");
        var heading = finished.Document.Blocks.OfType<Paragraph>().First();
        Require(Typography.GetStylisticSet4(heading) && heading.FontFamily == finished.FontFamily, "Heading font or ss04 differs from body");
        var text = finished.Document.Blocks.OfType<Paragraph>().Skip(1).First();
        Require(text.FontSize == 18 && text.FontWeight == FontWeights.Normal && streaming.Document.Blocks.FirstBlock!.FontSize == 18 &&
            Math.Abs(finished.Document.LineHeight - 28.125) < .01 && Math.Abs(streaming.Document.LineHeight - 28.125) < .01,
            "Finished and streaming body text must use 18 DIP at normal weight");
        Require(heading.FontSize == 18 && heading.FontWeight == FontWeights.SemiBold &&
            heading.Inlines.OfType<Run>().First().FontWeight == FontWeights.SemiBold,
            "Heading and hierarchy marker must use 18 DIP at semibold weight");
        Require(text.Inlines.OfType<Span>().Single().FontSize == 14, "Body sizing changed the dedicated code size");
        var nonChat = new MarkdownView { Markdown = "示例" };
        Require(nonChat.FontSize == 16 && !ReferenceEquals(nonChat.FontFamily, ChatReadingTypography.Family) &&
            !Typography.GetStylisticSet4(nonChat.Document), "Non-chat Markdown inherited chat typography");
        var streamingTemplateOrder = new MarkdownView { IsStreaming = true, Markdown = "实时文字 👩🏽‍💻", ChatMode = true };
        streamingTemplateOrder.Markdown += " 继续显示";
        Require(streamingTemplateOrder.CopySource(streamingTemplateOrder.Document.ContentStart, streamingTemplateOrder.Document.ContentEnd).Trim() ==
            "实时文字 👩🏽‍💻 继续显示", "Applying chat typography to a live document lost streamed text");

        using var handler = new FormHandler(); using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(Path.Combine(output, "chat-reading-font.tex"));
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Clear();
        book.Turns.Add(new AiTurn("正文与轮次示例 Il1 O0", "# 阅读区的标题 O0\n\n正文从 16 调整到 18，观察混排 IBM Il1 O0、公式与 `code` 的可读性。\n\n## 保留折叠状态\n\n折叠内容。\n\n## 公式 $x^2$ 与文字混排\n\n$$x^2 + 1$$"));
        var host = new Window { Owner = owner, Width = 900, Height = 700, Left = -3000, Top = -3000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, Content = panel };
        host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var turn = views[book.Turns[0].Id];
            await Task.WhenAll(turn.Answers.Select(answer => answer.RenderTask)); host.UpdateLayout();
            var number = (TextBlock)Field(turn, "_number")!;
            var liveNumber = (TextBlock)panel.FindName("LiveTurnNumber");
            var liveQuestion = (MarkdownView)panel.FindName("LiveQuestion");
            Require(number.FontFamily == ChatReadingTypography.Family && liveNumber.FontFamily == ChatReadingTypography.Family && liveQuestion.FontFamily == ChatReadingTypography.Family, "Turn numbers or live question use another family");
            Require(Typography.GetStylisticSet4(number) && Typography.GetStylisticSet4(liveNumber), "Turn numbers do not inherit ss04");
            Require(number.FontSize == 18 && liveNumber.FontSize == 18 && number.FontWeight == FontWeights.SemiBold &&
                liveNumber.FontWeight == FontWeights.SemiBold && liveQuestion.FontSize == 18 && turn.Questions.Single().FontSize == 18,
                "Live and finished turn numbers must both use 18 DIP semibold and match question size");
            Require(((TextBox)panel.FindName("Composer")).FontSize == 15 && ((TextBlock)panel.FindName("LiveSentTime")).FontSize == 13 &&
                ((TextBlock)Field(turn, "_sentTime")!).FontSize == 13, "Body sizing changed the editor or timestamp size");
            var notice = (TextBlock)panel.FindName("FontFallbackNotice");
            Require(notice.Visibility == (ChatReadingTypography.HasHiragino && ChatReadingTypography.HasPlex ? Visibility.Collapsed : Visibility.Visible), "Font fallback notice does not reflect the actual font availability");
            if (notice.Visibility == Visibility.Visible)
            {
                ((TextBlock)panel.FindName("StatusText")).Text = "网络状态";
                Require(notice.Visibility == Visibility.Visible && ((TextBlock)panel.FindName("StatusText")).Text == "网络状态", "Font fallback notice overwrote another status");
            }
            var formulas = Descendants<ChatFormula>(turn).ToArray();
            Require(formulas.Length > 0, "Rendered TeX formula is missing");
            await Task.WhenAll(formulas.Select(formula => formula.RenderTask)).WaitAsync(TimeSpan.FromSeconds(60));
            Require(formulas.All(formula => formula.Error is null && TextElement.GetFontSize(formula) == 16),
                "TeX formula could not be rendered at its dedicated size");
            var mainSection = (Section)turn.Answers.Single().Document.Blocks.FirstBlock!;
            var bodySection = mainSection.Blocks.OfType<Section>().Single();
            var foldedSection = bodySection.Blocks.OfType<Section>().First();
            var foldedHeading = (Hyperlink)((Paragraph)foldedSection.Blocks.FirstBlock!).Inlines.FirstInline!;
            foldedHeading.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            var hint = (Run)foldedHeading.Inlines.LastInline!;
            Require(hint.Text == "（已折叠）" && Math.Abs(hint.FontSize - 12.8) < .01 && hint.FontWeight == FontWeights.Normal,
                "The folded label must retain its dedicated size and weight");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            var answerGlyphs = GlyphInk(turn.Answers.Single(), turn);
            var headingGlyph = answerGlyphs.First(glyph => glyph.Text.Contains("阅读区的标题"));
            var bodyGlyph = answerGlyphs.First(glyph => glyph.Text.Contains("正文从"));
            var markerGlyph = answerGlyphs.First(glyph => glyph.Text.Contains("[1:1]"));
            var numberGlyph = GlyphInk(number, turn).First(glyph => glyph.Text.Contains("[1]"));
            Require(headingGlyph.EmSize == 18 && bodyGlyph.EmSize == 18 && markerGlyph.EmSize == 18 && numberGlyph.EmSize == 18,
                "WPF did not paint the body, heading and numbers at 18 DIP");
            Require(headingGlyph.Weight > bodyGlyph.Weight || headingGlyph.BoldSimulation && !bodyGlyph.BoldSimulation,
                "WPF did not paint the heading at a heavier weight than body");
            Require(new[] { markerGlyph, numberGlyph }.All(glyph => glyph.Weight == 600 && !glyph.BoldSimulation &&
                glyph.Face.Equals(semiboldFace, StringComparison.OrdinalIgnoreCase)), "WPF did not paint the numbers with bundled Plex semibold");
            Console.WriteLine($"Reading faces: body {bodyGlyph.Face}/{bodyGlyph.Weight}, heading {headingGlyph.Face}/{headingGlyph.Weight}, marker {markerGlyph.Face}/{markerGlyph.Weight}, ordinal {numberGlyph.Face}/{numberGlyph.Weight}");
            var originalTheme = ThemeManager.Current.Id;
            try
            {
                foreach (var theme in new[] { "ristretto", "github-light-default" })
                {
                    ThemeManager.Apply(theme); host.UpdateLayout();
                    Require(turn.Answers.All(answer => ReferenceEquals(answer.FontFamily, ChatReadingTypography.Family)), "Reading font changed with theme");
                    Capture(host, Path.Combine(output, "chat-reading-font-" + theme + ".png"));
                }
                host.Width = 420; host.UpdateLayout();
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Capture(host, Path.Combine(output, "chat-reading-font-narrow.png"));
                var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
                var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                var clipped = new List<int>();
                foreach (var ordinal in new[] { 108, 1024 })
                {
                    book.Turns.AddRange(Enumerable.Range(book.Turns.Count + 1, ordinal - book.Turns.Count)
                        .Select(i => new AiTurn($"第 {i} 轮问题 Il1 O0", "测试宽度。")));
                    Invoke(panel, "RefreshTranscript");
                    scroll.ScrollToEnd(); host.UpdateLayout();
                    await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var last = views[book.Turns[^1].Id];
                    var ordinalBlock = (TextBlock)Field(last, "_number")!;
                    var ink = GlyphInk(ordinalBlock, viewport).Single(glyph => glyph.Text.Contains($"[{ordinal}]"));
                    Console.WriteLine($"Narrow ordinal {ordinal}: rendered horizontal bounds {ink.Bounds.Left:F3}–{ink.Bounds.Right:F3} DIP in a {viewport.ActualWidth:F3}-DIP viewport");
                    Capture(host, Path.Combine(output, $"chat-reading-font-ordinal-{ordinal}.png"));
                    if (ink.Bounds.Left < 0 || ink.Bounds.Right > viewport.ActualWidth) clipped.Add(ordinal);
                }
                Require(clipped.Count == 0, "Narrow ordinals clipped: " + string.Join(", ", clipped));
            }
            finally { ThemeManager.Apply(originalTheme); }
        }
        finally { handler.Release.TrySetResult(); host.Close(); }
        Console.WriteLine("PASS chat reading fonts and hierarchy: 18-DIP body, semibold headings and ordinals, stable labels, editor and code sizes, actual IBM glyphs and ss04.");
    }
}
