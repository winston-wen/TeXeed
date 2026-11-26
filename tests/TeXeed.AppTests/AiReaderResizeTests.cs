using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    // Compare the same stretch of the rendered answer before and after a
    // viewport change. Allow one DIP for anti-aliasing and pixel snapping.
    private static double DifferentReaderInk(BitmapSource baseline, BitmapSource current, Int32Rect band, int newTop, int radius)
    {
        int width = band.Width, height = band.Height, stride = width * 4;
        var oldPixels = new byte[stride * height]; var newPixels = new byte[stride * height];
        baseline.CopyPixels(band, oldPixels, stride, 0);
        current.CopyPixels(new Int32Rect(band.X, newTop, width, height), newPixels, stride, 0);
        var bg = oldPixels.AsSpan(0, 3).ToArray();
        bool[] Ink(byte[] pixels)
        {
            var mask = new bool[width * height];
            for (var i = 0; i < mask.Length; i++)
                mask[i] = Math.Abs(bg[0] - pixels[i * 4]) > 64 || Math.Abs(bg[1] - pixels[i * 4 + 1]) > 64 ||
                    Math.Abs(bg[2] - pixels[i * 4 + 2]) > 64;
            return mask;
        }
        var oldMask = Ink(oldPixels); var newMask = Ink(newPixels);
        var worst = 0d; int changed = 0, total = 0;
        for (var y = radius; y < height - radius; y++)
        {
            for (var x = radius; x < width - radius; x++)
            {
                int i = y * width + x;
                if (!oldMask[i] && !newMask[i]) continue;
                total++;
                if (oldMask[i] == newMask[i]) continue;
                var other = oldMask[i] ? newMask : oldMask; var matched = false;
                for (var dy = -radius; dy <= radius && !matched; dy++)
                    for (var dx = -radius; dx <= radius; dx++)
                        if (other[i + dy * width + dx]) { matched = true; break; }
                if (!matched) changed++;
            }
            if (y % (radius * 24) == 0 || y == height - radius - 1)
            {
                if (total > width / 2) worst = Math.Max(worst, (double)changed / total);
                total = changed = 0;
            }
        }
        if (oldMask.Count(pixel => pixel) < width) throw new Exception("Reader pixel comparison did not capture the reference text.");
        return worst;
    }

    private static async Task CheckReaderViewportResizeAsync(Window owner, string output)
    {
        const string multi = @"\begin{aligned}w_x&=(\boldsymbol b'_x)^{\mathsf T}(\boldsymbol v\bmod p)+h_1,\\w'&=\boldsymbol b^{\mathsf T}(\boldsymbol u\bmod p)+h_1.\end{aligned}";
        const string single = @"z^x=\operatorname{Rec}(w',c_x)";
        var cache = Directory.CreateDirectory(Path.Combine(AiStorage.Home, "math-svg"));
        if (!Environment.GetCommandLineArgs().Contains("--real-tex"))
            foreach (var (name, tex) in new[] { ("aligned", multi), ("single", single) })
            {
                using var fixture = typeof(Program).Assembly.GetManifestResourceStream("TeXeed.AppTests.Fixtures.eqref." + name + ".svg")!;
                using var dest = File.Create(Path.Combine(cache.FullName, ChatTexRenderer.CacheFileName(tex, true))); fixture.CopyTo(dest);
            }
        const string first = "首先核对矩阵乘积和取模的顺序，然后计算接收方的数值。";
        const string middle = "但是这里得到的两个内积属于不同的选择支，接下来只恢复当前分支。";
        const string last = "因此，先写出等式，再检查哪一项依赖先前的消息。";
        var chapters = Enumerable.Range(1, 9).Select(n => $"# 先前的推导第 {n} 节\n\n" +
            string.Join("\n\n", Enumerable.Range(1, 18).Select(i => $"步骤 {n}.{i}。长回答中的条件、假设与计算须分别核对，遇到符号歧义时回到对应的公式。")) +
            "\n\n$$" + (n % 2 == 0 ? multi : single) + "$$");
        var source = string.Join("\n\n", chapters) + "\n\n# 分支恢复\n\n## 接收方如何计算\n\n" + first + "\n\n$$" + multi +
            "$$\n\n" + middle + "\n\n$$" + single + "$$\n\n" + last;
        string[] ninthText = ["先将这个方向代入生成式，比较两个独立的选择。", "再回到公开输入：恢复分支用到了刚才的一项，而取模本身不改变分支。", "最后把这个结果与上游推导的式子逐项核对。"];
        var ninthSource = "# 重查公开输入\n\n" + string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"第九轮准备步骤 {i}：从定义展开，给每个量写出它的维度和定义域。")) +
            "\n\n# 核对不同的两条公式\n\n" + ninthText[0] + "\n\n$$" + single + "$$\n\n" + ninthText[1] + "\n\n$$" + multi + "$$\n\n" +
            ninthText[2] + "\n\n" + string.Join("\n\n", Enumerable.Repeat("继续检验第九轮的其余条件。先复核表示，再讨论模运算限制。", 100));
        var book = new AiNotebook(); var target = new AiTurn("从定义推导两个分支的计算步骤", source);
        var headings = MarkdownView.BookmarkFragments(source).Where(f => f.Kind == AiBookmarkKind.Heading).ToList();
        foreach (var i in new[] { 1, 4, 6 }) target.CollapsedSections[target.Id + "-answer:" + headings[i].Position] = true;
        book.Turns.Add(target);
        foreach (var n in Enumerable.Range(2, 50))
            book.Turns.Add(new AiTurn($"后续轮次 {n}", n == 9 ? ninthSource :
                n % 3 == 0 ? string.Join("\n\n", Enumerable.Repeat(first, 9)) : last)
                { Collapsed = n is >= 2 and <= 8 || n % 4 == 0, FoldGroup = n is >= 2 and <= 8 ? "2-8" : null });
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var store = new AiWorkspaceStore(Path.Combine(output, "reader-resize.tex"), book);
        panel.Bind(store, book.Active.Id);
        var offscreen = Environment.GetCommandLineArgs().Contains("--offscreen");
        var host = new Window { Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Width = 552, Height = 769,
            ShowActivated = !offscreen, ShowInTaskbar = false, Content = panel, WindowStartupLocation = WindowStartupLocation.Manual, Left = offscreen ? -12000 : 20, Top = 24 };
        host.Show();
        async Task Idle()
        {
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(45); host.UpdateLayout();
        }
        var failures = new HashSet<string>();
        using var trace = new StreamWriter(Path.Combine(output, "reader-geometry.log")) { AutoFlush = true };
        try
        {
            var layout = Descendants<ChatVirtualizingPanel>(panel).Single();
            var history = ItemsControl.GetItemsOwner(layout);
            var generator = history.ItemContainerGenerator;
            void CheckSlots(string phase)
            {
                int previous = -2; var last = Rect.Empty;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(layout); i++)
                {
                    var child = (FrameworkElement)VisualTreeHelper.GetChild(layout, i);
                    var index = generator.IndexFromContainer(child);
                    var rect = LayoutInformation.GetLayoutSlot(child);
                    if (!child.IsVisible || index < 0 || rect.IsEmpty) continue;
                    if (index == previous + 1 && Math.Abs(rect.Top - last.Bottom) > 1)
                    {
                        var detail = $"{phase}: adjacent containers {previous}/{index}: gap {rect.Top - last.Bottom:F2} DIP ({last} / {rect}).";
                        trace.WriteLine(detail); failures.Add(detail);
                    }
                    last = rect; previous = index;
                }
            }
            foreach (var turnNumber in new[] { 1, 9 })
            {
                var turn = book.Turns[turnNumber - 1];
                if (turnNumber == 9) { host.Width = 820; await Idle(); }
                await Idle(); await panel.NavigateSameConversationTurnAsync(turn.Id); await Idle();
                CheckSlots($"visit-{turnNumber}");
                if (turnNumber == 9) { host.Width = 552; await Idle(); CheckSlots("narrow-with-previous-turn-retained"); }
                var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
                var answer = views[turn.Id].Answers.Single(); await answer.RenderTask; await AwaitMathAsync(answer); await Idle();
                var reader = (ScrollViewer)panel.FindName("MessagesScroll");
                static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch
                    { Paragraph p => [p], Section s => Paragraphs(s.Blocks), _ => Array.Empty<Paragraph>() });
                var paragraphs = Paragraphs(answer.Document.Blocks).ToList();
                var body = (turnNumber == 1 ? new[] { first, middle, last } : ninthText).Select(text => paragraphs.Single(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains(text))).ToArray();
                var formulas = Descendants<ChatFormula>(answer).Where(f => f.Source == multi || f.Source == single)
                    .OrderBy(f => f.TranslatePoint(new Point(), answer).Y).TakeLast(2).ToArray();
                if (formulas.Length != 2 || formulas[0].Source != (turnNumber == 1 ? multi : single) || formulas[1].Source != (turnNumber == 1 ? single : multi))
                    throw new Exception("Fixture did not realize both of the deep display equations in order");
                Rect TextRect(Paragraph p)
                {
                    var top = p.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
                    var end = p.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)!.GetCharacterRect(LogicalDirection.Backward);
                    top.Union(end);
                    return answer.TransformToVisual(host).TransformBounds(top);
                }
                Rect Bounds(FrameworkElement node) => node.TransformToVisual(host).TransformBounds(new Rect(new Point(), node.RenderSize));
                for (var i = 0; i < 5 && Math.Abs(TextRect(body[0]).Top - 90) > 2; i++)
                {
                    reader.ScrollToVerticalOffset(reader.VerticalOffset + TextRect(body[0]).Top - 90);
                    await Idle();
                }
                if (Math.Abs(TextRect(body[0]).Top - 90) > 3) throw new Exception("Could not position deep body anchor before resizing: " + TextRect(body[0]));
                var initial = TextRect(body[0]).Top; var originalOrigin = Bounds(reader).Top;
                void Measure(string phase)
                {
                    CheckSlots(phase);
                    if (!answer.IsLoaded) { failures.Add(phase + " detached the reading anchor."); return; }
                    var viewport = Bounds(reader);
                    var rects = new[] { TextRect(body[0]), Bounds(formulas[0]), TextRect(body[1]), Bounds(formulas[1]), TextRect(body[2]) };
                    trace.WriteLine($"{phase}: reader={viewport}, offset={reader.VerticalOffset:F2}, paragraphs and display formula boxes={string.Join(" | ", rects)}");
                    for (var i = 1; i < rects.Length; i++)
                        if (Math.Min(rects[i - 1].Bottom, viewport.Bottom) > Math.Max(rects[i].Top, viewport.Top) + 1)
                            failures.Add($"{phase}: displayed body blocks {i - 1}/{i} overlap, {rects[i - 1]} and {rects[i]}.");
                    var realized = Descendants<AiTurnView>(reader).Where(v => v.IsVisible).OrderBy(v => v.TurnNumber).ToArray();
                    foreach (var presenter in realized)
                        if (views.GetValueOrDefault(presenter.Turn.Id) != presenter && Bounds(presenter).IntersectsWith(viewport))
                            failures.Add($"{phase}: unregistered visible presenter for turn {presenter.TurnNumber}.");
                    for (var i = 1; i < realized.Length; i++)
                    {
                        var above = Bounds(realized[i - 1]); var below = Bounds(realized[i]);
                        if (Math.Min(above.Bottom, viewport.Bottom) > Math.Max(below.Top, viewport.Top) + 1)
                            failures.Add($"{phase}: realized turns {realized[i-1].TurnNumber}/{realized[i].TurnNumber} overlap.");
                    }
                    if (phase != "before" && Math.Abs(rects[0].Top - initial) > 2)
                        failures.Add($"{phase}: reading anchor drifted {rects[0].Top - initial:F2} DIP while reader origin moved {viewport.Top-originalOrigin:F2} DIP.");
                    if (realized.Length > 1) trace.WriteLine("  " + string.Join(", ", realized.Select(v => $"turn {v.TurnNumber} [{Bounds(v).Top:F0},{Bounds(v).Bottom:F0}]")));
                }
                void Press(Key key, bool control = false)
                {
                    var old = new byte[256]; if (!GetKeyboardState(old)) throw new Exception("Cannot read test keyboard state");
                    var modifiers = new byte[256]; if (control) modifiers[0x11] = modifiers[0xa2] = 0x80;
                    try
                    {
                        if (!SetKeyboardState(modifiers)) throw new Exception("Cannot set test keyboard modifier");
                        var routed = CreateKey(host, key, Keyboard.PreviewKeyDownEvent);
                        var origin = Keyboard.FocusedElement is UIElement focused && panel.IsAncestorOf(focused) ? focused : panel;
                        origin.RaiseEvent(routed);
                        if (!routed.Handled) throw new Exception($"Reader did not handle {key}, modifiers={Keyboard.Modifiers}, source={origin.GetType().Name}");
                    }
                    finally { SetKeyboardState(old); }
                }
                BitmapSource? beforePaint = null;
                var dpi = VisualTreeHelper.GetDpi(host);
                int Y(double dip) => (int)Math.Round(dip * dpi.DpiScaleY);
                async Task CapturePhase(string phase)
                {
                    await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(60);
                    var file = Path.Combine(output, $"reader-{turnNumber}-{phase}");
                    Measure(phase);
                    Capture(host, file + ".png", dpi.DpiScaleX);
                    using var stream = File.OpenRead(file + ".png");
                    var rendered = new FormatConvertedBitmap(new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgr32, null, 0);
                    var y = Y(TextRect(body[0]).Top - 6);
                    var radius = (int)Math.Ceiling(dpi.DpiScaleY);
                    if (beforePaint is null) beforePaint = rendered;
                    else
                    {
                        var band = new Int32Rect(Y(46), Y(initial - 6), Y(434), Y(292));
                        if (y < 0 || y + band.Height > rendered.PixelHeight)
                        {
                            failures.Add($"{phase}: the reference passage moved outside the screenshot at y={y}.");
                            return;
                        }
                        var difference = DifferentReaderInk(beforePaint, rendered, band, y, radius);
                        trace.WriteLine($"{phase}: aligned WPF ink difference {difference:P2}");
                        if (difference > 0.08) failures.Add($"{phase}: body paint changed ({difference:P2}).");
                    }
                }
                var search = (FrameworkElement)panel.FindName("MessageSearchPanel");
                var handling = "before"; trace.WriteLine($"Reading turn {turnNumber}");
                void Frame(object? sender, EventArgs args) => Measure(handling + "-frame");
                await CapturePhase("before");
                CompositionTarget.Rendering += Frame;
                try
                {
                    handling = "search"; Press(Key.F, true); await CapturePhase("search");
                    if (!search.IsVisible) throw new Exception("Ctrl+F did not open the search strip");
                    handling = "escape"; Press(Key.Escape); await CapturePhase("escape");
                    if (search.IsVisible) throw new Exception("Esc did not close the search strip");
                    handling = "rapid"; Press(Key.F, true); Press(Key.Escape); Press(Key.F, true);
                    await CapturePhase("rapid"); Press(Key.Escape);
                    if (turnNumber == 9)
                    {
                        handling = "annotation"; panel.SetAnswerMarking(true); await CapturePhase("annotation");
                        panel.CancelAnswerMarking();
                        var notice = (TextBlock)panel.FindName("StatusText");
                        await WaitUntilAsync(() => !notice.IsVisible, "Annotation notice did not release its viewport height");
                    }
                    handling = "restored"; await CapturePhase("restored");
                }
                finally { CompositionTarget.Rendering -= Frame; }
                if (turnNumber == 1)
                {
                    // Keep the same two rows WPF may retain outside its viewport
                    // around keyboard focus while a later turn is being read.
                    answer.Focus();
                    VirtualizingStackPanel.AddCleanUpVirtualizedItemHandler(history, (_, args) =>
                    {
                        if (ReferenceEquals(args.Value, target) || ReferenceEquals(args.Value, book.Turns[1])) args.Cancel = true;
                    });
                }
                else
                {
                    var earlier = Descendants<AiTurnView>(layout).Single(v => ReferenceEquals(v.Turn, target));
                    var asset = Descendants<ChatFormula>(earlier).Last();
                    var previousHeight = earlier.ActualHeight;
                    // Model a late change in a previously measured display object.
                    // Exercise the normal panel measure/arrange path in both directions.
                    asset.MinHeight = asset.ActualHeight + reader.ViewportHeight * 7;
                    await Idle(); await CapturePhase("retained-formula-grew");
                    trace.WriteLine($"Late display growth: first turn {previousHeight:F2} -> {earlier.ActualHeight:F2} DIP.");
                    if (earlier.ActualHeight < previousHeight + 2 * reader.ViewportHeight)
                        failures.Add("Fixture did not grow the retained turn by at least two viewports.");
                    asset.ClearValue(FrameworkElement.MinHeightProperty);
                    await Idle(); await CapturePhase("retained-formula-shrank");
                }
            }
            if (failures.Count > 0) throw new Exception(string.Join("\n", failures.Take(8)));
            Console.WriteLine("PASS: Ctrl+F / Esc and annotation notice retained deep reading location; visible paragraph, equation and adjacent turn geometry sampled across frames.");
        }
        finally { host.Content = null; host.Close(); }
    }
}
