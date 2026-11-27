using System.IO;
using System.Security.AccessControl;
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
    private static async Task CheckRevisionStorageSharingAsync(string project, AiNotebook book)
    {
        var source = book.Turns[0];
        // Windows needs DELETE access on the file during an atomic rename.
        using (var replacing = new FileInfo(AiStorage.NotebookPath(project)).Create(
            FileMode.Open, FileSystemRights.Read | FileSystemRights.Delete, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.None, null))
        {
            var persisted = AiStorage.Load(project);
            if (persisted.Turns.Count != book.Turns.Count || persisted.Turns[0].Answer != source.Answer)
                throw new Exception("Reading during atomic replacement lost the saved turn");
        }

        var store = new AiWorkspaceStore(project, book);
        var journal = await AiTurnCheckpoint.StartAsync(project, book.Active.Id, book.Active.Title, source, new AiStreamBuffer());
        await journal.StopAsync();
        var previousDraft = book.Draft;
        var journalPath = AiTurnCheckpoint.PathFor(project);
        using (var checkpointReader = File.OpenRead(journalPath))
        {
            const string draft = "保存后等待清理恢复文件";
            await store.SaveWithMutationAsync(() => book.Draft = draft, () => book.Draft = previousDraft);
            var reopened = AiStorage.Load(project);
            if (book.Draft != draft || reopened.Draft != draft || reopened.Turns.Count != book.Turns.Count || !File.Exists(journalPath))
                throw new Exception("A locked journal rolled back a committed edit or duplicated the turn");
        }
        book.Draft = previousDraft;
        await store.SaveAsync();
        if (File.Exists(journalPath)) throw new Exception("The next successful save did not retire the journal");
    }

    private static void RevisionShortcut(Window window, UIElement target, Key command)
    {
        var original = new byte[256];
        if (!GetKeyboardState(original)) throw new Exception("Could not capture the test thread's modifier keys");
        var ctrl = new byte[256];
        ctrl[0x11] = ctrl[0xA2] = 0x80;
        try
        {
            if (!SetKeyboardState(ctrl)) throw new Exception("Could not set Ctrl for the revision shortcut");
            var key = CreateKey(window, command, Keyboard.PreviewKeyDownEvent);
            target.RaiseEvent(key);
            if (!key.Handled) throw new Exception($"Ctrl+{command} was not handled by the active editor");
        }
        finally { SetKeyboardState(original); }
    }

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
        await CheckRevisionStorageSharingAsync(project, book);

        async Task<AiPanel> Open()
        {
            await CallAsync(window, "OpenAsync", project);
            var panel = (AiPanel)Field(window, "_aiPanel")!;
            await CallAsync(window, "OpenAiConversationAsync", panel.ConversationId);
            await Layout();
            return panel;
        }
        async Task<RevisionLineEditor> OpenLastParagraph(AiPanel panel, AiTurn which, string words, int rowCount)
        {
            var view = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == which.Id);
            var answer = view.Answers.Single();
            await answer.RenderTask; await Layout();
            var last = answer.Document.Blocks.OfType<Paragraph>().Last(p =>
                new TextRange(p.ContentStart, p.ContentEnd).Text.Contains(words, StringComparison.Ordinal));
            var origin = last.ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft;
            answer.CaptureContextParagraph(new Point(origin.X + 3, origin.Y + 5));
            var menu = answer.ContextMenu!;
            menu.PlacementTarget = answer; menu.IsOpen = true; await Layout();
            var command = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "整理这一段"));
            menu.IsOpen = false;
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Layout();
            var editor = Descendants<TurnRevisionEditor>(view).Single();
            var rows = Descendants<RevisionLineHost>(editor).ToList();
            if (rows.Count != rowCount || rows[^1].Editor is not { IsKeyboardFocusWithin: true } line ||
                Descendants<RevisionLineEditor>(editor).Count(node => node.IsVisible) != 1)
                throw new Exception("Right-clicking the second occurrence of typeset content did not focus its own source paragraph");
            return line;
        }
        var panel = await Open();
        var line = await OpenLastParagraph(panel, turn, "同一句话", 3);
        const string suffix = "核对后收进这一轮。";
        void Append(RevisionLineEditor input, string value)
        {
            input.CaretOffset = input.Text.Length;
            var composition = new TextComposition(InputManager.Current, input.TextArea, value);
            input.TextArea.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
        }
        Append(line, suffix);
        await WaitUntilAsync(() => AiStorage.Load(project).Turns.Single(t => t.Id == turn.Id).UserRevisionDraft?.Rows[^1].Text == paragraph + suffix,
            "A draft did not autosave while the source paragraph remained open");
        await CallAsync(window, "OpenAsync", other);
        panel = await Open();
        const string otherDraft = "还在整理。";
        var otherLine = await OpenLastParagraph(panel, unrelated, unrelated.Answer, 1);
        Append(otherLine, otherDraft);
        line = await OpenLastParagraph(panel, turn, "同一句话", 3);
        if (line.Text != paragraph + suffix) throw new Exception("Reopening the project lost or misrouted the autosaved draft");
        Append(line, " $A");
        var generation = (int)Field(window, "_pdfGeneration")!;
        RevisionShortcut(window, line.TextArea, Key.S);
        var editor = Descendants<TurnRevisionEditor>(Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == turn.Id)).Single();
        await WaitUntilAsync(() => Descendants<TextBlock>(editor).Any(t => t.IsVisible && t.Text.Contains("定界符")),
            "Ctrl+S did not report the unpaired TeX delimiter");
        if (AiStorage.Load(project).Turns.Single(t => t.Id == turn.Id).RevisionHistory?.Count > 0 ||
            !Descendants<TurnRevisionEditor>(panel).Any(v => v.IsVisible))
            throw new Exception("Invalid TeX entered the revision history or lost the editor");
        var lastHost = Descendants<RevisionLineHost>(editor).Last();
        lastHost.Focus(); line = lastHost.Editor!;
        line.Select(line.Text.Length - 3, 3);
        line.TextArea.RaiseEvent(CreateKey(window, Key.Delete, Keyboard.PreviewKeyDownEvent));
        await Layout();
        line = Descendants<RevisionLineHost>(editor).Last().Editor ?? throw new Exception("Correcting the formula lost the insertion point");
        if (line.Text != paragraph + suffix) throw new Exception($"The failed-save draft was not editable: {line.Text}");
        line.TextArea.RaiseEvent(CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent));
        await Layout();
        if (Descendants<RevisionLineEditor>(editor).Any(l => l.IsVisible) || !editor.IsKeyboardFocusWithin)
            throw new Exception("ESC did not restore the paragraph to preview while leaving the revision focused");
        RevisionShortcut(window, editor, Key.S);
        if (editor.IsLoaded) RevisionShortcut(window, editor, Key.S); // Second key arrives during the disk write.
        await WaitUntilAsync(() => AiStorage.Load(project).Turns.Single(t => t.Id == turn.Id).RevisionHistory?.Count == 2 &&
            !editor.IsLoaded, "Ctrl+S after a failed save and ESC did not finish the owning revision");
        if ((bool)Field(window, "_building")! || (int)Field(window, "_pdfGeneration")! != generation)
            throw new Exception("Finishing a paragraph revision also triggered a project build");
        await CallAsync(window, "OpenAsync", other);
        panel = await Open();
        if (Descendants<TurnRevisionEditor>(panel).Any() ||
            !Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == turn.Id).Answers.Single().Markdown.EndsWith(suffix))
            throw new Exception("Reopening the saved turn did not show its revised text in reading mode");
        var finished = AiStorage.Load(project);
        var revised = finished.Turns.Single(t => t.Id == turn.Id);
        var untouched = finished.Turns.Single(t => t.Id == unrelated.Id);
        if (revised.UserRevisionDraft != null || revised.Answer != turn.Answer + suffix ||
            revised.RevisionHistory![1].Author != "user" || untouched.RevisionHistory?.Count > 0 || untouched.Answer != unrelated.Answer ||
            untouched.UserRevisionDraft?.Rows.Single().Text != unrelated.Answer + otherDraft)
            throw new Exception("Finishing the paragraph draft changed the wrong history or text");
        var activeBook = (AiNotebook)Field(panel, "_book")!;
        activeBook.Active.Permission = AiPermission.FullAccess;
        line = await OpenLastParagraph(panel, revised, "同一句话", 3);
        const string beforeAi = "这句由用户先写。";
        Append(line, beforeAi);
        ((TextBox)panel.FindName("Composer")).Focus();
        var runner = (AiRevisionRunner)Invoke(panel, "CreateRevisionRunner",
            new AiRevisionRequest(turn.Id, Guid.NewGuid().ToString("N")), (Action<bool>)(_ => { }))!;
        var result = await runner("read_answer_revision", """{"start_row":0,"max_rows":10}""", default);
        await Layout();
        var after = AiStorage.Load(project).Turns.Single(t => t.Id == turn.Id);
        if (!result.Contains(beforeAi) || after.UserRevisionDraft != null || after.Answer != turn.Answer + suffix + beforeAi ||
            after.RevisionHistory is not [{ Id: "original" }, { Author: "user" }, { Author: "user" }] ||
            Descendants<TurnRevisionEditor>(panel).Any(e => e.IsVisible))
            throw new Exception("Reading for AI failed to save and close the actual paragraph editor");
        line = await OpenLastParagraph(panel, after, "同一句话", 3);
        const string concurrentEdit = "同时发送改稿命令。";
        Append(line, concurrentEdit);
        var saveGate = (SemaphoreSlim)Field((AiWorkspaceStore)Field(panel, "_store")!, "_gate")!;
        await saveGate.WaitAsync();
        Task<string> concurrentRead;
        try
        {
            RevisionShortcut(window, line.TextArea, Key.S);
            concurrentRead = runner("read_answer_revision", """{"start_row":0,"max_rows":10}""", default);
            await Layout();
            if (concurrentRead.IsCompleted || AiStorage.Load(project).Turns[0].Answer != after.Answer)
                throw new Exception("An AI reader used uncommitted text while Ctrl+S waited for disk");
        }
        finally { saveGate.Release(); }
        var concurrentResult = await concurrentRead;
        var completed = AiStorage.Load(project).Turns[0];
        if (!concurrentResult.Contains(concurrentEdit) || completed.Answer != after.Answer + concurrentEdit ||
            completed.UserRevisionDraft != null || completed.RevisionHistory!.Count != after.RevisionHistory!.Count + 1)
            throw new Exception("Concurrent Ctrl+S and AI reading failed to share one durable user revision");
        Console.WriteLine("PASS: main-window paragraph menu distinguishes repeated Markdown, autosaved draft recovery, failed TeX validation, retry from ESC preview and Ctrl+S completion change only the owning turn");
    }
}
