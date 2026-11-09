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
        Check(menu.Items.OfType<MenuItem>().Select(i=>i.Header.ToString()).SequenceEqual(new[]{"AI 对话","AI 设置","AI 历史"}), "AI submenu layout differs"); Invoke(window,"OpenAiTab"); window.UpdateLayout();
        var aiKey = TabWorkspace.AiConversationKey(ai.ConversationId);
        var sourceKey = TabWorkspace.AiSourceKey(ai.ConversationId);
        var pdfKey = TabWorkspace.AiPreviewKey(ai.ConversationId);
        Check(tabs.Owner(aiKey) == TabSide.Right && tabs.Owner(sourceKey) == TabSide.Left && tabs.Owner(pdfKey) == TabSide.Right && ai.IsVisible, "AI button did not open the session comments, source and PDF views");
        Check(!tabs.Sources.Contains(aiKey) && !tabs.Sources.Contains(sourceKey) && !tabs.Sources.Contains(pdfKey), "AI session views treated as project source files");
        var composer = (TextBox)ai.FindName("Composer");
        composer.Focus(); window.UpdateLayout();
        Check(composer.VerticalContentAlignment == VerticalAlignment.Top && composer.GetRectFromCharacterIndex(0).Top < 18, "Composer caret must begin near the top padding");
        using (var catalog = JsonDocument.Parse("{\"slug\":\"gpt-6.1-sol\",\"display_name\":\"Sol\",\"supported_reasoning_levels\":[{\"effort\":\"low\"},{\"effort\":\"high\"},{\"effort\":\"ultra\"}]}"))
        {
            var parsed = AiModel.FromCatalog(catalog.RootElement);
            Check(parsed.ReasoningEfforts.SequenceEqual(new[] { "low", "high" }), "Model efforts must follow the catalog and public Responses contract");
        }
        var strip = (EditorTabStrip)window.FindName("RightFileTabs");
        var aiTab = ((StackPanel)strip.FindName("Items")).Children.Cast<Border>().Single(b => (string)b.Tag == aiKey);
        var aiButtons = ((Grid)aiTab.Child).Children.Cast<Button>().ToArray();
        Check(aiButtons.Length == 2 && aiButtons[1].ToolTip?.ToString()?.Contains("关闭") == true, "AI tab should contain only title and close; settings belong in the chat header");
        Invoke(window,"OpenAiSettings"); window.UpdateLayout();
        Check(tabs.FocusedTab == TabWorkspace.AiSettingsKey && ai.Settings.IsVisible && !ai.IsVisible, "AI gear did not open separate settings tab");
        Check(!tabs.Sources.Contains(TabWorkspace.AiSettingsKey), "AI settings treated as source");
        Invoke(window, "OpenAiTab");
        Invoke(window, "MoveWorkspaceTab", aiKey, TabSide.Left, 0, false); window.UpdateLayout();
        Check(tabs.Owner(aiKey) == TabSide.Left && ai.IsVisible, "AI tab did not move left");

        var project = (TexProject)Field(window, "_project")!;
        var restored = AiStorage.Load(project.MainPath);
        var book = (AiNotebook)Field(ai, "_book")!;
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
        using (var cancelBroken = new CancellationTokenSource(200))
            try { await new AiResponses(brokenConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, cancelBroken.Token); } catch (OperationCanceledException) { failed = true; }
        Check(failed, "Interrupted stream treated as success");
        await CheckAiStreamRecoveryAsync(Check);
        var batchHandler = new AiFakeHandler();
        using (var batchPanel = new AiPanel(new AiConnection(batchHandler)))
        {
            var batchProject = Path.Combine(output, "batch-fixture-" + Guid.NewGuid().ToString("N") + ".tex");
            await batchPanel.SetProjectAsync(batchProject); batchPanel.ProjectFiles = () => files;
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
            var batchBook = (AiNotebook)Field(batchPanel, "_book")!;
            var firstId = batchBook.Active.Id;
            batchBook.Turns.Add(new("第一问", "回答"));
            var firstTurnId = batchBook.Turns[0].Id;
            ((TextBox)batchPanel.FindName("Composer")).Text = "草稿 A";
            await batchPanel.NewConversationAsync();
            var secondId = batchBook.Active.Id;
            Check(secondId != firstId && batchBook.Turns.Count == 0 && batchBook.Entries.Count == 0, "New conversation inherited messages/context");
            ((TextBox)batchPanel.FindName("Composer")).Text = "草稿 B";
            await batchPanel.SelectConversationAsync(firstId);
            Check(((TextBox)batchPanel.FindName("Composer")).Text == "草稿 A", "Switching conversation lost its draft/context");
            Check(AiHistoryPage.Search(batchBook, "第一问").Single().Id == firstId, "History search did not find question text");
            batchBook.Turns.Add(new("问题 C", "可搜索的回答关键词"));
            Check(AiHistoryPage.Search(batchBook, "回答关键词").Single().Id == firstId, "History search did not find answer text");
            await batchPanel.DeleteTurnAsync(firstTurnId);
            Check(!batchBook.Turns.Any(t => t.Id == firstTurnId) && batchBook.Turns.Count == 1, "Deleting a message removed the wrong turns");
            await batchPanel.DeleteConversationAsync(firstId);
            Check(batchBook.Active.Id == secondId && batchBook.Conversations.Count == 1 && ((TextBox)batchPanel.FindName("Composer")).Text == "草稿 B", "Deleting active conversation did not restore another conversation");
            var savedHistory = AiStorage.Load(batchProject);
            Check(savedHistory.Conversations.Count == 1 && savedHistory.Active.Id == secondId && savedHistory.Draft == "草稿 B", "Conversation deletion did not persist");
            await batchPanel.DeleteConversationAsync(secondId);
            Check(batchBook.Conversations.Count == 1 && batchBook.Turns.Count == 0 && batchBook.Active.Id != secondId, "Deleting last conversation must yield a fresh empty chat");
        }
        var legacyProject = Path.Combine(output, "legacy-ai.tex");
        var legacyPath = AiStorage.NotebookPath(legacyProject); Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { Entries = restored.Entries, Turns = new[] { new { Question = "[a6eb2178] 原来的问题", Answer = "原来的回答" } }, Draft = "原来的草稿" }));
        var migrated = AiStorage.Load(legacyProject);
        Check(migrated.Conversations.Count == 1 && migrated.Turns.Single().Answer == "原来的回答" && migrated.Draft == "原来的草稿", "Legacy conversation migration lost data");
        Check(migrated.Turns.Single().DisplayQuestion == "原来的问题", "Legacy internal ID prefix leaked into displayed message");
        var emptyLegacy = new AiTurn("旧的空回答问题", "");
        Check(emptyLegacy.DisplayAnswer.Contains("未保存回答"), "Legacy empty answer is not explained to the user");
        var nextInput = AiResponses.MakeInput([new AiEntry { Question = "新的问题" }], [emptyLegacy]);
        Check(nextInput.Count == 1 && !nextInput.ToJsonString().Contains("旧的空回答问题"), "Legacy empty replies polluted the next request context");
        CheckAiContextBudget(Check);
        AiStorage.Save(legacyProject, migrated.Snapshot());
        using (var migratedJson = JsonDocument.Parse(File.ReadAllText(legacyPath)))
            Check(migratedJson.RootElement.TryGetProperty("Conversations", out _) && !migratedJson.RootElement.TryGetProperty("Entries", out _), "History schema duplicates media at notebook root");
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
        book.Active.Title = "群的单位元";
        book.Turns.Add(new("群的单位元为什么唯一？", "若 e 和 f 都是单位元，则 e = ef = f，因此单位元唯一。"));
        Invoke(ai, "RefreshTranscript");
        var mainModels = (ComboBox)ai.Settings.FindName("Models");
        mainModels.ItemsSource = new[] { new AiModel("gpt-6.1-sol", "GPT-6.1 Sol") { ReasoningEfforts = ["low", "medium", "high", "xhigh", "max"] } };
        mainModels.SelectedIndex = 0;
        await ai.Settings.SelectEffortAsync("high");
        var shared = book.Active.SharedDocument;
        const string sharedSource = "\\documentclass{article}\n\\begin{document}\n单位元唯一。\n\\end{document}";
        Check(shared.Source.Length == 0 ? shared.TryInitialize(sharedSource, "用户", "user") : shared.TryApply(shared.Revision, sharedSource, "用户"), "Could not prepare the shared-document search fixture");
        Invoke(window, "SelectWorkspaceTab", tabs.Owner(aiKey)!.Value, aiKey);
        ai.OpenDocumentSearch();
        ((TextBox)Field(ai, "_documentSearchText")!).Text = "唯一";
        await CallAsync(ai, "SearchDocumentAsync");
        Check(((TextBlock)Field(ai, "_documentSearchStatus")!).Text == "1 / 1", "Shared-document search failed to find its source text");
        ((Window)Field(ai, "_documentSearchWindow")!).Close();
        Capture(window, Path.Combine(output, "ai-tab.png"));
        Invoke(window,"OpenAiHistory"); window.UpdateLayout();
        var historyView = (AiHistoryView)Field(window,"_aiHistory")!;
        ((TextBox)historyView.FindName("SearchBox")).Text = "单位元"; window.UpdateLayout();
        Check(((ItemsControl)historyView.FindName("ConversationList")).Items.Count == 1,"History tab search failed");
        Capture(window,Path.Combine(output,"ai-history-tab.png"));
        Invoke(window, "OpenAiSettings"); window.UpdateLayout(); Capture(window, Path.Combine(output, "ai-settings.png")); Invoke(window, "OpenAiTab");
        var context = editor.ContextMenu; context.ApplyTemplate();
        Check(((System.Windows.Media.SolidColorBrush)context.Background).Color != ((System.Windows.Media.SolidColorBrush)context.Foreground).Color, "Context menu foreground/background are indistinguishable");
        Invoke(window, "SelectWorkspaceTab", tabs.Owner(aiKey)!.Value, aiKey);
        await CallAsync(window, "HandleShortcutAsync", CreateKey(window, Key.F4, Keyboard.PreviewKeyDownEvent), ModifierKeys.Control);
        Check(tabs.Owner(aiKey) is null && tabs.Owner(TabWorkspace.PreviewKey) is not null, "Ctrl+F4 AI closing affected protected preview");
        Invoke(window, "OpenTitleSubmenu", menu); Check(menu.IsSubmenuOpen || menu.HasItems, "Alt+A failed to expose AI commands");
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
        public int Requests, ResponseRequests; public bool Broken, ProgramFailure, SawToolOutput, SawContract, SwitchAccount, ToolsInEvents, Empty;
        public string? LastModel, LastEffort;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath != "/v1/responses") throw new Exception("Unexpected network endpoint in AI test");
            ResponseRequests++;
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
            if (ProgramFailure) data = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\ndata: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"fixture program failure\"}}}\n\n";
            if (SwitchAccount && Requests == 1) { var changed = AiCredentials.Load(); changed.ApiKey = "different-test-key"; changed.Save(); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }
    }
}
