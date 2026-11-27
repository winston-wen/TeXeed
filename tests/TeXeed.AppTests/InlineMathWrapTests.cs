using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckInlineMathWrapAsync(MainWindow owner, string output, bool realTex, bool measureOnly)
    {
        const string ring = @"R_p=\mathbb{Z}_p[X]/(X^N+1)";
        const string sum = "z=A_1+A_2+A_3+A_4+A_5+A_6+A_7+A_8+A_9";
        const string fraction = @"\frac{a+b}{c-d}";
        const string nested = @"\left(\frac{x_1+x_2+x_3}{y_1-y_2-y_3}\right)^{m+n}";
        const string shortFormula = "x_i";
        const string prefix = "对多项式舍入时，逐系数应用同一规则，结果属于";
        const string suffix = "。对多项式向量或矩阵舍入时，则对其中每个多项式这样处理，形状不变。";
        var markdown = $"{prefix} ${ring}${suffix}\n\n依次相加得到 ${sum}$。\n\n" +
            $"其中 ${shortFormula}$ 为系数，分数 ${fraction}$ 和带幂的括号 ${nested}$ 各自构成一个整体。";
        (string Name, string Source)[] expressions = [("ring", ring), ("sum", sum), ("short", shortFormula), ("fraction", fraction), ("group", nested)];
        var cache = Directory.CreateDirectory(Path.Combine(AiStorage.Home, "math-svg"));
        if (!realTex)
        {
            foreach (var (name, source) in expressions)
            {
                using var fixture = typeof(Program).Assembly.GetManifestResourceStream($"TeXeed.AppTests.Fixtures.inline-wrap.{name}.svg")
                    ?? throw new FileNotFoundException("Missing TeX SVG for " + source);
                using var file = File.Create(Path.Combine(cache.FullName, ChatTexRenderer.CacheFileName(source, false)));
                fixture.CopyTo(file);
            }
        }
        var assets = await Task.WhenAll(expressions.Select(e => ChatTexRenderer.RenderAsync(e.Source, false)));
        if (realTex)
            for (var i = 0; i < expressions.Length; i++) File.Copy(assets[i].Path, Path.Combine(output, expressions[i].Name + ".svg"), true);
        var oldTheme = ThemeManager.Current.Id;
        ThemeManager.Apply("ristretto");
        var shell = new Border { Padding = new Thickness(18, 14, 18, 14) };
        shell.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        var window = new Window
        {
            Owner = owner, Content = shell, Width = 950, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = -3000, Top = -3000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false
        };
        window.Show();
        var view = new MarkdownView();
        async Task Settle()
        {
            window.UpdateLayout();
            await view.RenderTask;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        ChatFormula[] Pieces(string source) => Descendants<ChatFormula>(view).Where(f => f.Source == source).ToArray();
        double Baseline(ChatFormula f) => f.TranslatePoint(new Point(0, TextBlock.GetBaselineOffset(f)), view).Y;
        void CheckObjectSelection(ChatFormula piece)
        {
            var center = piece.TranslatePoint(new Point(piece.ActualWidth / 2, piece.ActualHeight / 2), view);
            Require(ReferenceEquals(view.InputHitTest(center), piece), "The wrapped formula is not hit-testable at its painted position.");
            view.Selection.Select(view.Document.ContentStart, view.Document.ContentStart);
            piece.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            var parts = view.SelectedReferenceParts();
            var selected = view.SelectedReferenceObject();
            Require(parts is [{ Kind: "formula", Text: ring, Display: false }] && selected.Kind == "formula" && selected.Source == ring,
                "Right-clicking a wrapped piece did not select exactly one complete inline formula.");
            Require(view.CopyForClipboard(view.Selection.Start, view.Selection.End).Trim() == "$" + ring + "$",
                "Copying the selected formula lost or duplicated part of the TeX.");
            var container = LogicalTreeHelper.GetParent(piece) as InlineUIContainer ??
                throw new InvalidOperationException("Missing inline container at the equation hit.");
            view.Selection.Select(container.ElementStart, container.ElementEnd);
            Require(view.SelectedReferenceParts() is [{ Kind: "formula", Text: ring, Display: false }] &&
                view.CopyForClipboard(view.Selection.Start, view.Selection.End).Trim() == "$" + ring + "$",
                "Dragging across a single slice did not preserve the whole formula.");
        }
        var samples = new List<object>();
        async Task Measure(string operation, Func<Task> action, bool record)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var begin = Stopwatch.GetTimestamp();
            await action();
            await Settle();
            var elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (record)
                samples.Add(new { operation, milliseconds = elapsed, bytes, formulaVisuals = Descendants<ChatFormula>(view).Count(),
                    visibleControls = Descendants<Control>(shell).Count(c => c.IsVisible) });
        }
        try
        {
            for (var pass = 0; pass < (measureOnly ? 7 : 1); pass++)
            {
                window.Width = 950;
                await Measure("open", () =>
                {
                    view = new MarkdownView { ChatMode = true, Markdown = markdown };
                    shell.Child = view;
                    return Task.CompletedTask;
                }, pass > 0);
                var originals = Pieces(ring);
                Require(originals.Length > 0 && originals.All(f => f.SvgPath != null && f.Error == null), "TeX did not render the ring.");
                var width = originals.Sum(f => f.ActualWidth);
                var left = originals[0].TranslatePoint(new Point(), view).X;
                var firstBaseline = Baseline(originals[0]);
                var middleWidth = Math.Ceiling(window.Width - view.ActualWidth + view.Padding.Right + left + width * .60);
                if (!measureOnly)
                {
                    Require(originals.All(f => Math.Abs(Baseline(f) - firstBaseline) < .6),
                        "A wide line must contain the complete ring at one text baseline.");
                    foreach (var atom in new[] { shortFormula, fraction, nested })
                        Require(Pieces(atom).Length == 1, "A TeX group without an outer breakpoint was split: " + atom);
                    CheckWrapDrawing(originals, assets[0]);
                    Capture(window, Path.Combine(output, "inline-wrap-wide.png"), 2);
                }
                await Measure("resize-to-line-end", () => { window.Width = middleWidth; return Task.CompletedTask; }, pass > 0);
                var wrapped = Pieces(ring);
                if (!measureOnly)
                {
                    Capture(window, Path.Combine(output, "inline-wrap-line-end.png"), 2);
                    var positions = wrapped.Select(Baseline).ToArray();
                    Require(Math.Abs(positions[0] - firstBaseline) < 1 && positions[^1] - positions[0] > 12,
                        $"The first line did not use its remaining width and continue on the next line at {middleWidth} DIP: " + string.Join(", ", positions));
                    Require(Math.Abs(wrapped[0].TranslatePoint(new Point(), view).X - left) < 1 &&
                        wrapped.First(f => Baseline(f) > positions[0] + 12).TranslatePoint(new Point(), view).X < 10,
                        "The continuation did not start at the paragraph's left edge.");
                    var paragraph = (Paragraph)view.Document.Blocks.FirstBlock!;
                    var copy = view.CopyForClipboard(paragraph.ContentStart, paragraph.ContentEnd);
                    Require(copy.Contains(prefix) && copy.Contains(suffix) && copy.Split("$" + ring + "$").Length == 2 &&
                        view.CopySource(paragraph.ContentStart, paragraph.ContentEnd).Trim() == prefix + " $" + ring + "$" + suffix,
                        "Wrapping changed the canonical paragraph or its clipboard text: " + copy);
                    var anchor = new AiAnswerAnchor("source", "answer", 0, suffix.Length, suffix, view.Revision)
                        { TextOffset = (prefix + " $" + ring + "$").Length };
                    var endOfFormula = view.AnnotationPointer("after-ring", anchor);
                    Require(endOfFormula != null && view.CopySource(endOfFormula, paragraph.ContentEnd).Trim() == suffix,
                        "An annotation after the wrapped equation no longer returns to the adjacent text.");
                    foreach (var piece in wrapped) CheckObjectSelection(piece);
                    view.Selection.Select(paragraph.ContentStart, paragraph.ContentStart);
                }
                await Measure("narrow", () => { window.Width = 228; return Task.CompletedTask; }, pass > 0);
                if (!measureOnly)
                {
                    Require(Pieces(sum).Select(f => Math.Round(Baseline(f))).Distinct().Count() >= 3, "Long sum did not continue across multiple lines.");
                    var edge = view.ActualWidth - view.Padding.Right;
                    Require(Pieces(sum).All(f => f.TranslatePoint(new Point(f.ActualWidth, 0), view).X <= edge + 1),
                        "A wrapped part overflows the narrow column.");
                    Capture(window, Path.Combine(output, "inline-wrap-narrow.png"), 2);
                }
                await Measure("back-to-wide", () => { window.Width = 950; return Task.CompletedTask; }, pass > 0);
                if (!measureOnly)
                {
                    var restored = Pieces(ring);
                    Require(restored.All(f => Math.Abs(Baseline(f) - firstBaseline) < .6), "Widening the view left stale line breaks.");
                    CheckWrapDrawing(restored, assets[0]);
                }
            }
            if (measureOnly) File.WriteAllText(Path.Combine(output, "inline-wrap-performance.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            else Console.WriteLine("PASS: a TeX ring fills a line ending, a long sum wraps repeatedly, nested groups remain intact, original glyphs, right-click references and clipboard source survive narrowing and widening.");
        }
        finally { window.Close(); ThemeManager.Apply(oldTheme); }
        if (!measureOnly)
        {
            await CheckConversationFormulaWrapAsync(owner, output, ring);
            await CheckInlineBreakSyntaxAsync(realTex);
        }
    }

    private static void CheckWrapDrawing(ChatFormula[] pieces, ChatSvg svg)
    {
        var scale = (double)pieces[0].GetValue(TextElement.FontSizeProperty) / (10 * 72d / 72.27);
        var logicalWidth = svg.Bounds.Width * scale + 2;
        if (Math.Abs(pieces.Sum(p => p.ActualWidth) - logicalWidth) > 1)
            throw new InvalidOperationException("Wide inline slices changed the width of the TeX box.");
        var width = (int)Math.Ceiling((logicalWidth + 4) * 2);
        var height = (int)Math.Ceiling((svg.Bounds.Height * scale + 6) * 2);
        byte[] Alpha(bool original)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (original)
                {
                    dc.PushTransform(new TranslateTransform(1 - svg.Bounds.Left * scale, 1 - svg.Bounds.Top * scale));
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    dc.DrawGeometry(Brushes.Black, null, svg.Geometry);
                    dc.Pop(); dc.Pop();
                }
                else
                {
                    double start = 0;
                    foreach (var piece in pieces)
                    {
                        dc.PushTransform(new TranslateTransform(start, 0));
                        dc.PushClip(new RectangleGeometry(new Rect(piece.RenderSize)));
                        dc.DrawDrawing(VisualTreeHelper.GetDrawing(piece));
                        dc.Pop(); dc.Pop();
                        start += piece.ActualWidth;
                    }
                }
            }
            var bitmap = new RenderTargetBitmap(width, height, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var data = new byte[width * height * 4];
            bitmap.CopyPixels(data, width * 4, 0);
            return data;
        }
        var reference = Alpha(true);
        var rendered = Alpha(false);
        long delta = 0, total = 0;
        for (var i = 3; i < reference.Length; i += 4)
        {
            delta += Math.Abs(reference[i] - rendered[i]);
            total += reference[i];
        }
        if (total == 0 || delta > total * .06)
            throw new InvalidOperationException($"Wide TeX glyphs changed after splitting (alpha error {delta}/{total}).");
    }
}
