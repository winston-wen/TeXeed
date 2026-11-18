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
        var nonChat = new MarkdownView { Markdown = "示例" };
        Require(!ReferenceEquals(nonChat.FontFamily, ChatReadingTypography.Family) && !Typography.GetStylisticSet4(nonChat.Document), "Non-chat Markdown inherited chat typography");

        using var handler = new FormHandler(); using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(Path.Combine(output, "chat-reading-font.tex"));
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Add(new AiTurn("中文 Il1 O0？", "# 中文标题 O0\n\n正文 IBM Il1 O0。\n\n$$x^2 + 1$$"));
        var host = new Window { Owner = owner, Width = 900, Height = 700, Left = -3000, Top = -3000, ShowActivated = false, ShowInTaskbar = false, Content = panel };
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
            Require(formulas.All(formula => formula.Error is null), "TeX formula could not be rendered");
            var originalTheme = ThemeManager.Current.Id;
            try
            {
                foreach (var theme in new[] { "ristretto", "github-light-default" })
                {
                    ThemeManager.Apply(theme); host.UpdateLayout();
                    Require(turn.Answers.All(answer => ReferenceEquals(answer.FontFamily, ChatReadingTypography.Family)), "Reading font changed with theme");
                    Capture(host, Path.Combine(output, "chat-reading-font-" + theme + ".png"));
                }
            }
            finally { ThemeManager.Apply(originalTheme); }
        }
        finally { handler.Release.TrySetResult(); host.Close(); }
        Console.WriteLine("PASS chat reading fonts: actual IBM glyphs and ss04, Hiragino W3 when available, streaming, headings and turn numbers.");
    }
}
