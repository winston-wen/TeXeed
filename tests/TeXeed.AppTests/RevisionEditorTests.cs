using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    // Sample the rendered TextView, including the opacity of its caret layer.
    private static bool RevisionCaretPainted(RevisionLineEditor line)
    {
        var caret = Descendants<RevisionLineEditor.SourceCaret>(line).Single();
        var bounds = caret.InkBounds;
        if (bounds.IsEmpty) return false;
        var view = line.TextArea.TextView;
        var center = caret.TranslatePoint(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), view);
        var brush = new VisualBrush(view)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(Math.Floor(center.X), Math.Floor(center.Y), 1, 1),
            Stretch = Stretch.Fill
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, 1, 1));
        var image = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var pixel = new byte[4];
        image.CopyPixels(pixel, 4, 0);
        var expected = ThemeManager.Brush("RevisionCaret").Color;
        return pixel[0] == expected.B && pixel[1] == expected.G && pixel[2] == expected.R;
    }

    private static async Task CheckRevisionCaretBlinkAsync(Window host, RevisionLineEditor line, string output)
    {
        var start = line.CaretOffset;
        line.CaretOffset = 0;
        line.CaretOffset = start;
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var transitions = new List<double>();
        var elapsed = Stopwatch.StartNew();
        var lit = RevisionCaretPainted(line);
        if (!lit) throw new Exception("A recently moved insertion caret is not painted");
        while (transitions.Count < 7 && elapsed.Elapsed.TotalSeconds < 4)
        {
            await Task.Delay(25);
            var next = RevisionCaretPainted(line);
            if (lit == next) continue;
            transitions.Add(elapsed.Elapsed.TotalMilliseconds);
            lit = next;
        }
        File.WriteAllText(Path.Combine(output, "revision-caret-transitions-ms.json"), JsonSerializer.Serialize(transitions));
        if (transitions.Count < 7)
            throw new Exception($"Only {transitions.Count} rendered caret transitions in {elapsed.Elapsed.TotalSeconds:F1}s.");
        // Compare full cycles and both half-cycles. Median tolerates an occasional busy dispatcher frame.
        var bright = new List<double>(); var dark = new List<double>(); var periods = new List<double>();
        for (var i = 1; i < transitions.Count; i++)
        {
            (i % 2 == 0 ? bright : dark).Add(transitions[i] - transitions[i - 1]);
            if (i >= 2) periods.Add(transitions[i] - transitions[i - 2]);
        }
        static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
        if (Median(periods) is < 425 or > 595 || Median(bright) is < 175 or > 337 || Median(dark) is < 175 or > 337)
            throw new Exception($"Unexpected bright/dark durations or period: {string.Join(", ", transitions.Select(t => t.ToString("F0")))} ms");
        Console.WriteLine($"Caret pixels: bright {Median(bright):F0} ms, dark {Median(dark):F0} ms, full period {Median(periods):F0} ms.");
        Capture(host, Path.Combine(output, "revision-paragraph-ristretto-off.png"));
        line.CaretOffset--;
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!RevisionCaretPainted(line)) throw new Exception("Moving during the dark phase did not light the caret");
        line.Select(0, 2);
        if (RevisionCaretPainted(line)) throw new Exception("Selection still paints an insertion caret");
        line.Select(2, 0);
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!RevisionCaretPainted(line)) throw new Exception("Collapsing a selection did not light the caret");
    }

    private static async Task CheckRevisionEditorAsync(MainWindow owner, string output)
    {
        static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch
        {
            Paragraph paragraph => [paragraph],
            Section section => Paragraphs(section.Blocks),
            _ => []
        });
        var project = Path.Combine(output, "revision-editor-" + Guid.NewGuid().ToString("N") + ".tex");
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Width = 790, Height = 810, Content = panel, Title = "轮次原位整理" };
        host.Show();
        try
        {
            await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            var mathHome = Directory.CreateDirectory(Path.Combine(AiStorage.Home, "math-svg")).FullName;
            foreach (var (fixture, tex, display) in new[] { ("capital", "A", false), ("display-energy", "E=mc^2", true) })
            {
                using var svg = typeof(Program).Assembly.GetManifestResourceStream($"TeXeed.AppTests.Fixtures.inline-axis.{fixture}.svg")!;
                using var file = File.Create(Path.Combine(mathHome, ChatTexRenderer.CacheFileName(tex, display)));
                svg.CopyTo(file);
            }
            const string equationLabel = "eq:receiving-party-selection-and-coefficient-rounding-after-wrap";
            const string referenceSource = @"\eqref{" + equationLabel + "}";
            const string sourceLine = "最后一段含 $A$，公式 " + referenceSource + " 保留编号。";
            var turn = new AiTurn("怎样从追问回到整理稿？", "# 定义\n\n承接段。\n\n命题甲与乙。\n\n## 例子\n\n" +
                sourceLine + "\n\n| 标题 | 数值 |\n| --- | --- |\n| A | 1 |\n\n$$E=mc^2\\label{" + equationLabel + "}$$");
            book.Turns.Add(turn);
            foreach (var quote in new[] { "甲", "乙" })
                book.Conversations.Add(new AiConversation { Title = "解释" + quote, ParentConversationId = book.Active.Id,
                    BranchAnchor = new AiAnswerAnchor(turn.Id, turn.DisplaySegments[0].Id, 0, 1, quote, AiStorage.Hash(turn.Answer)) });
            Invoke(panel, "RefreshTranscript"); await Idle();
            var measurements = new Dictionary<string, object>();
            async Task Idle() { host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout(); }
            AiTurn Current() => book.Turns.Single(t => t.Id == turn.Id);
            AiTurnView View() => Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == turn.Id);
            TurnRevisionEditor Editor() => Descendants<TurnRevisionEditor>(panel).Single(v => v.IsVisible);
            RevisionLineEditor Line(int index)
            {
                var host = Descendants<RevisionLineHost>(Editor()).ElementAt(index);
                host.Focus();
                return host.Editor ?? throw new Exception("Focused paragraph did not create its input surface");
            }
            RevisionLineEditor ActiveLine(int index)
            {
                var hosts = Descendants<RevisionLineHost>(Editor()).ToList();
                var source = Descendants<RevisionLineEditor>(Editor()).Where(line => line.IsVisible).ToList();
                Check(source.Count == 1 && ReferenceEquals(hosts[index].Editor, source[0]) && source[0].TextArea.IsKeyboardFocusWithin,
                    $"Expected only paragraph {index + 1} in source with keyboard focus; visible editors {source.Count}, focus {Keyboard.FocusedElement}");
                return source[0];
            }
            List<AiRevisionRow> Rows() => Current().UserRevisionDraft!.Rows;
            void Click(string label)
            {
                var button = Descendants<Button>(Editor()).Single(b => b.Content as string == label);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            async Task<(double MenuMs, double ShowMs, long ShowBytes)> Command(string name, string? target = null)
            {
                var answer = View().Answers.Single();
                if (name == "整理这一段")
                {
                    await answer.RenderTask; await Idle();
                    var paragraph = Paragraphs(answer.Document.Blocks).First(p =>
                        target is null || new TextRange(p.ContentStart, p.ContentEnd).Text.Contains(target, StringComparison.Ordinal));
                    var origin = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft;
                    answer.CaptureContextParagraph(new Point(origin.X + 3, origin.Y + 5));
                }
                var menuStart = Stopwatch.GetTimestamp();
                var menu = answer.ContextMenu!;
                menu.PlacementTarget = answer; menu.IsOpen = true; await Idle();
                var item = menu.Items.OfType<MenuItem>().Single(it => it.Header as string == name);
                Check(item.IsEnabled, $"Context command is disabled: {name}");
                if (name == "整理这一段") Check(!menu.Items.OfType<MenuItem>().Any(it => Equals(it.Header, "整理这一轮")), "Paragraph menu still contains the turn-level command");
                menu.IsOpen = false;
                var showStart = Stopwatch.GetTimestamp(); var allocated = GC.GetAllocatedBytesForCurrentThread();
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Idle();
                return (Stopwatch.GetElapsedTime(menuStart, showStart).TotalMilliseconds, Stopwatch.GetElapsedTime(showStart).TotalMilliseconds,
                    GC.GetAllocatedBytesForCurrentThread() - allocated);
            }
            async Task KeyIn(int index, Key key, int at, int length = 0)
            {
                var line = Line(index); line.TextArea.Focus(); line.Select(at, length); if (length == 0) line.CaretOffset = at;
                line.TextArea.RaiseEvent(CreateKey(host, key, Keyboard.PreviewKeyDownEvent)); await Idle();
            }
            void Type(RevisionLineEditor line, string value)
            {
                var composition = new TextComposition(InputManager.Current, line.TextArea, value);
                line.TextArea.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
            }
            Check(Descendants<TurnRevisionEditor>(panel).Count() == 0, "Idle transcript allocated a revision editor");
            var cold = await Command("整理这一段", "承接段");
            ActiveLine(1);
            measurements["firstMenuMs"] = cold.MenuMs;
            measurements["firstOpenMs"] = cold.ShowMs;
            measurements["firstOpenUiAllocatedBytes"] = cold.ShowBytes;
            measurements["editableRows"] = Descendants<RevisionLineHost>(Editor()).Count();
            measurements["initialNativeEditors"] = Descendants<RevisionLineEditor>(Editor()).Count();
            Check(Rows().Count == 7 && Rows()[2].Links.Count == 2 && Rows()[5].Locked && Rows()[6].Locked, "Native editor lost protected links or table");
            var formula = Descendants<ChatFormula>(Editor()).Single(f => f.Source == "E=mc^2");
            await formula.RenderTask; await Idle();
            Check(formula.SvgPath is not null && formula.ActualWidth > 8 && formula.ActualHeight > 8 && formula.IsVisible, "The locked display formula was not painted");
            var mathHostView = Descendants<RevisionLineHost>(Editor()).ElementAt(4);
            Capture(host, Path.Combine(output, "revision-editor.png"));
            var originalTheme = ThemeManager.Current.Id;
            try
            {
                foreach (var theme in new[] { "ristretto", "github-light-default" })
                {
                    ThemeManager.Apply(theme);
                    var focused = ActiveLine(1); focused.CaretOffset = 0; focused.CaretOffset = 2;
                    await Idle();
                    var caret = Descendants<RevisionLineEditor.SourceCaret>(focused).Single();
                    var ink = caret.InkBounds;
                    Check(!ink.IsEmpty && ink.Width >= 3 && ink.Height > 8, $"The {theme} source caret has no thick visible stroke: {ink}");
                    var path = Path.Combine(output, $"revision-paragraph-{theme}.png");
                    Capture(host, path);
                    var center = caret.TranslatePoint(new Point(ink.X + ink.Width / 2, ink.Y + ink.Height / 2), panel);
                    using var png = File.OpenRead(path);
                    var image = new FormatConvertedBitmap(new PngBitmapDecoder(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
                    var pixel = new byte[4];
                    image.CopyPixels(new Int32Rect((int)center.X, (int)center.Y, 1, 1), pixel, 4, 0);
                    var expected = ThemeManager.Brush("RevisionCaret").Color;
                    Check(pixel[0] == expected.B && pixel[1] == expected.G && pixel[2] == expected.R,
                        $"The {theme} caret at {center} painted #{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}, expected {expected}");
                    if (theme == "ristretto") await CheckRevisionCaretBlinkAsync(host, focused, output);
                }
            }
            finally { ThemeManager.Apply(originalTheme); }
            var previousCaret = Descendants<RevisionLineEditor.SourceCaret>(ActiveLine(1)).Single();
            await KeyIn(1, Key.Right, ActiveLine(1).Text.Length);
            Check(ActiveLine(2).CaretOffset == 0, "Right at the end of a paragraph did not switch its neighbour into source");
            Check(RevisionCaretPainted(ActiveLine(2)), "Newly focused paragraph did not paint its insertion caret");
            var inactive = Stopwatch.StartNew();
            while (inactive.ElapsedMilliseconds < 600)
            {
                Check(!previousCaret.IsLoaded && previousCaret.Opacity == 0,
                    "The previous paragraph's caret continued blinking after it was detached");
                await Task.Delay(40);
            }
            await KeyIn(2, Key.Left, 0);
            Check(ActiveLine(1).CaretOffset == Rows()[1].Text.Length, "Left at the start did not restore the previous paragraph and caret");
            await KeyIn(1, Key.Enter, ActiveLine(1).Text.Length);
            var oneCharacter = ActiveLine(2);
            await WaitUntilAsync(() => !RevisionCaretPainted(oneCharacter), "The empty paragraph's caret did not blink");
            Type(oneCharacter, "甲"); await Idle();
            Check(RevisionCaretPainted(oneCharacter), "Typing during the dark phase did not light the caret");
            Check(oneCharacter.SelectionLength == 0 && oneCharacter.CaretOffset == 1, "Single-character deletion fixture has an unexpected selection");
            var hwnd = new WindowInteropHelper(host).Handle;
            var backspace = KeyInterop.VirtualKeyFromKey(Key.Back);
            Check(PostMessage(hwnd, 0x0100, backspace, 1) && PostMessage(hwnd, 0x0101, backspace, unchecked((int)0xc0000001)),
                "Could not deliver the Backspace key to the active editor");
            await WaitUntilAsync(() => Rows().Count == 7, "Deleting the last character did not remove its paragraph");
            await Idle();
            Check(ActiveLine(1).CaretOffset == Rows()[1].Text.Length, "Backspacing the last character did not move the caret and source to the previous paragraph");
            async Task RenderedMath()
            {
                var inline = Descendants<ChatFormula>(mathHostView).SingleOrDefault(f => f.Source == "A" && f.IsVisible);
                Check(inline is not null, "Inactive paragraph did not typeset its inline formula");
                await inline!.RenderTask; await Idle();
                Check(inline.SvgPath is not null && inline.ActualWidth > 0, "Inactive inline formula has no rendered ink");
            }
            await RenderedMath();
            mathHostView.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            ActiveLine(1); await RenderedMath();
            var hitPoint = mathHostView.TranslatePoint(new Point(10, AiChatTypography.LineHeight / 2), host);
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            switch (host.InputHitTest(hitPoint))
            {
                case UIElement hit when ReferenceEquals(hit, mathHostView) || mathHostView.IsAncestorOf(hit):
                    hit.RaiseEvent(press);
                    break;
                case TextElement textHit when mathHostView.Child is MarkdownView rich &&
                    textHit.ContentStart.IsInSameDocument(rich.Document.ContentStart):
                    textHit.RaiseEvent(press);
                    break;
                default:
                    throw new Exception("The rendered rich paragraph is not a hit target in its text line");
            }
            await Idle();
            var mathEditor = ActiveLine(4);
            Check(ActiveLine(4).Text == sourceLine && !Descendants<ChatFormula>(mathHostView).Any(f => f.IsVisible),
                "Focused source did not expose the literal inline TeX and protected equation reference");
            mathEditor.TextArea.RaiseEvent(CreateKey(host, Key.Escape, Keyboard.PreviewKeyDownEvent)); await Idle();
            Check(!Descendants<RevisionLineEditor>(Editor()).Any(line => line.IsVisible) && Current().UserRevisionDraft is not null,
                "Escape did not put the paragraph back in reading mode within the current draft");
            await RenderedMath();
            mathEditor = Line(4);
            Descendants<Button>(Editor()).Single(b => Equals(b.Content, "完成整理")).Focus(); await Idle();
            Check(!Descendants<RevisionLineEditor>(Editor()).Any(line => line.IsVisible), "Focusing the toolbar left a paragraph in source");
            await RenderedMath();
            mathEditor = Line(4);
            var sourceStart = mathEditor.Text.IndexOf("$A$", StringComparison.Ordinal);
            mathEditor.Select(sourceStart, "$A$".Length);
            Type(mathEditor, @"\(q^2\)"); await Idle();
            Check(Rows()[4].Text == sourceLine.Replace("$A$", @"\(q^2\)", StringComparison.Ordinal), "Keyboard input did not replace the inline TeX with its exact source");
            await panel.SaveAsync();
            Check(AiStorage.Load(project).Turns.Single().UserRevisionDraft?.Rows[4].Text == Rows()[4].Text, "Changed TeX source did not round-trip in the draft");
            Click("撤销"); await Idle();
            await KeyIn(4, Key.Delete, sourceStart, "$A$".Length);
            Check(Rows()[4].Text == sourceLine.Replace("$A$", "", StringComparison.Ordinal), "Deleting inline TeX changed its neighboring prose or equation reference");
            Click("撤销"); await Idle();
            var refEditor = Line(4);
            var referenceStart = refEditor.Text.IndexOf(referenceSource, StringComparison.Ordinal);
            await KeyIn(4, Key.Delete, referenceStart + 2, 8);
            Check(Rows()[4].Text == sourceLine, "Deleting part of a protected equation reference changed the draft");
            refEditor = Line(4);
            refEditor.CaretOffset = referenceStart + 2;
            Type(refEditor, "错"); await Idle();
            Check(Rows()[4].Text == sourceLine, "Typing inside a protected equation reference changed the draft");
            await KeyIn(4, Key.Enter, referenceStart + 2);
            Check(Rows().Count == 8 && Rows()[4].Text.EndsWith(referenceSource, StringComparison.Ordinal) && Rows()[5].Text == " 保留编号。",
                "Splitting a paragraph within a wrapped reference must retain the whole reference");
            Click("撤销"); await Idle();
            host.Width = 360;
            refEditor = Line(4);
            await Idle();
            Capture(host, Path.Combine(output, "revision-editor-inline-wrap.png"));
            Check(ActiveLine(4).Text == sourceLine, "Narrow layout changed the active source");
            var textView = refEditor.TextArea.TextView;
            var visual = textView.VisualLines.Single();
            var firstReferenceLine = visual.GetTextLine(visual.GetVisualColumn(referenceStart));
            var lastReferenceLine = visual.GetTextLine(visual.GetVisualColumn(referenceStart + referenceSource.Length - 1));
            Check(!ReferenceEquals(firstReferenceLine, lastReferenceLine) && Math.Abs(firstReferenceLine.Baseline - lastReferenceLine.Baseline) < 1,
                $"Protected equation reference failed to wrap at matching baselines (width {textView.ActualWidth:F1} DIP; " +
                $"{visual.TextLines.Count} visual lines; first/last baselines {firstReferenceLine.Baseline:F2}/{lastReferenceLine.Baseline:F2} DIP)");
            host.Width = 790; await Idle();
            Check(Rows()[4].Text == sourceLine, "Undo did not restore the original formula and reference");
            var linkIds = Rows()[2].Links.Select(l => l.Id).ToArray();
            var across = Line(2);
            across.Select(0, across.Text.Length); Type(across, "合并表述"); await Idle();
            Check(Rows()[2].Text == "合并表述" && Rows()[2].Links.Select(l => l.Id).SequenceEqual(linkIds), "IME completion over two protected references lost wording or ownership");
            Click("撤销"); await Idle();
            var text = Line(2); text.TextArea.Focus(); text.CaretOffset = text.Text.IndexOf(RevisionBuffer.Marker) + 1;
            Type(text, "中"); await Idle();
            Check(Rows()[2].Text == "命题甲中与乙。" && Rows()[2].Links[0].Offset == 3 && Rows()[2].Links[1].Offset == 6, "Committed Chinese text did not insert immediately after the reference");
            await KeyIn(2, Key.Enter, text.CaretOffset);
            Check(Rows().Count == 8 && Rows()[2].Links.Single().Id == linkIds[0] && Rows()[3].Links.Single().Id == linkIds[1], "Enter did not split reference ownership");
            Check(ActiveLine(3).CaretOffset == 0, "Splitting did not move the source caret to the new paragraph");
            await KeyIn(3, Key.Back, 0);
            Check(Rows().Count == 7 && Rows()[2].Links.Select(l => l.Id).SequenceEqual(linkIds) && ActiveLine(2).CaretOffset == 5, "Backspace failed to merge at original seam");
            await KeyIn(2, Key.Delete, 0, Line(2).Text.Length);
            Check(Rows().Count == 6 && Rows()[1].Links.Select(l => l.Id).SequenceEqual(linkIds) && ActiveLine(1).CaretOffset == 4, "Clearing a linked row did not move both links to the previous text end");
            Click("撤销"); await Idle();
            Check(Rows().Count == 7 && Rows()[2].Links.Count == 2, "Undo lost a paragraph or its references");
            var rightId = Rows()[2].Id;
            await KeyIn(1, Key.Delete, Line(1).Text.Length);
            Check(Rows()[1].Id == rightId && Rows()[1].Text == "承接段。命题甲中与乙。" && ActiveLine(1).CaretOffset == 4, "Nonempty Delete failed to join the next row at its original seam");
            Click("撤销"); await Idle();
            var locked = Descendants<MarkdownView>(Editor()).Single(v => v.Markdown.StartsWith("$$E=mc^2", StringComparison.Ordinal));
            locked.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent)); await Idle();
            var last = Descendants<RevisionLineHost>(Editor()).Count() - 1;
            await KeyIn(last, Key.Delete, 0);
            Check(Rows()[^1].Text.Length == 0 && Rows().Count == 8, "Delete removed the last blank paragraph");
            await KeyIn(last, Key.Back, 0);
            Check(Rows()[^1].Locked && Rows().Count == 7, "Backspace next to a locked display damaged the formula");
            await KeyIn(1, Key.Enter, 0);
            Check(Rows()[1].Text == "", "Enter at start did not create an empty paragraph: " + string.Join(" | ", Rows().Select(r => r.Text)));
            await KeyIn(1, Key.Delete, 0);
            Check(Rows()[1].Text == "承接段。" && ActiveLine(1).CaretOffset == 0, "Empty Delete did not put the caret at next text start");
            await KeyIn(0, Key.Enter, 0); await KeyIn(0, Key.Back, 0);
            Check(Rows()[0].Text == "", "Backspace deleted the first empty row");
            await KeyIn(0, Key.Delete, 0);
            Click("选择段落"); await Idle();
            Check(!Descendants<CheckBox>(Editor()).Any(), "Selection introduced checkboxes");
            var wanted = new[] { Rows()[0].Id, Rows()[2].Id };
            foreach (var id in wanted) Descendants<Button>(Editor()).Single(b => Equals(b.Tag, id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click("删除所选 2 段"); await Idle();
            Check(Rows().Count == 5 && Rows()[0].Links.Select(l => l.Id).SequenceEqual(linkIds) && Rows()[^1].Locked, "Batch deletion changed protected contents");
            var heading = Line(1); heading.TextArea.Focus();
            Check(Descendants<TextBlock>(Editor()).Any(t => t.IsVisible && t.Text == "## "), "Focused second-level heading did not expose its Markdown prefix");
            Descendants<ComboBox>(Editor()).Single().SelectedIndex = 1; await Idle();
            Check(Rows()[1].Level == 1 && Descendants<TextBlock>(Editor()).Any(t => t.IsVisible && t.Text == "# "), "Changing the heading level did not update its source prefix");
            ActiveLine(1).TextArea.RaiseEvent(CreateKey(host, Key.Escape, Keyboard.PreviewKeyDownEvent)); await Idle();
            Check(Descendants<TextBlock>(Editor()).Any(t => t.IsVisible && t.Text == "[1:1] "), "Edited heading level was not renumbered when rendered");
            await panel.SaveAsync();
            Check(AiStorage.Load(project).Turns.Single().UserRevisionDraft?.Rows.Count == Rows().Count, "Unsaved manual revision draft did not round-trip");
            Click("完成整理");
            await WaitUntilAsync(() => Current().RevisionHistory?.Count == 2 && !Descendants<TurnRevisionEditor>(panel).Any(), "Manual revision did not commit via toolbar");
            await Idle();
            var saved = AiStorage.Load(project).Turns.Single();
            Check(saved.UserRevisionDraft == null && saved.RevisionHistory![0].Author == "ai" && saved.RevisionHistory[1].Author == "user" && saved.Answer.Contains("| A | 1 |"), "Saved history, authors or locked table did not round-trip");
            await Command("编辑历史");
            var history = Application.Current.Windows.OfType<Window>().Single(w => w.Title.Contains("编辑历史"));
            try
            {
                var choices = Descendants<ComboBox>(history).Single(); choices.SelectedIndex = 1; history.UpdateLayout();
                Check(((DockPanel)history.Content).Children.OfType<MarkdownView>().Single().Markdown == turn.Answer, "History did not restore the original text");
                Capture(history, Path.Combine(output, "revision-history.png"));
            }
            finally { history.Close(); }
            await Command("整理这一段"); var first = Line(0); first.TextArea.Focus(); first.CaretOffset = 0; Type(first, "临时");
            Click("取消整理");
            await WaitUntilAsync(() => Current().UserRevisionDraft == null && !Descendants<TurnRevisionEditor>(panel).Any(), "Cancel left an active editor");
            Check(Current().RevisionHistory!.Count == 2 && !Current().Answer.Contains("临时"), "Cancel changed the saved answer");
            var warm = new List<object>();
            for (var i = 0; i < 5; i++)
            {
                var sample = await Command("整理这一段");
                warm.Add(new { menuMs = sample.MenuMs, openMs = sample.ShowMs, openUiAllocatedBytes = sample.ShowBytes });
                Click("取消整理");
                await WaitUntilAsync(() => Current().UserRevisionDraft == null && !Descendants<TurnRevisionEditor>(panel).Any(), "Warm editor close did not finish");
            }
            measurements["warm"] = warm;
            File.WriteAllText(Path.Combine(output, "revision-editor-performance.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: real rendition/menu, editable inline TeX source and untouched equation references, wrapped baselines, composed Chinese input beside references, split/merge, empty and protected paragraphs, selection, undo, heading numbering, draft, history and cancel");
        }
        finally { host.Close(); }
    }
}
