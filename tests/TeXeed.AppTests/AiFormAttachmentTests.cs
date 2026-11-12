using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiFormAttachmentsAsync(MainWindow owner, string output)
    {
        await CheckFormAttachmentCheckpointRecoveryAsync(output);
        var project = Path.Combine(output, "form-attachment-fixture.tex");
        var sourcePath = Path.Combine(output, "fixture.txt");
        var folderPath = Path.Combine(output, "fixture-folder");
        File.WriteAllText(sourcePath, "供用户练习的附件原文"); Directory.CreateDirectory(folderPath);
        using var handler = new FormHandler();
        using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        var source = AiForms.WithSubmission(new AiTurn("出题", "# 上传资料\n\n{{text:source}}\n\n{{file:source:请上传史料以核对原文}}"), null);
        book.Turns.Add(source);
        var origin = new AiFormAttachmentOrigin(1, book.Active.Id, book.Active.Title, source.Id, 1, "source", "请上传史料以核对原文");
        if (await panel.AddAttachmentsAsync([sourcePath], origin, () => false)) throw new Exception("A closed form accepted a late attachment");
        if (book.Entries.Count != 0) throw new Exception("A rejected attachment changed the draft");
        if (!await panel.AddAttachmentsAsync([sourcePath], origin, () => true) || !await panel.AddAttachmentsAsync([folderPath], origin, () => true)
            || !panel.AddFileReferences([sourcePath], origin, () => true))
            throw new Exception("Could not add fixture file and folder");
        var pending = book.Entries.ToArray();
        if (pending.Length != 3 || pending[0].Files.Single().BlobId is null || pending[1].Files.Single().ReferenceKind != "directory" || pending[2].Files.Single().ReferenceKind != "file")
            throw new Exception("Form attachments did not retain snapshot and directory path semantics");
        pending[2].AttachmentSelected = false;
        if (!AiFormAttachmentOrigins.TryGet(pending[0], out var persistedOrigin) || persistedOrigin != origin ||
            !pending[0].AttachmentFormOrigin.Contains(origin.Purpose, StringComparison.Ordinal))
            throw new Exception("Form attachment provenance or purpose is missing from attachment list");
        var catalog = new AiAttachmentCatalog([], pending);
        var catalogListing = catalog.Execute("list_attachments", "{\"offset\":0,\"limit\":10}");
        if (!catalogListing.Contains("answer_form", StringComparison.Ordinal) || !catalogListing.Contains(origin.Purpose, StringComparison.Ordinal) ||
            !catalogListing.Contains(origin.TurnId, StringComparison.Ordinal))
            throw new Exception("Attachment context management did not include the form provenance");
        var selected = AiPanel.SnapshotSelectedParts(pending[0], false, true, 1);
        if (!selected.Question.Contains(origin.Purpose, StringComparison.Ordinal) || !selected.Question.Contains(source.Id, StringComparison.Ordinal))
            throw new Exception("Form provenance was not included in the outgoing snapshot");
        await panel.SaveAsync();
        var reloaded = AiStorage.Load(project).Active;
        if (reloaded.Entries.Count != 3 || !AiFormAttachmentOrigins.TryGet(reloaded.Entries[1], out var loadedOrigin) || loadedOrigin != origin)
            throw new Exception("Form attachment provenance did not survive reopening the draft");
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 900, Height = 700, Content = panel }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[source.Id].Answers.Single(); await answer.RenderTask; host.UpdateLayout();
            var button = Descendants<FormAttachmentButton>(answer).Single();
            var text = Descendants<TextBox>(answer).Single(control => System.Windows.Automation.AutomationProperties.GetName(control) == "source");
            text.Text = "这段文字与文件使用相同字段名";
            if (!button.IsEnabled || !button.Content.ToString()!.Contains(origin.Purpose) || !button.Content.ToString()!.Contains("2 项") || button.ContextMenu?.Items.Count != 3)
                throw new Exception("File form did not show purpose, current selection and all three attachment actions");
            pending[1].AttachmentSelected = false; Invoke(panel, "RefreshFormEnabled");
            if (!button.Content.ToString()!.Contains("1 项")) throw new Exception("File form did not reflect a deselected attachment");
            pending[1].AttachmentSelected = true; Invoke(panel, "RefreshFormEnabled");
            static bool ContainsText(JsonElement node, string value) => node.ValueKind switch
            {
                JsonValueKind.String => node.GetString()?.Contains(value, StringComparison.Ordinal) == true,
                JsonValueKind.Array => node.EnumerateArray().Any(item => ContainsText(item, value)),
                JsonValueKind.Object => node.EnumerateObject().Any(item => ContainsText(item.Value, value)),
                _ => false
            };
            var composer = (TextBox)panel.FindName("Composer"); composer.Text = "";
            var send = panel.SubmitAsync(); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (button.IsEnabled || !button.Content.ToString()!.Contains("已提交 2 项") || !button.ToolTip.ToString()!.Contains("fixture.txt"))
                throw new Exception("Submitted file form lost its locked attachment summary");
            var pendingPath = AiTurnCheckpoint.PathsFor(project).Distinct(StringComparer.OrdinalIgnoreCase).Single();
            var journal = JsonSerializer.Deserialize<AiTurnCheckpoint.Pending>(File.ReadAllText(pendingPath))!;
            using var body = JsonDocument.Parse(handler.Body);
            var escapedText = JsonSerializer.Serialize("这段文字与文件使用相同字段名").Trim('"');
            if (journal.Turn.SentAnnotations.Count != 2 || !ContainsText(body.RootElement, "fixture.txt") ||
                !ContainsText(body.RootElement, "source") || !ContainsText(body.RootElement, "请上传史料") || !ContainsText(body.RootElement, escapedText))
                throw new Exception("The actual request or checkpoint lost the field association");
            var journalForm = AiForms.FromTurn(journal.Turn) ?? throw new Exception("Missing form snapshot in checkpoint");
            if (journalForm.Values.GetValueOrDefault("text:source") != "这段文字与文件使用相同字段名" || AiFormAttachmentOrigins.ReadSelection(journalForm, "source").Length != 2)
                throw new Exception("Text and files sharing one field id overwrote each other in the checkpoint");
            var recoveredDuringRequest = AiStorage.Load(project).Active;
            var recoveredForm = AiForms.SubmittedFor(recoveredDuringRequest, source.Id) ?? throw new Exception("Missing recovered form snapshot");
            if (recoveredForm.Values.GetValueOrDefault("text:source") != "这段文字与文件使用相同字段名" || AiFormAttachmentOrigins.ReadSelection(recoveredForm, "source").Length != 2)
                throw new Exception("Text and files sharing one field id were not restored after a crash");
            handler.Release.TrySetResult(); await send.WaitAsync(TimeSpan.FromSeconds(20)); await panel.StopAndSaveAsync();
            var stored = AiStorage.Load(project).Active;
            var last = stored.Turns.Last();
            if (last.SentAnnotations.Count != 2 || !AiForms.HistoryQuestion(last).Contains(origin.Purpose, StringComparison.Ordinal) ||
                !AiForms.HistoryQuestion(last).Contains(folderPath, StringComparison.Ordinal))
                throw new Exception("Later model history lost the attachment purpose or folder association");
            var savedSelection = AiForms.SubmittedFor(stored, source.Id) ?? throw new Exception("Missing frozen file form");
            var paths = AiFormAttachmentOrigins.ReadSelection(savedSelection, "source");
            if (paths.Length != 2 || paths.Contains(sourcePath, StringComparer.OrdinalIgnoreCase) || stored.Entries.Count != 1)
                throw new Exception("Unchecked attachment was included in the frozen field or removed from the draft");
            if (savedSelection.Values.GetValueOrDefault("text:source") != "这段文字与文件使用相同字段名")
                throw new Exception("The text field was overwritten by an attachment with the same id");
            var legacy = new AiForms.Submission(source.Id, new Dictionary<string, string> { ["source"] = "[\"旧附件.txt\"]" });
            if (!AiFormAttachmentOrigins.ReadSelection(legacy, "source").SequenceEqual(["旧附件.txt"]))
                throw new Exception("Legacy file-form snapshots were not read");
            if (AiFormAttachmentOrigins.ReadSelection(legacy, "source", allowLegacy: false).Length != 0)
                throw new Exception("An ambiguous legacy value was misidentified as an attachment");
            var newEmpty = new AiForms.Submission(source.Id, new Dictionary<string, string> { ["source"] = "[\"用户文字\"]" }) { Attachments = new() };
            if (AiFormAttachmentOrigins.ReadSelection(newEmpty, "source").Length != 0)
                throw new Exception("New-format text was incorrectly interpreted as an old file selection");
            var ambiguousTurn = AiForms.WithSubmission(new AiTurn("旧表单", "{{text:shared}}\n\n{{file:shared:选择材料}}"), null);
            var ambiguousConversation = new AiConversation(); ambiguousConversation.Turns.Add(ambiguousTurn);
            AiForms.Lock(ambiguousConversation, new AiForms.Submission(ambiguousTurn.Id, new())
            { Attachments = new(), AmbiguousLegacy = new() { ["shared"] = "原有待核对值" } });
            var ambiguousState = (AiFormAttachmentState)Invoke(panel, "FormAttachmentState", ambiguousConversation, ambiguousTurn, "shared")!;
            if (ambiguousState.Count != 0 || !ambiguousState.Description.Contains("原有待核对值", StringComparison.Ordinal))
                throw new Exception("The file control hid an unresolved ambiguous legacy value");
            Console.WriteLine("PASS: file-form attachment snapshots, directory references, provenance, send, checkpoint and history.");
        }
        finally { handler.Release.TrySetResult(); host.Close(); }
    }

    private static async Task CheckFormAttachmentCheckpointRecoveryAsync(string output)
    {
        foreach (var explicitPart in new[] { false, true })
        foreach (var change in new[] { "none", "file", "question", "origin", "extension", "selection" })
        {
            var project = Path.Combine(output, $"form-attachment-recovery-{explicitPart}-{change}.tex");
            var book = new AiNotebook();
            var origin = new AiFormAttachmentOrigin(1, book.Active.Id, "练习会话", "form-turn", 1, "file-1", "核对资料");
            var entry = new AiEntry { Title = "附件：材料.txt", EntryKind = "attachment", Files = [new("材料.txt", "text/plain", "Zg==")] };
            AiFormAttachmentOrigins.Set(entry, origin);
            entry.AdditionalData!["future"] = JsonSerializer.SerializeToElement("original");
            book.Entries.Add(entry);
            var accepted = AiPanel.SnapshotSelectedParts(entry, false, true, 1);
            if (explicitPart) { accepted.SentReferencePart = false; accepted.SentAttachmentPart = true; }
            var turn = new AiTurn("提交资料", "") { Prompt = "", SentAnnotations = [accepted] };
            AiStorage.Save(project, book.Snapshot());
            var journal = await AiTurnCheckpoint.StartAsync(project, book.Active.Id, book.Active.Title, turn, new AiStreamBuffer());
            try
            {
                switch (change)
                {
                    case "file": entry.Files.Add(new("追加.txt", "text/plain", "Zg==")); break;
                    case "question": entry.Question = "后来添加的说明"; break;
                    case "origin": AiFormAttachmentOrigins.Set(entry, origin with { Purpose = "用户后来更改用途" }); break;
                    case "extension": entry.AdditionalData!["future"] = JsonSerializer.SerializeToElement("user-changed"); break;
                    case "selection": entry.AttachmentSelected = false; break;
                }
                if (change != "none") AiStorage.Save(project, book.Snapshot());
                var recovered = AiStorage.Load(project);
                if (recovered.Turns.Count != 1 || recovered.Turns[0].SentAnnotations.Count != 1)
                    throw new Exception("Checkpoint did not recover the submitted form attachment");
                if (change == "none" && recovered.Entries.Count != 0)
                    throw new Exception("Accepted form attachment reappeared as pending after checkpoint recovery");
                if (change != "none" && recovered.Entries.Count != 1)
                    throw new Exception($"Checkpoint recovery consumed a form attachment changed after sending: explicit={explicitPart}, change={change}");
            }
            finally { await journal.StopAsync(); journal.Commit(); }
        }
        Console.WriteLine("PASS: accepted form attachments recover once, and subsequent draft changes are preserved.");
    }
}
