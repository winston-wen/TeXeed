using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckAiInlineTypographyAsync(MainWindow owner, string output, bool realTex, bool measureOnly)
    {
        const string equality = "=";
        const string tallEquality = @"\vphantom{\dfrac{A^A}{B_B}}=";
        const string inline = @"\operatorname{Key}(510)=0";
        const string display = "E=mc^2";
        const string indexed = "b'_{1-x}=r-b'_x";
        const string sent = "B=b'_0";
        const string fraction = @"\frac{a}{b}";
        const string transpose = @"(b'_d)^{\mathsf T}(v\bmod p)";
        (string Name, string Source, bool Display)[] fixtures =
        [
            ("equality", equality, false), ("tall-equality", tallEquality, false),
            ("inline-key", inline, false), ("display-energy", display, true),
            ("capital", "A", false), ("lowercase", "r", false), ("indexed", indexed, false),
            ("sent", sent, false), ("fraction", fraction, false), ("transpose", transpose, false)
        ];
        var mathHome = Path.Combine(AiStorage.Home, "math-svg");
        if (!realTex)
        {
            Directory.CreateDirectory(mathHome);
            foreach (var (name, source, displayStyle) in fixtures)
            {
                using var svg = typeof(Program).Assembly.GetManifestResourceStream($"TeXeed.AppTests.Fixtures.inline-axis.{name}.svg")
                    ?? throw new InvalidOperationException("Missing TeX fixture: " + name);
                using var destination = File.Create(Path.Combine(mathHome, ChatTexRenderer.CacheFileName(source, displayStyle)));
                svg.CopyTo(destination);
            }
        }

        var markdown = $"# 标题样例 ${equality}$ 标题来源\n\n正文样例 ${tallEquality}$ 正文来源。" +
            $"\n\n双方都知道公共矩阵 $A$ 和公共向量 $r$。\n\n接收方采样 ${indexed}$。" +
            $"\n\n发送方计算 ${sent}$，分数为 ${fraction}$。\n\n现在回看式 (14)：${transpose}$ 就是对应分量的乘积。" +
            $"\n\nThe answer is ${inline}$。\n\n$$\n{display}\n$$";
        const string excerpt = "引文中的向量 $r$。";
        var entry = new AiEntry { AnswerAnchor = new AiAnswerAnchor("source", "segment", 0, excerpt.Length, excerpt, "revision") };
        var turn = new AiTurn("混排样例 Il1 O0", markdown) { Prompt = "混排样例 Il1 O0", SentAnnotations = [entry] };
        var turnView = new AiTurnView { TurnNumber = 1, Turn = turn };
        var scroll = new ScrollViewer { Content = turnView };
        scroll.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
        var host = new Window
        {
            Owner = owner, Content = scroll, Width = 850, Height = 680, Left = -3000, Top = -3000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false
        };
        var originalTheme = ThemeManager.Current.Id;
        var failures = new List<string>();
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        async Task Layout()
        {
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
        }
        host.Show();
        try
        {
            await Layout();
            var answer = turnView.Answers.Single();
            await answer.RenderTask;
            var formulas = Descendants<ChatFormula>(answer).ToArray();
            await Task.WhenAll(formulas.Select(formula => formula.RenderTask)).WaitAsync(TimeSpan.FromSeconds(60));
            var assets = new Dictionary<string, ChatSvg>();
            foreach (var (name, source, displayStyle) in fixtures)
            {
                assets[source] = await ChatTexRenderer.RenderAsync(source, displayStyle);
                if (realTex) File.Copy(assets[source].Path, Path.Combine(output, name + ".svg"), overwrite: true);
            }
            var reference = turnView.Questions.Last();
            await reference.RenderTask;
            await WaitUntilAsync(() => Descendants<ChatFormula>(reference).Any(), "Inline formula missing from the sent excerpt.");
            var sourceText = answer.CopySource(answer.Document.ContentStart, answer.Document.ContentEnd);
            foreach (var (id, quote) in new[] { ("heading", "标题来源"), ("body", "正文来源") })
            {
                var offset = sourceText.IndexOf(quote, StringComparison.Ordinal);
                if (offset < 0) throw new InvalidOperationException("Missing inline link anchor: " + quote);
                var anchor = new AiAnswerAnchor(turn.Id, "answer", 0, quote.Length, quote, answer.Revision) { TextOffset = offset };
                if (!answer.AddBranchLink(id, "回看来源", anchor, null, null, () => { }))
                    throw new InvalidOperationException("Could not attach inline link to " + quote);
            }
            await Layout();
            foreach (var (theme, width, screenshot) in new[]
            {
                ("ristretto", 850d, "ristretto"), ("github-light-default", 850d, "github-light-default"),
                ("ristretto", 440d, "narrow")
            })
            {
                ThemeManager.Apply(theme);
                host.Width = width;
                await Layout();
                Capture(host, Path.Combine(output, "inline-axis-" + screenshot + ".png"), 2);
                var glyphs = GlyphInk(answer, answer);
                var headingInk = glyphs.Single(glyph => glyph.Text.Contains("标题样例"));
                var bodyInk = glyphs.Single(glyph => glyph.Text.Contains("正文样例"));
                var markerInk = glyphs.Single(glyph => glyph.Text.Contains("[1:1]"));
                var turnNumber = Descendants<TextBlock>(turnView).Single(block => block.Text == "[1]");
                var numberInk = GlyphInk(turnNumber, turnView).Single(glyph => glyph.Text.Contains("[1]"));
                Check(headingInk.Weight == bodyInk.Weight && headingInk.BoldSimulation == bodyInk.BoldSimulation &&
                    markerInk.Weight == 400 && numberInk.Weight == 400 && !markerInk.BoldSimulation && !numberInk.BoldSimulation,
                    $"{screenshot}: heading and ordinals did not use ordinary reading weight ({headingInk.Weight}/{bodyInk.Weight}/{markerInk.Weight}/{numberInk.Weight}).");
                Check(new[] { headingInk, bodyInk, markerInk, numberInk }.All(glyph => glyph.EmSize == 18),
                    $"{screenshot}: heading, body and ordinals did not paint at 18 DIP.");

                foreach (var (name, textInk) in new[] { ("heading", headingInk), ("body", bodyInk) })
                {
                    var button = (Button)answer.BranchLinkBlocks[name].Child;
                    var bubble = Descendants<Image>(button).Single(image => ReferenceEquals(image.Tag, EmojiSprite.Marker));
                    var bubbleInk = InlineBubbleInk(bubble, answer);
                    var bubbleDelta = VerticalCenter(bubbleInk) - VerticalCenter(textInk.Bounds);
                    Console.WriteLine($"{screenshot} {name}: bubble Δ {bubbleDelta:F3} DIP.");
                    Check(Math.Abs(bubbleDelta) <= 1.25, $"{screenshot} {name}: painted bubble misses the text axis by {bubbleDelta:F3} DIP.");
                }
                foreach (var (source, adjacent) in new[] { (equality, "标题样例"), (tallEquality, "正文样例"),
                    ("A", "公共矩阵"), ("r", "公共向量"), (indexed, "接收方采样"), (sent, "发送方计算"), (fraction, "分数为"),
                    (transpose, "现在回看式"), (inline, "The answer") })
                {
                    var formula = formulas.Single(value => value.Source == source);
                    var text = glyphs.Single(glyph => glyph.Text.Contains(adjacent));
                    var delta = PaintedTexBaseline(formula, assets[source], answer) - text.Baseline;
                    Console.WriteLine($"{screenshot} {source}: painted TeX baseline minus text baseline = {delta:F3} DIP.");
                    Check(Math.Abs(delta) < .5, $"{screenshot} {source} baseline differs from adjacent text by {delta:F3} DIP.");
                }
                var cited = Descendants<ChatFormula>(reference).Single();
                var citationText = GlyphInk(reference, reference).Single(glyph => glyph.Text.Contains("引文中的向量"));
                var citationDelta = PaintedTexBaseline(cited, assets["r"], reference) - citationText.Baseline;
                Check(Math.Abs(citationDelta) < .5, $"{screenshot} sent excerpt baseline differs by {citationDelta:F3} DIP.");
                foreach (var (name, source, displayStyle) in fixtures)
                {
                    var formula = formulas.Single(value => value.Source == source);
                    var ink = InlineFormulaInk(formula, answer);
                    var em = ink.Width / assets[source].Geometry.Bounds.Width * (10 * 72d / 72.27) / (displayStyle ? 1.15 : 1);
                    Console.WriteLine($"{screenshot} {name}: actual TeX glyph scale {em:F3} DIP, bounds {ink}.");
                    Check(formula.Error is null && Math.Abs(em - 18) < .05,
                        $"{screenshot} {name}: TeX path was drawn at {em:F3} DIP.");
                }
            }

            var streaming = new MarkdownView { ChatMode = true, IsStreaming = true, Markdown = $"正文中轴 ${equality}$" };
            scroll.Content = streaming;
            await streaming.RenderTask;
            await Layout();
            var liveText = GlyphInk(streaming, streaming).Single(glyph => glyph.Text.Contains("正文中轴"));
            Check(liveText.EmSize == 18, "Streaming reading text is not painted at 18 DIP.");
            streaming.IsStreaming = false;
            await streaming.RenderTask;
            await Layout();
            var streamedMath = Descendants<ChatFormula>(streaming).Single();
            await streamedMath.RenderTask;
            await Layout();
            var streamedInk = InlineFormulaInk(streamedMath, streaming);
            var streamedText = GlyphInk(streaming, streaming).Single(glyph => glyph.Text.Contains("正文中轴"));
            var streamedDelta = PaintedTexBaseline(streamedMath, assets[equality], streaming) - streamedText.Baseline;
            var streamedEm = streamedInk.Width / assets[equality].Geometry.Bounds.Width * 10 * 72d / 72.27;
            Console.WriteLine($"After streaming settles: baseline Δ {streamedDelta:F3} DIP, actual scale {streamedEm:F3} DIP.");
            Check(Math.Abs(streamedDelta) < .5 && Math.Abs(streamedEm - 18) < .05,
                $"Settled inline TeX size or baseline differs from the reading text: size {streamedEm:F3}, baseline Δ {streamedDelta:F3}.");
            if (!measureOnly && failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
            if (measureOnly && failures.Count > 0) Console.WriteLine(string.Join(Environment.NewLine, failures));
            else Console.WriteLine("PASS: WPF paints regular titles and ordinals, 18-DIP inline TeX on the text baseline and centered speech bubbles.");
        }
        finally { host.Close(); ThemeManager.Apply(originalTheme); }
    }

    private static double VerticalCenter(Rect bounds) => bounds.Top + bounds.Height / 2;

    private static double PaintedTexBaseline(ChatFormula formula, ChatSvg svg, Visual relative)
    {
        // Recover the TeX origin from the drawn paths and shipout metrics, independently of WPF's attached baseline.
        var drawing = InlineFormulaInk(formula, relative);
        var scale = drawing.Width / svg.Geometry.Bounds.Width;
        return drawing.Top + (svg.Baseline + svg.Bounds.Top - svg.Geometry.Bounds.Top) * scale;
    }

    private static Rect InlineFormulaInk(ChatFormula formula, Visual relative)
    {
        if (VisualTreeHelper.GetDrawing(formula) is not { Bounds.IsEmpty: false } drawing || formula.SvgPath is null)
            throw new InvalidOperationException("No TeX path was painted for " + formula.Source + ": " + formula.Error);
        return formula.TransformToAncestor(relative).TransformBounds(drawing.Bounds);
    }

    private static Rect InlineBubbleInk(Image image, Visual relative)
    {
        var source = (BitmapSource)image.Source;
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[bitmap.PixelHeight * stride];
        bitmap.CopyPixels(pixels, stride, 0);
        var top = bitmap.PixelHeight;
        var bottom = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
                if (pixels[y * stride + x * 4 + 3] >= 128)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y + 1);
                }
        if (top >= bottom) throw new InvalidOperationException("The speech bubble has no visible pixels.");
        var scale = image.ActualHeight / bitmap.PixelHeight;
        return image.TransformToAncestor(relative).TransformBounds(new Rect(0, top * scale, image.ActualWidth, (bottom - top) * scale));
    }
}
