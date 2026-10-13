using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiAsync(MainWindow window, string output)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var lockedDestination = Path.Combine(output, "atomic-config.json");
        var pendingFile = lockedDestination + ".tmp";
        File.WriteAllText(lockedDestination, "old"); File.WriteAllText(pendingFile, "new");
        var held = new FileStream(lockedDestination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(70); held.Dispose(); });
        AtomicFile.Replace(pendingFile, lockedDestination); await release;
        Check(File.ReadAllText(lockedDestination) == "new" && !File.Exists(pendingFile), "Temporary Windows file lock broke atomic config persistence");
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var editor = (LatexEditor)window.FindName("LeftEditor");
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var menu = (MenuItem)window.FindName("AiMenuItem");
        Check(ReferenceEquals(((Menu)window.FindName("MainMenu")).Items.Cast<object>().Last(), menu), "AI must be the final main menu");
        Check(!menu.HasItems, "AI menu must have no submenu"); menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); window.UpdateLayout();
        Check(tabs.Owner(TabWorkspace.AiKey) == TabSide.Right && ai.IsVisible, "AI button did not open right AI tab");
        Check(!tabs.Sources.Contains(TabWorkspace.AiKey), "AI tab treated as source file");
        var composer = (TextBox)ai.FindName("Composer");
        composer.Focus(); window.UpdateLayout();
        Check(composer.VerticalContentAlignment == VerticalAlignment.Top && composer.GetRectFromCharacterIndex(0).Top < 18, "Composer caret must begin near the top padding");
        using (var catalog = JsonDocument.Parse("{\"slug\":\"gpt-6.1-sol\",\"display_name\":\"Sol\",\"supported_reasoning_levels\":[{\"effort\":\"low\"},{\"effort\":\"high\"},{\"effort\":\"ultra\"}]}"))
        {
            var parsed = AiModel.FromCatalog(catalog.RootElement);
            Check(parsed.ReasoningEfforts.SequenceEqual(new[] { "low", "high" }), "Model efforts must follow the catalog and public Responses contract");
        }
        var strip = (EditorTabStrip)window.FindName("RightFileTabs");
        var aiTab = ((StackPanel)strip.FindName("Items")).Children.Cast<Border>().Single(b => (string)b.Tag == TabWorkspace.AiKey);
        var aiButtons = ((Grid)aiTab.Child).Children.Cast<Button>().ToArray();
        Check(aiButtons.Length == 3 && aiButtons[1].ToolTip?.ToString() == "AI 设置" && aiButtons[2].ToolTip?.ToString()?.Contains("关闭") == true, "AI title/gear/close ordering is incorrect");
        aiButtons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Check(tabs.FocusedTab == TabWorkspace.AiSettingsKey && ai.Settings.IsVisible && !ai.IsVisible, "AI gear did not open separate settings tab");
        Check(!tabs.Sources.Contains(TabWorkspace.AiSettingsKey), "AI settings treated as source");
        Invoke(window, "OpenAiTab");
        Invoke(window, "MoveWorkspaceTab", TabWorkspace.AiKey, TabSide.Left, 0, false); window.UpdateLayout();
        Check(tabs.Owner(TabWorkspace.AiKey) == TabSide.Left && ai.IsVisible, "AI tab did not move left");
        Invoke(window, "CloseTabs", TabWorkspace.AiKey, TabCloseScope.This); Check(tabs.Owner(TabWorkspace.AiKey) is null, "AI close failed");
        editor.Select(0, 14); var text = editor.SelectedText;
        Invoke(window, "CollectSourceForAi", editor); window.UpdateLayout();
        var book = (AiNotebook)Field(ai, "_book")!;
        Check(book.Entries.Count == 1 && book.Entries[0].Sources.Single().Text == text, "Source snapshot differs from selected text");
        Check(editor.ContextMenu.Items.OfType<MenuItem>().Count() >= 12, "Editor context menu missing edit commands");
        book.Entries[0].Question = "解释这一行"; await ai.SaveAsync();
        var project = (TexProject)Field(window, "_project")!;
        var restored = AiStorage.Load(project.MainPath); Check(restored.Entries.Single().Question == "解释这一行", "Project AI notebook did not persist");
        Check(AiStorage.NotebookPath(project.MainPath) != AiStorage.NotebookPath(project.MainPath + "x"), "Project notebook isolation failed");
        var credentials = new AiCredentials { ApiKey = "test-key-never-send", UseApiKey = true }; credentials.Save();
        Check(AiCredentials.Load().ApiKey == credentials.ApiKey && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AiStorage.Home, "credentials.dpapi"))).Contains(credentials.ApiKey), "DPAPI secret storage failed");
        var identity = MakeTestIdentity();
        Check(ChatGptLogin.ValidateIdentity(identity.Jwt, "test-client", "test-nonce", identity.Keys).Subject == "test-user", "Valid OIDC identity rejected");
        foreach (var (client, nonce) in new[] { ("bad-client", "test-nonce"), ("test-client", "bad-nonce") })
        { var rejected = false; try { ChatGptLogin.ValidateIdentity(identity.Jwt, client, nonce, identity.Keys); } catch (CryptographicException) { rejected = true; } Check(rejected, "OIDC binding not checked"); }
        var sigRejected = false; try { ChatGptLogin.ValidateIdentity(identity.Jwt[..^10] + "AAAAAAAAAA", "test-client", "test-nonce", identity.Keys); } catch (CryptographicException) { sigRejected = true; } Check(sigRejected, "Invalid signature accepted");
        var files = new[] { new ProjectFile("C:/project/main.tex", "main.tex", ProjectFileKind.Tex, "first\nA compact proof.\nlast") };
        Check(AiResponses.ExecuteTool("search_source", "{\"query\":\"compact proof\"}", files).Contains("main.tex:2"), "Source search lacks location");
        Check(AiResponses.ExecuteTool("read_source", "{\"file\":\"../../secrets\",\"start_line\":1,\"line_count\":20}", files).Contains("不在"), "Tool escaped project snapshot");
        using var handler = new AiFakeHandler(); using var connection = new AiConnection(handler);
        var answer = new StringBuilder();
        await new AiResponses(connection).RunAsync("test-model", restored.Entries, [], files, x => answer.Append(x), _ => { }, CancellationToken.None);
        Check(answer.ToString() == "定位到 main.tex:2。" && handler.Requests == 2, "Streaming tool roundtrip failed");
        Check(handler.SawToolOutput && handler.SawContract, "Responses request broke OAuth-compatible streaming contract");
        using (var eventHandler = new AiFakeHandler { ToolsInEvents = true })
        using (var eventConnection = new AiConnection(eventHandler))
        {
            var streamed = new StringBuilder();
            await new AiResponses(eventConnection).RunAsync("test-model", restored.Entries, [], files, x => streamed.Append(x), _ => { }, CancellationToken.None);
            Check(eventHandler.Requests == 2 && eventHandler.SawToolOutput && streamed.ToString().Contains("main.tex:2"), "Terminal output omission lost item.done tool call");
        }
        using (var emptyConnection = new AiConnection(new AiFakeHandler { Empty = true }))
        {
            var rejected = false;
            try { await new AiResponses(emptyConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Empty completed response must not mark a question answered");
        }
        using var brokenConnection = new AiConnection(new AiFakeHandler { Broken = true }); var failed = false;
        try { await new AiResponses(brokenConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        Check(failed, "Interrupted stream treated as success");
        var batchHandler = new AiFakeHandler();
        using (var batchPanel = new AiPanel(new AiConnection(batchHandler)))
        {
            await batchPanel.SetProjectAsync(Path.Combine(output, "batch-fixture.tex")); batchPanel.ProjectFiles = () => files;
            var modelBox = (ComboBox)batchPanel.Settings.FindName("Models");
            var testModels = new[] { new AiModel("test-model", "测试模型") { ReasoningEfforts = ["low", "high"] }, new AiModel("second-model", "第二个模型") { ReasoningEfforts = ["low", "high"] } };
            modelBox.ItemsSource = testModels; modelBox.SelectedIndex = 0;
            await batchPanel.Settings.SaveSelectionAsync(CancellationToken.None);
            var chatModel = (ComboBox)batchPanel.FindName("ModelPicker");
            Check(chatModel.Items.Count == 2 && (chatModel.SelectedItem as AiModel)?.Id == "test-model", "Chat model picker did not mirror settings");
            chatModel.SelectedItem = testModels[1];
            await batchPanel.Settings.SaveSelectionAsync(CancellationToken.None);
            var chatEffort = (ComboBox)batchPanel.FindName("EffortPicker");
            chatEffort.SelectedItem = chatEffort.Items.Cast<AiEffort>().Single(e => e.Value == "high");
            await batchPanel.Settings.SaveSelectionAsync(CancellationToken.None);
            Check(batchPanel.Settings.SelectedModel?.Id == "second-model" && batchPanel.Settings.SelectedEffort == "high", "Chat choices did not update settings");
            Check(AiCredentials.Load().Model == "second-model" && AiCredentials.Load().ReasoningEffort == "high", "Selected model/effort did not persist");
            var first = new AiEntry { Question = "第一问" }; var second = new AiEntry { Question = "第二问" }; var skip = new AiEntry { Question = "不发送", Selected = false };
            batchPanel.AddEntry(first); batchPanel.AddEntry(second); batchPanel.AddEntry(skip); await batchPanel.SubmitAsync();
            Check(first.State == "已完成" && second.State == "已完成" && skip.State == "待提交", "Batch submission did not honor selected entries");
            var batchBook = (AiNotebook)Field(batchPanel, "_book")!;
            Check(batchBook.Turns.Count == 1 && batchBook.Turns[0].Question.Contains("第一问") && batchBook.Turns[0].Question.Contains("第二问") && !batchBook.Turns[0].Question.Contains("不发送"), "Batch history mixed unselected questions");
            Check(batchHandler.LastModel == "second-model" && batchHandler.LastEffort == "high", "Actual Responses request ignored model or reasoning selection");
            var firstId = batchBook.Active.Id;
            var firstTurnId = batchBook.Turns[0].Id;
            ((TextBox)batchPanel.FindName("Composer")).Text = "草稿 A";
            await batchPanel.NewConversationAsync();
            var secondId = batchBook.Active.Id;
            Check(secondId != firstId && batchBook.Turns.Count == 0 && batchBook.Entries.Count == 0, "New conversation inherited messages/context");
            ((TextBox)batchPanel.FindName("Composer")).Text = "草稿 B";
            batchPanel.AddEntry(new AiEntry { Title = "B 的圈点" });
            await batchPanel.SelectConversationAsync(firstId);
            Check(((TextBox)batchPanel.FindName("Composer")).Text == "草稿 A" && batchBook.Entries.Count == 3, "Switching conversation lost its draft/context");
            Check(AiHistoryWindow.Search(batchBook, "第一问").Single().Id == firstId, "History search did not find question text");
            batchBook.Turns.Add(new("问题 C", "可搜索的回答关键词"));
            Check(AiHistoryWindow.Search(batchBook, "回答关键词").Single().Id == firstId, "History search did not find answer text");
            await batchPanel.DeleteTurnAsync(firstTurnId);
            Check(!batchBook.Turns.Any(t => t.Id == firstTurnId) && batchBook.Turns.Count == 1, "Deleting a message removed the wrong turns");
            await batchPanel.DeleteConversationAsync(firstId);
            Check(batchBook.Active.Id == secondId && batchBook.Conversations.Count == 1 && ((TextBox)batchPanel.FindName("Composer")).Text == "草稿 B", "Deleting active conversation did not restore another conversation");
            var savedHistory = AiStorage.Load(Path.Combine(output, "batch-fixture.tex"));
            Check(savedHistory.Conversations.Count == 1 && savedHistory.Active.Id == secondId && savedHistory.Draft == "草稿 B", "Conversation deletion did not persist");
            await batchPanel.DeleteConversationAsync(secondId);
            Check(batchBook.Conversations.Count == 1 && batchBook.Turns.Count == 0 && batchBook.Active.Id != secondId, "Deleting last conversation must yield a fresh empty chat");
        }
        var legacyProject = Path.Combine(output, "legacy-ai.tex");
        var legacyPath = AiStorage.NotebookPath(legacyProject); Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { Entries = restored.Entries, Turns = new[] { new { Question = "[a6eb2178] 原来的问题", Answer = "原来的回答" } }, Draft = "原来的草稿" }));
        var migrated = AiStorage.Load(legacyProject);
        Check(migrated.Conversations.Count == 1 && migrated.Turns.Single().Answer == "原来的回答" && migrated.Draft == "原来的草稿" && migrated.Entries.Count == 1, "Legacy conversation migration lost data");
        Check(migrated.Turns.Single().DisplayQuestion == "原来的问题", "Legacy internal ID prefix leaked into displayed message");
        var emptyLegacy = new AiTurn("旧的空回答问题", "");
        Check(emptyLegacy.DisplayAnswer.Contains("未保存回答"), "Legacy empty answer is not explained to the user");
        var nextInput = AiResponses.MakeInput([new AiEntry { Question = "新的问题" }], [emptyLegacy]);
        Check(nextInput.Count == 1 && !nextInput.ToJsonString().Contains("旧的空回答问题"), "Legacy empty replies polluted the next request context");
        AiStorage.Save(legacyProject, migrated.Snapshot());
        using (var migratedJson = JsonDocument.Parse(File.ReadAllText(legacyPath)))
            Check(migratedJson.RootElement.TryGetProperty("Conversations", out _) && !migratedJson.RootElement.TryGetProperty("Entries", out _), "History schema duplicates media at notebook root");
        using (var failedPanel = new AiPanel(new AiConnection(new AiFakeHandler { Broken = true })))
        {
            await failedPanel.SetProjectAsync(Path.Combine(output, "failure-fixture.tex")); failedPanel.ProjectFiles = () => files;
            var models = (ComboBox)failedPanel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("test-model", "测试模型") }; models.SelectedIndex = 0;
            var entry = new AiEntry { Question = "保留待重试" }; failedPanel.AddEntry(entry); await failedPanel.SubmitAsync();
            Check(entry.Selected && entry.State == "未完成，可重试", "Failed batch lost selection or marked successful");
        }
        var switched = false;
        using (var changedConnection = new AiConnection(new AiFakeHandler { SwitchAccount = true }))
        {
            try { await new AiResponses(changedConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, CancellationToken.None); }
            catch (InvalidOperationException) { switched = true; }
        }
        Check(switched, "Mid-request credential switch was not rejected"); credentials.Save();
        var crop = new PdfPageCrop(20, 400, [new(50, 100), new(300, 350)]);
        var bands = PdfPreview.SelectionBands(new Rect(0, 20, 200, 60), new Size(400, 100), new Size(600, 800), crop);
        Check(bands.Count == 2 && bands[0].Top == 70 && bands[0].Height == 30 && bands[1].Top == 300 && bands[1].Height == 30, "Compressed PDF gap incorrectly included in selection");
        var preview = (PdfPreview)window.FindName("Preview"); Invoke(window, "SelectWorkspaceTab", tabs.Owner(TabWorkspace.PreviewKey)!.Value, TabWorkspace.PreviewKey);
        var pdf = Path.Combine(output, "ai-preview.pdf"); WriteThemePdf(pdf); preview.SetCropOptions(false, false); await preview.LoadAsync(pdf); window.UpdateLayout();
        preview.BeginAiSelection();
        var layouts = ((System.Collections.IEnumerable)Field(preview, "_layouts")!).Cast<object>().ToArray();
        var top0 = (double)layouts[0].GetType().GetProperty("Top")!.GetValue(layouts[0])!; var top1 = (double)layouts[1].GetType().GetProperty("Top")!.GetValue(layouts[1])!;
        var left0 = (double)layouts[0].GetType().GetProperty("Left")!.GetValue(layouts[0])!;
        var selection = new Rect(left0 + 30, top0 + 50, 150, top1 - top0 + 30);
        typeof(PdfPreview).GetField("_aiSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(preview, selection);
        await preview.ConfirmAiSelectionAsync(); window.UpdateLayout();
        var pdfEntry = book.Entries.Last();
        Check(pdfEntry.PdfBlocks.Select(b => b.Page).Distinct().SequenceEqual(new[] { 1, 2 }) && pdfEntry.PdfRevision.Length == 64, "Cross-page PDF capture did not preserve pages/revision");
        Check(pdfEntry.PdfBlocks.All(b => b.Width > 0 && b.Height > 0 && Convert.FromBase64String(b.ImageBase64).Take(4).SequenceEqual(new byte[] { 137, 80, 78, 71 })), "PDF screenshots invalid");
        Check(tabs.FocusedTab == TabWorkspace.AiKey, "PDF selection did not open AI");
        Check(preview.IsAiSelecting && editor.IsAiMarking, "Confirming a mark must keep the toggle active");
        var point = editor.CreateAiMark(0, 0, false);
        Check(point.IsPoint && point.Offset == 0 && point.Length == 0, "Single click must create a source point");
        var sentence = editor.CreateAiMark(editor.Text.IndexOf("Hello", StringComparison.Ordinal), editor.Text.IndexOf("Hello", StringComparison.Ordinal) + 3, true);
        Check(!sentence.IsPoint && editor.Document.GetText(sentence.Offset, sentence.Length).Trim() == "Hello from TeXeed.", "Source hold/drag must select the complete sentence");
        Invoke(window, "SetAiMarking", false);
        Check(!editor.IsAiMarking && !preview.IsAiSelecting, "Marking toggle did not clear both views");
        Invoke(window, "SetAiMarking", true);
        var cursorEvent = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.QueryCursorEvent };
        editor.TextArea.TextView.RaiseEvent(cursorEvent);
        Check(cursorEvent.Cursor == Cursors.Help, "Circle mode must use help-select cursor over source text");
        var rightDouble = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(rightDouble, 2);
        editor.RaiseEvent(rightDouble);
        Check(!editor.IsAiMarking && rightDouble.Handled, "Right double-click did not leave circle mode");
        Invoke(window, "SetAiMarking", true);
        var escape = CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent);
        Invoke(window, "Window_KeyDown", window, escape);
        Check(!editor.IsAiMarking && escape.Handled, "Escape did not leave marking mode");
        var beforePoint = book.Entries.Count;
        Invoke(window, "CollectSourceMark", editor, point);
        Check(book.Entries.Count == beforePoint + 1 && book.Entries.Last().IsPoint && tabs.FocusedTab == TabWorkspace.AiKey, "Source point was not immediately collected in chat composer");
        book.Active.Title = "群的单位元";
        book.Turns.Add(new("群的单位元为什么唯一？", "若 e 和 f 都是单位元，则 e = ef = f，因此单位元唯一。"));
        Invoke(ai, "RefreshTranscript");
        var mainModels = (ComboBox)ai.Settings.FindName("Models");
        mainModels.ItemsSource = new[] { new AiModel("gpt-6.1-sol", "GPT-6.1 Sol") { ReasoningEfforts = ["low", "medium", "high", "xhigh", "max"] } };
        mainModels.SelectedIndex = 0;
        await ai.Settings.SelectEffortAsync("high");
        var search = (TextBox)ai.FindName("MessageSearchBox"); search.Text = "唯一";
        Check(((TextBlock)ai.FindName("MessageMatchStatus")).Text == "1/1", "In-conversation search failed to find the answer");
        Capture(window, Path.Combine(output, "ai-tab.png"));
        var historyDialog = new AiHistoryWindow(book, ai.DeleteConversationAsync) { Owner = window };
        historyDialog.Show(); ((TextBox)historyDialog.FindName("SearchBox")).Text = "单位元"; historyDialog.UpdateLayout();
        Check(((ListBox)historyDialog.FindName("ConversationList")).Items.Count == 1 && ((Button)historyDialog.FindName("DeleteButton")).IsEnabled, "History UI cannot search or delete a selected conversation");
        Capture(historyDialog, Path.Combine(output, "ai-history.png")); historyDialog.Close();
        Invoke(window, "OpenAiSettings"); window.UpdateLayout(); Capture(window, Path.Combine(output, "ai-settings.png")); Invoke(window, "OpenAiTab");
        var context = editor.ContextMenu; context.IsOpen = true; window.UpdateLayout();
        Check(((System.Windows.Media.SolidColorBrush)context.Background).Color != ((System.Windows.Media.SolidColorBrush)context.Foreground).Color, "Context menu foreground/background are indistinguishable"); context.IsOpen = false;
        await CallAsync(window, "HandleShortcutAsync", CreateKey(window, Key.F4, Keyboard.PreviewKeyDownEvent), ModifierKeys.Control);
        Check(tabs.Owner(TabWorkspace.AiKey) is null && tabs.Owner(TabWorkspace.PreviewKey) is not null, "Ctrl+F4 AI closing affected protected preview");
        Invoke(window, "OpenTitleSubmenu", menu); Check(tabs.Owner(TabWorkspace.AiKey) is not null, "Alt+A failed to activate AI leaf menu");
        await ai.SaveAsync();
        Console.WriteLine($"AI: {count} checks passed (OAuth identity, encrypted credentials, streaming/tool calls, source/PDF context, tabs, persistence).");
    }
    private static (string Jwt, JsonElement Keys) MakeTestIdentity()
    {
        using var rsa = RSA.Create(2048); var key = rsa.ExportParameters(false); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var h = ChatGptLogin.Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"kid\":\"test\"}"));
        var p = ChatGptLogin.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = ChatGptLogin.Issuer, aud = "test-client", sub = "test-user", email = "test@example.com", iat = now, exp = now + 300, nonce = "test-nonce" }));
        var signature = ChatGptLogin.Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var keys = JsonSerializer.SerializeToElement(new { keys = new[] { new { kid = "test", kty = "RSA", n = ChatGptLogin.Base64Url(key.Modulus!), e = ChatGptLogin.Base64Url(key.Exponent!) } } });
        return (h + "." + p + "." + signature, keys);
    }
    private sealed class AiFakeHandler : HttpMessageHandler
    {
        public int Requests; public bool Broken, SawToolOutput, SawContract, SwitchAccount, ToolsInEvents, Empty;
        public string? LastModel, LastEffort;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath != "/v1/responses") throw new Exception("Unexpected network endpoint in AI test");
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            LastModel = json["model"]?.ToString(); LastEffort = json["reasoning"]?["effort"]?.ToString();
            SawContract = json["store"]!.GetValue<bool>() == false && json["stream"]!.GetValue<bool>() && json["tools"]![0]!["type"]!.GetValue<string>() == "namespace";
            if (Requests > 1) SawToolOutput = json["input"]!.AsArray().Any(n => n?["type"]?.GetValue<string>() == "function_call_output" && n["output"]!.GetValue<string>().Contains("main.tex:2"));
            var data = Broken ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" : Requests == 1
                ? "data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"function_call\",\"name\":\"search_source\",\"call_id\":\"c1\",\"arguments\":\"{\\\"query\\\":\\\"compact proof\\\"}\"}]}}\n\n"
                : "data: {\"type\":\"response.output_text.delta\",\"delta\":\"定位到 main.tex:2。\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            if (ToolsInEvents && Requests == 1)
                data = "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"name\":\"search_source\",\"call_id\":\"c1\",\"arguments\":\"{\\\"query\\\":\\\"compact proof\\\"}\"}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            if (Empty) data = "data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            if (SwitchAccount && Requests == 1) { var changed = AiCredentials.Load(); changed.ApiKey = "different-test-key"; changed.Save(); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }
    }
}
