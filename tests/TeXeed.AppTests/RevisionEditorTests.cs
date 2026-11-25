using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckRevisionEditorAsync(MainWindow owner, string output)
    {
        static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
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
            List<AiRevisionRow> Rows() => Current().UserRevisionDraft!.Rows;
            void Click(string label)
            {
                var button = Descendants<Button>(Editor()).Single(b => b.Content as string == label);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            async Task<(double MenuMs, double ShowMs, long ShowBytes)> Command(string name)
            {
                var menuStart = Stopwatch.GetTimestamp();
                var menu = View().Answers.Single().ContextMenu!;
                menu.PlacementTarget = View().Answers.Single(); menu.IsOpen = true; await Idle();
                var item = menu.Items.OfType<MenuItem>().Single(it => it.Header as string == name);
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
            var cold = await Command("整理这一轮");
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
            Check(!mathHostView.IsKeyboardFocusWithin && mathHostView.Child is TextBlock { Text: sourceLine } &&
                !Descendants<ChatFormula>(Editor()).Any(f => f.Source == "A"), "Inline TeX must be visible as editable source even in an unfocused paragraph");
            Capture(host, Path.Combine(output, "revision-editor.png"));
            var mathEditor = Line(4);
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
            var textView = refEditor.TextArea.TextView;
            var visual = textView.VisualLines.Single();
            var firstReferenceLine = visual.GetTextLine(visual.GetVisualColumn(referenceStart));
            var lastReferenceLine = visual.GetTextLine(visual.GetVisualColumn(referenceStart + referenceSource.Length - 1));
            Check(!ReferenceEquals(firstReferenceLine, lastReferenceLine) && Math.Abs(firstReferenceLine.Baseline - lastReferenceLine.Baseline) < 1,
                "The protected equation reference did not wrap across normally aligned visual lines");
            Capture(host, Path.Combine(output, "revision-editor-inline-wrap.png"));
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
            await KeyIn(3, Key.Back, 0);
            Check(Rows().Count == 7 && Rows()[2].Links.Select(l => l.Id).SequenceEqual(linkIds) && Line(2).CaretOffset == 5, "Backspace failed to merge at original seam");
            await KeyIn(2, Key.Delete, 0, Line(2).Text.Length);
            Check(Rows().Count == 6 && Rows()[1].Links.Select(l => l.Id).SequenceEqual(linkIds) && Line(1).CaretOffset == 4, "Clearing a linked row did not move both links to the previous text end");
            Click("撤销"); await Idle();
            Check(Rows().Count == 7 && Rows()[2].Links.Count == 2, "Undo lost a paragraph or its references");
            var rightId = Rows()[2].Id;
            await KeyIn(1, Key.Delete, Line(1).Text.Length);
            Check(Rows()[1].Id == rightId && Rows()[1].Text == "承接段。命题甲中与乙。" && Line(1).CaretOffset == 4, "Nonempty Delete failed to join the next row at its original seam");
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
            Check(Rows()[1].Text == "承接段。" && Line(1).CaretOffset == 0, "Empty Delete did not put the caret at next text start");
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
            Descendants<ComboBox>(Editor()).Single().SelectedIndex = 1; await Idle();
            Check(Rows()[1].Level == 1 && Descendants<TextBlock>(Editor()).Any(t => t.Text == "[1:1] "), "Edited heading level was not renumbered");
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
            await Command("整理这一轮"); var first = Line(0); first.TextArea.Focus(); first.CaretOffset = 0; Type(first, "临时");
            Click("取消整理");
            await WaitUntilAsync(() => Current().UserRevisionDraft == null && !Descendants<TurnRevisionEditor>(panel).Any(), "Cancel left an active editor");
            Check(Current().RevisionHistory!.Count == 2 && !Current().Answer.Contains("临时"), "Cancel changed the saved answer");
            var warm = new List<object>();
            for (var i = 0; i < 5; i++)
            {
                var sample = await Command("整理这一轮");
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
