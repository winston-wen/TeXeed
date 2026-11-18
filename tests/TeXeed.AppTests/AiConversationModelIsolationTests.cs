using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private sealed class ConversationModelHandler : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<(string Model, string? Effort)> Requests = new();
        internal readonly SemaphoreSlim Releases = new(0);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Requests.Enqueue(((string?)json["model"] ?? "", (string?)json["reasoning"]?["effort"]));
            await Releases.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"done\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private static async Task CheckConversationModelIsolationAsync(MainWindow owner, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static AiModel Model(string id) => new(id, id) { ReasoningEfforts = ["low", "high"], DefaultReasoningEffort = "low", ContextWindow = 4096 };
        static async Task ChooseAsync(AiPanel panel, string modelId, string effort)
        {
            var modelBox = (ComboBox)panel.FindName("ModelPicker");
            modelBox.SelectedItem = modelBox.Items.OfType<AiModel>().Single(model => model.Id == modelId);
            await WaitUntilAsync(() => !(bool)Field(panel, "_modelPreferenceSaving")! && panel.Conversation.SelectedModelId == modelId, "Conversation model selection did not save");
            var effortBox = (ComboBox)panel.FindName("EffortPicker");
            effortBox.SelectedItem = effortBox.Items.OfType<AiEffort>().Single(item => item.Value == effort);
            await WaitUntilAsync(() => !(bool)Field(panel, "_modelPreferenceSaving")! && panel.Conversation.SelectedReasoningEffort == effort, "Conversation effort selection did not save");
        }
        static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch { Paragraph p => [p], Section s => Paragraphs(s.Blocks), _ => [] });

        new AiCredentials { ApiKey = "fixture-never-real", UseApiKey = true }.Save();
        var models = new[] { Model("fixture-alpha"), Model("fixture-beta"), Model("fixture-gamma") };
        using var aHandler = new ConversationModelHandler();
        using var bHandler = new ConversationModelHandler();
        using var cHandler = new ConversationModelHandler();
        using var sharedConnection = new AiConnection(aHandler);
        using var settings = new AiSettingsPanel(sharedConnection);
        var defaultPicker = (ComboBox)settings.FindName("Models");
        defaultPicker.ItemsSource = models;
        await settings.SelectModelAsync(models[0]);
        await settings.SelectEffortAsync("low");
        var project = Path.Combine(output, "conversation-model-isolation.tex");
        var book = new AiNotebook(); var a = book.Active; a.Title = "会话 A";
        var b = book.NewConversation(); b.Title = "会话 B";
        var legacy = new AiConversation { Title = "旧会话", Draft = "旧草稿", Turns = [new AiTurn("旧问题", "旧回答")] };
        book.Conversations.Add(legacy); book.ActiveConversationId = a.Id;
        var store = new AiWorkspaceStore(project, book);
        using var paneA = new AiPanel(new AiConnection(aHandler), settings);
        using var paneB = new AiPanel(new AiConnection(bHandler), settings);
        using var paneLegacy = new AiPanel(new AiConnection(cHandler), settings);
        paneA.InitializeBranchUi(); paneB.InitializeBranchUi();
        paneA.Bind(store, a.Id); paneB.Bind(store, b.Id); paneLegacy.Bind(store, legacy.Id);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(paneA); Grid.SetColumn(paneB, 1); grid.Children.Add(paneB);
        var host = new Window { Owner = owner, Left = -3000, Top = -3000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Width = 1500, Height = 800, Content = grid };
        host.Show(); host.UpdateLayout();
        try
        {
            Check(legacy.SelectedModelId == "fixture-alpha" && legacy.SelectedReasoningEffort == "low" && legacy.Draft == "旧草稿" && legacy.Turns.Single().Answer == "旧回答", "Legacy conversation did not use the existing default without losing content");
            await ChooseAsync(paneA, "fixture-alpha", "high");
            await ChooseAsync(paneB, "fixture-beta", "low");
            Check(a.SelectedModelId == "fixture-alpha" && a.SelectedReasoningEffort == "high" && b.SelectedModelId == "fixture-beta" && b.SelectedReasoningEffort == "low", "Conversation choices crossed between tabs");
            Check(settings.SelectedModel?.Id == "fixture-alpha" && settings.SelectedEffort == "low", "Conversation selection changed the default settings");

            var seed = new AiTurn("原问题", "这里是追问来源。") { SentAt = DateTimeOffset.Now, AnsweredAt = DateTimeOffset.Now };
            a.Turns.Add(seed); Invoke(paneA, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var views = (Dictionary<string, AiTurnView>)Field(paneA, "_turnViews")!;
            var answer = views[seed.Id].Answers.Single(); await answer.RenderTask;
            var paragraph = Paragraphs(answer.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("追问来源"));
            answer.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
            await paneA.BranchFromSelectionAsync(answer);
            var branch = book.Conversations.Single(c => c.ParentConversationId == a.Id);
            Check(branch.SelectedModelId == "fixture-alpha" && branch.SelectedReasoningEffort == "high", "Follow-up did not inherit the parent's current choice");
            using var paneBranch = new AiPanel(new AiConnection(cHandler), settings);
            paneBranch.Bind(store, branch.Id);
            await ChooseAsync(paneBranch, "fixture-gamma", "low");
            Check(a.SelectedModelId == "fixture-alpha" && a.SelectedReasoningEffort == "high" && b.SelectedModelId == "fixture-beta", "Changing a branch changed another conversation");

            await settings.SelectModelAsync(models[2]);
            Check(a.SelectedModelId == "fixture-alpha" && b.SelectedModelId == "fixture-beta" && legacy.SelectedModelId == "fixture-alpha", "Changing defaults rewrote existing conversations");
            var fresh = book.NewConversation(); fresh.Title = "新会话";
            using var paneFresh = new AiPanel(new AiConnection(cHandler), settings); paneFresh.Bind(store, fresh.Id);
            Check(fresh.SelectedModelId == "fixture-gamma" && fresh.SelectedReasoningEffort == "low", "New conversation did not use the current default");

            ((TextBox)paneA.FindName("Composer")).Text = "A initial";
            ((TextBox)paneB.FindName("Composer")).Text = "B initial";
            var sendA = paneA.SubmitAsync(); var sendB = paneB.SubmitAsync();
            await WaitUntilAsync(() => aHandler.Requests.Count == 1 && bHandler.Requests.Count == 1, "Both concurrent requests were not issued");
            Check(aHandler.Requests.ElementAt(0) == ("fixture-alpha", "high") && bHandler.Requests.ElementAt(0) == ("fixture-beta", "low"), "Actual concurrent request bodies crossed model or effort");
            ((TextBox)paneA.FindName("Composer")).Text = "A draft preserved";
            ((TextBox)paneB.FindName("Composer")).Text = "B queued";
            await CallAsync(paneB, "AddFollowUpAsync", "queue");
            ((TextBox)paneB.FindName("Composer")).Text = "B draft preserved";
            bHandler.Releases.Release();
            await sendB.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => bHandler.Requests.Count == 2, "Queued B request did not dispatch");
            Check(bHandler.Requests.ElementAt(1) == ("fixture-beta", "low") && !sendA.IsCompleted, "Queued request used another session's selection or stopped another session");
            ((Button)paneA.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await sendA.WaitAsync(TimeSpan.FromSeconds(10));
            var failed = a.Turns.Last(); Check(failed.IsIncomplete, "Fixture did not produce an incomplete turn");
            await ChooseAsync(paneA, "fixture-gamma", "high");
            var retryA = paneA.SubmitAsync(failed);
            await WaitUntilAsync(() => aHandler.Requests.Count == 2, "Retry was not issued");
            Check(aHandler.Requests.ElementAt(1) == ("fixture-gamma", "high") && bHandler.Requests.ElementAt(1) == ("fixture-beta", "low"), "Retry ignored its current session selection or affected another request");
            aHandler.Releases.Release(); bHandler.Releases.Release();
            await retryA.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => !paneB.HasActiveOperation && !(bool)Field(paneB, "_queueDispatching")!, "Queued B request did not complete");
            Check(((TextBox)paneA.FindName("Composer")).Text == "A draft preserved" && ((TextBox)paneB.FindName("Composer")).Text == "B draft preserved", "Sending, queuing or retry consumed a draft");

            defaultPicker.ItemsSource = new[] { models[0], models[2] };
            await settings.SelectModelAsync(models[2]);
            var bPicker = (ComboBox)paneB.FindName("ModelPicker");
            Check((bPicker.SelectedItem as AiModel)?.Id == "fixture-beta" && b.SelectedModelId == "fixture-beta" && bPicker.ToolTip?.ToString()?.Contains("不可用") == true, "A missing model was silently replaced");
            var countBefore = bHandler.Requests.Count;
            await paneB.SubmitAsync();
            Check(bHandler.Requests.Count == countBefore && ((TextBox)paneB.FindName("Composer")).Text == "B draft preserved" && ((TextBlock)paneB.FindName("StatusText")).Text.Contains("不可用"), "Unavailable model sent a different model or lost the draft");
            var unavailableQueue = AiFollowUpQueue.Load(b);
            var deferred = unavailableQueue.Enqueue("queue", "preserve pending when model disappears", []);
            unavailableQueue.Store(b); await store.SaveAsync();
            await CallAsync(paneB, "DispatchNextQueuedAsync");
            unavailableQueue = AiFollowUpQueue.Load(b);
            Check(bHandler.Requests.Count == countBefore && unavailableQueue.Items.Single().Id == deferred.Id && unavailableQueue.Items.Single().State == "pending",
                "An unavailable model consumed a pending queued message or dispatched a different model");

            await paneA.StopAndSaveAsync(); await paneB.StopAndSaveAsync(); await paneLegacy.StopAndSaveAsync(); await paneBranch.StopAndSaveAsync(); await paneFresh.StopAndSaveAsync();
            var reopened = AiStorage.Load(project);
            var persistedA = reopened.Conversations.Single(c => c.Id == a.Id); var persistedB = reopened.Conversations.Single(c => c.Id == b.Id);
            Check(persistedA.SelectedModelId == "fixture-gamma" && persistedA.SelectedReasoningEffort == "high" && persistedA.Draft == "A draft preserved", "Restart lost A's choices or draft");
            Check(persistedB.SelectedModelId == "fixture-beta" && persistedB.SelectedReasoningEffort == "low" && persistedB.Draft == "B draft preserved", "Restart lost B's unavailable selection or draft");
            Check(AiFollowUpQueue.Load(persistedB).Items.Single().Id == deferred.Id, "Restart lost the queue waiting for an unavailable model");
            Check(reopened.Conversations.Single(c => c.Id == branch.Id).SelectedModelId == "fixture-gamma" && reopened.Conversations.Single(c => c.Id == legacy.Id).Draft == "旧草稿", "Restart lost branch choice or old conversation draft");
            var restoredStore = new AiWorkspaceStore(project, reopened);
            using var restoredA = new AiPanel(new AiConnection(aHandler), settings);
            using var restoredB = new AiPanel(new AiConnection(bHandler), settings);
            restoredA.Bind(restoredStore, a.Id); restoredB.Bind(restoredStore, b.Id);
            grid.Children.Remove(paneA); grid.Children.Remove(paneB);
            grid.Children.Add(restoredA); Grid.SetColumn(restoredB, 1); grid.Children.Add(restoredB); host.UpdateLayout();
            Check((((ComboBox)restoredA.FindName("ModelPicker")).SelectedItem as AiModel)?.Id == "fixture-gamma"
                && (((ComboBox)restoredA.FindName("EffortPicker")).SelectedItem as AiEffort)?.Value == "high"
                && ((TextBox)restoredA.FindName("Composer")).Text == "A draft preserved", "Reopened A controls did not reflect the persisted selection and draft");
            Check((((ComboBox)restoredB.FindName("ModelPicker")).SelectedItem as AiModel)?.Id == "fixture-beta"
                && (((ComboBox)restoredB.FindName("EffortPicker")).SelectedItem as AiEffort)?.Value == "low"
                && ((TextBox)restoredB.FindName("Composer")).Text == "B draft preserved", "Reopened B controls lost its unavailable selection or draft");
            ((TextBox)restoredA.FindName("Composer")).Text = "A after restart";
            var afterRestart = restoredA.SubmitAsync();
            await WaitUntilAsync(() => aHandler.Requests.Count == 3, "Reopened conversation did not issue a request");
            Check(aHandler.Requests.ElementAt(2) == ("fixture-gamma", "high") && bHandler.Requests.Count == countBefore,
                "Reopened conversation request did not use its persisted selection");
            aHandler.Releases.Release(); await afterRestart.WaitAsync(TimeSpan.FromSeconds(10));
            Check(((TextBox)restoredB.FindName("Composer")).Text == "B draft preserved", "A's request after restart changed B's draft");
            await restoredA.StopAndSaveAsync(); await restoredB.StopAndSaveAsync();
            await CheckModelSaveFailureAndSwitchAsync(settings, cHandler, output);
            Console.WriteLine("PASS: independent conversation models/efforts, concurrent actual requests, queue, retry, branch inheritance, defaults, unavailable model, legacy data and restart.");
        }
        finally { host.Close(); }
    }

    private static async Task CheckModelSaveFailureAndSwitchAsync(AiSettingsPanel settings, ConversationModelHandler handler, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var failureProject = Path.Combine(output, "model-save-failure-" + Guid.NewGuid().ToString("N") + ".tex");
        var failureBook = new AiNotebook(); failureBook.Active.Title = "保存失败会话";
        var failureStore = new AiWorkspaceStore(failureProject, failureBook);
        using (var failurePanel = new AiPanel(new AiConnection(handler), settings))
        {
            failurePanel.Bind(failureStore, failureBook.Active.Id);
            ((TextBox)failurePanel.FindName("Composer")).Text = "保存失败草稿";
            var previous = (failureBook.Active.SelectedModelId, failureBook.Active.SelectedReasoningEffort, failureBook.Active.ModelPreferenceExplicit);
            var destination = AiStorage.NotebookPath(failureProject);
            Directory.CreateDirectory(destination);
            try
            {
                var picker = (ComboBox)failurePanel.FindName("ModelPicker");
                picker.SelectedItem = picker.Items.OfType<AiModel>().Single(model => model.Id == "fixture-alpha");
                await WaitUntilAsync(() => !(bool)Field(failurePanel, "_modelPreferenceSaving")! && ((TextBlock)failurePanel.FindName("StatusText")).Text.Length > 0, "Failed model save did not finish or report an error");
                Check((failureBook.Active.SelectedModelId, failureBook.Active.SelectedReasoningEffort, failureBook.Active.ModelPreferenceExplicit) == previous,
                    "Failed model save did not restore the prior selection");
                Check((picker.SelectedItem as AiModel)?.Id == previous.SelectedModelId && ((TextBox)failurePanel.FindName("Composer")).Text == "保存失败草稿",
                    "Failed model save left a misleading selection or lost the draft");
            }
            finally { Directory.Delete(destination); }
            await failurePanel.StopAndSaveAsync();
            Check(AiStorage.Load(failureProject).Active.Draft == "保存失败草稿", "Draft was not saved after the storage failure cleared");
        }

        var raceId = Guid.NewGuid().ToString("N");
        var raceProject = Path.Combine(output, "model-save-race-" + raceId + ".tex");
        var otherProject = Path.Combine(output, "model-save-race-other-" + raceId + ".tex");
        using var racePanel = new AiPanel(new AiConnection(handler), settings);
        await racePanel.SetProjectAsync(raceProject);
        ((TextBox)racePanel.FindName("Composer")).Text = "切换期间草稿";
        var gate = (SemaphoreSlim)Field(racePanel, "_saveGate")!;
        await gate.WaitAsync();
        var held = true;
        try
        {
            var picker = (ComboBox)racePanel.FindName("ModelPicker");
            picker.SelectedItem = picker.Items.OfType<AiModel>().Single(model => model.Id == "fixture-alpha");
            Check((bool)Field(racePanel, "_modelPreferenceSaving")!, "Fixture did not block the model-selection save");
            var requests = handler.Requests.Count;
            await racePanel.SubmitAsync();
            Check(handler.Requests.Count == requests && ((TextBox)racePanel.FindName("Composer")).Text == "切换期间草稿",
                "A request started or consumed the draft while its model selection was still saving");
            var switching = racePanel.SetProjectAsync(otherProject);
            await Task.Delay(100);
            Check(!switching.IsCompleted, "Project switching did not wait for the model selection save");
            gate.Release(); held = false;
            await switching.WaitAsync(TimeSpan.FromSeconds(10));
            var restored = AiStorage.Load(raceProject).Active;
            Check(restored.SelectedModelId == "fixture-alpha" && restored.Draft == "切换期间草稿", "Switching projects lost the saved selection or draft");
        }
        finally { if (held) gate.Release(); }
        await racePanel.StopAndSaveAsync();
        Console.WriteLine("PASS: model-selection storage failure rolls back and project switching waits for an in-flight save.");
    }
}
