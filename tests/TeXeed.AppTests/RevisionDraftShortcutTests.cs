using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckRevisionDraftShortcutAsync(MainWindow window, string output)
    {
        async Task Layout()
        {
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        static string Fixture(string folder)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "main.tex");
            File.WriteAllText(path, @"\documentclass{article}\begin{document}Draft test\end{document}");
            return path;
        }
        var root = Path.Combine(output, "shortcut-" + Guid.NewGuid().ToString("N"));
        var project = Fixture(Path.Combine(root, "first"));
        var other = Fixture(Path.Combine(root, "other"));
        const string paragraph = "**同一句话**，含有 $A$。";
        var turn = new AiTurn("辨认相同段落", paragraph + "\n\n过渡。\n\n" + paragraph);
        var unrelated = new AiTurn("另一轮", "保持这轮的稿本。");
        var book = AiStorage.Load(project);
        book.Turns.Add(turn);
        book.Turns.Add(unrelated);
        AiStorage.Save(project, book);

        async Task<AiPanel> Open()
        {
            await CallAsync(window, "OpenAsync", project);
            var panel = (AiPanel)Field(window, "_aiPanel")!;
            await CallAsync(window, "OpenAiConversationAsync", panel.ConversationId);
            await Layout();
            return panel;
        }
        async Task<RevisionLineEditor> OpenLastParagraph(AiPanel panel)
        {
            var answer = Descendants<AiTurnView>(panel).Single(view => view.Turn.Id == turn.Id).Answers.Single();
            await answer.RenderTask; await Layout();
            var last = answer.Document.Blocks.OfType<Paragraph>().Last(p =>
                new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("同一句话", StringComparison.Ordinal));
            var origin = last.ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft;
            answer.CaptureContextParagraph(new Point(origin.X + 3, origin.Y + 5));
            var menu = answer.ContextMenu!;
            menu.PlacementTarget = answer; menu.IsOpen = true; await Layout();
            var command = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "整理这一段"));
            menu.IsOpen = false;
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Layout();
            var editor = Descendants<TurnRevisionEditor>(panel).Single();
            var rows = Descendants<RevisionLineHost>(editor).ToList();
            if (rows.Count != 3 || rows[2].Editor is not { IsKeyboardFocusWithin: true } line ||
                Descendants<RevisionLineEditor>(editor).Count(node => node.IsVisible) != 1)
                throw new Exception("Right-clicking the second occurrence of typeset content did not focus its own source paragraph");
            return line;
        }
        var panel = await Open();
        var line = await OpenLastParagraph(panel);
        const string suffix = "这次只存草稿。";
        line.CaretOffset = line.Text.Length;
        var composition = new TextComposition(InputManager.Current, line.TextArea, suffix);
        line.TextArea.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
        ((DispatcherTimer)Field(panel, "_saveTimer")!).Stop(); // Isolate the shortcut from delayed autosave.
        var generation = (int)Field(window, "_pdfGeneration")!;
        var original = new byte[256];
        if (!GetKeyboardState(original)) throw new Exception("Could not capture the test thread's modifier keys");
        var ctrl = new byte[256];
        ctrl[0x11] = ctrl[0xA2] = 0x80;
        try
        {
            if (!SetKeyboardState(ctrl)) throw new Exception("Could not set Ctrl for the draft shortcut");
            var key = CreateKey(window, Key.S, Keyboard.PreviewKeyDownEvent);
            line.TextArea.RaiseEvent(key);
            if (!key.Handled) throw new Exception("The draft shortcut was not handled in the main window");
        }
        finally { SetKeyboardState(original); }
        bool DraftSaved() => AiStorage.Load(project).Turns.Single(t => t.Id == turn.Id).UserRevisionDraft?.Rows[^1].Text == paragraph + suffix;
        await WaitUntilAsync(DraftSaved, "Ctrl+S did not persist the revised paragraph");
        var stored = AiStorage.Load(project).Turns.Single(t => t.Id == turn.Id);
        if (stored.Answer != turn.Answer || stored.RevisionHistory?.Count > 0 || !line.IsKeyboardFocusWithin ||
            (bool)Field(window, "_building")! || (int)Field(window, "_pdfGeneration")! != generation)
            throw new Exception("Ctrl+S published a revision, left the paragraph or triggered a TeX project build");
        await CallAsync(window, "OpenAsync", other);
        panel = await Open();
        line = await OpenLastParagraph(panel);
        if (line.Text != paragraph + suffix) throw new Exception("Reopening the project and paragraph lost or misrouted its draft");
        Descendants<Button>(panel).Single(b => Equals(b.Content, "完成整理")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => AiStorage.Load(project).Turns.Single(t => t.Id == turn.Id).RevisionHistory?.Count == 2,
            "Finishing the restored draft did not create the turn history");
        var finished = AiStorage.Load(project);
        var revised = finished.Turns.Single(t => t.Id == turn.Id);
        var untouched = finished.Turns.Single(t => t.Id == unrelated.Id);
        if (revised.UserRevisionDraft != null || revised.Answer != turn.Answer + suffix ||
            revised.RevisionHistory![1].Author != "user" || untouched.RevisionHistory?.Count > 0 || untouched.Answer != unrelated.Answer)
            throw new Exception("Finishing the paragraph draft changed the wrong history or text");
        Console.WriteLine("PASS: main-window paragraph menu distinguishes repeated Markdown, Ctrl+S saves only the draft, project reopen and completion update just the owning turn");
    }
}
