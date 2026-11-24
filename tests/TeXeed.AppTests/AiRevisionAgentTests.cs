using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckRevisionAgentAsync(MainWindow window, string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var folder = Path.Combine(output, "agent-project");
        Directory.CreateDirectory(folder);
        var project = Path.Combine(folder, "revision-agent.tex");
        File.WriteAllText(project, "\\documentclass{article}\n\\begin{document}Revision fixture.\\end{document}");
        var seed = new AiNotebook();
        seed.Active.Permission = AiPermission.ReadOnly;
        var target = new AiTurn("原题", "先写一句。再用 $x$ 作说明。\n\n多余段。");
        var other = new AiTurn("另一个问题", "另一轮的原文。");
        seed.Active.Turns.AddRange([target, other]);
        var anchor = new AiAnswerAnchor(target.Id, target.Id + "-answer", 0, 4, "先写一句", AiStorage.Hash(target.Answer));
        var branch = new AiConversation { ParentConversationId = seed.Active.Id, BranchAnchor = anchor, Title = "分叉提问" };
        seed.Conversations.Add(branch);
        AiStorage.Save(project, seed);
        await CallAsync(window, "OpenAsync", project);
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        var handler = new RevisionFixtureServer();
        using var panel = new AiPanel(new AiConnection(handler));
        panel.CreateToolRunner = () => window.CreateAiToolRunner(panel.Permission, panel.ShellApprovals);
        async Task Reload()
        {
            var store = await AiWorkspaceStore.LoadAsync(project);
            panel.Bind(store, seed.Active.Id);
            var models = (ComboBox)panel.Settings.FindName("Models");
            models.ItemsSource = new[] { new AiModel("fixture", "改稿测试模型") };
            models.SelectedIndex = 0;
        }
        await Reload();
        async Task Send(string text, bool authorized, params Func<string[], JsonObject>[] responses)
        {
            handler.Steps = new(responses); handler.Authorized = authorized; handler.Failure = null;
            ((TextBox)panel.FindName("Composer")).Text = text;
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            if (handler.Failure is { } error) throw new InvalidOperationException("Revision HTTP fixture failed", error);
            Check(handler.Steps.Count == 0, "Expected requests were missing after: " + text);
            Check(!AiStorage.Load(project).Active.Turns.Last().IsIncomplete, "Test turn failed: " + text);
        }
        var read = new Func<string[], JsonObject>(_ => handler.Call("read_answer_revision", new { start_row = 0, max_rows = 20 }));
        var wait = new Func<string[], JsonObject>(_ => handler.Call("await_revision_reply", new { }));
        var ignored = "普通讨论。";
        await Send("讨论一下是否应该改写第1轮，不要动原文。", false,
            _ => handler.Call("apply_answer_revision", new { }), results =>
            {
                Check(results.Last().Contains("未授权"), "A fabricated revision tool call escaped the ordinary chat gate");
                return handler.Say(ignored);
            });
        Check(AiStorage.Load(project).Turns[0].RevisionHistory is null, "An ordinary discussion changed a turn");
        foreach (var text in new[] { "不要整理第1轮", "「请改写第1轮」是什么意思？", "请解释如何改写第1轮", "请改写第1轮是什么意思？", "改写第1轮会影响分叉吗？", "请改写第1轮和第2轮" })
            Check(AiAnswerRevision.Resolve(text, seed.Turns, []) is null, "Granted an ambiguous/quoted request: " + text);
        Check(AiAnswerRevision.Resolve("把上一轮回答改成三段", seed.Turns, [])?.TargetTurnId == other.Id, "Previous-turn command selected wrong target");
        Check(AiAnswerRevision.Resolve("请改写第二轮的回答", seed.Turns, [])?.TargetTurnId == other.Id, "Chinese ordinal selected wrong target");

        await Send("请改写第 1 轮：请先问我该保留哪一部分。", true, read, results =>
        {
            var draft = JsonNode.Parse(results.Last())!;
            Check(draft["target"]!.ToString() == target.Id, "Read touched the wrong turn");
            var objects = draft["rows"]![0]!["parts"]!.AsArray().Where(p => p!["kind"]!.ToString() == "keep").ToArray();
            Check(objects.Length == 2 && objects[0]!["references"]![0]!.ToString() == branch.Title && objects[1]!["source"]!.ToString() == "$x$", "Formula or in-paragraph branch anchor absent from tool result");
            return wait(results);
        }, _ => handler.Say("要保留哪部分？ {{text:keep}}"));
        await Reload();
        var first = AiStorage.Load(project).Turns.Last();
        Check(first.RevisionRequest is { AwaitingReply: true } && first.RevisionRequest.TargetTurnId == target.Id, "Clarification target did not survive reopening");
        AiForms.SetDraft(((AiNotebook)Field(panel, "_book")!).Turns.Last(), "text", "keep", "已经填过这个框");
        await Send("今天只讨论别的题。", false, _ => handler.Say(ignored));
        // An unrelated discussion should not grant writes even if there was a partially filled form.
        await Send("继续改稿：先确认该保留的公式。", true, wait, _ => handler.Say("保留哪个公式？ {{text:formula}}"));
        await Reload();
        var book = (AiNotebook)Field(panel, "_book")!;
        var last = book.Turns.Last();
        AiForms.SetDraft(last, "text", "formula", "保留 x，保留引用，删掉第二段。");
        string? application = null;
        JsonObject Apply(string targetId, string version, string rowId, object[] parts, string? deleted = null)
        {
            var changes = new List<object> { new { row = rowId, position = "replace", paragraphs = new[] { new { level = 0, parts } } } };
            if (deleted is not null) changes.Add(new { row = deleted, position = "replace", paragraphs = Array.Empty<object>() });
            return handler.Call("apply_answer_revision", new { target = targetId, version, changes });
        }
        string version = "", row = "", unwanted = "";
        await Send("", true, read, results =>
        {
            var draft = JsonNode.Parse(results.Last())!;
            Check(draft["target"]!.ToString() == target.Id, "Form reply was rebound to the latest question");
            Check(draft["task_history"]![0]!["instruction"]!.ToString().Contains("先问我") &&
                draft["task_history"]![1]!["clarification"]!.ToString().Contains("保留哪个公式"), "The task lost the original request or its follow-up questions");
            version = draft["version"]!.ToString(); row = draft["rows"]![0]!["id"]!.ToString(); unwanted = draft["rows"]![1]!["id"]!.ToString();
            return Apply(other.Id, version, row, []);
        }, results =>
        {
            Check(results.Last().Contains("目标与用户选定"), "Cross-target tool submission accepted");
            return Apply(target.Id, version, row, [new { kind = "text", value = "丢掉对象" }]);
        }, results =>
        {
            Check(results.Last().Contains("缺少原段落"), "A proposal could drop immutable content");
            var good = Apply(target.Id, version, row, [new { kind = "text", value = "新说明" }, new { kind = "keep", value = "0" },
                new { kind = "text", value = "保留 " }, new { kind = "keep", value = "1" }, new { kind = "text", value = "。" }], unwanted);
            application = good["arguments"]!.ToString(); return good;
        }, results =>
        {
            Check(results.Last().Contains("已保存"), "Target update failed: " + results.Last());
            return handler.Call("apply_answer_revision", JsonNode.Parse(application!));
        }, results =>
        {
            Check(results.Last().Contains("已保存"), "Repeated equivalent application was not idempotent");
            return handler.Say("已改好。");
        });
        var saved = AiStorage.Load(project);
        var answer = saved.Turns[0]; var history = answer.RevisionHistory!;
        Check(history.Count == 2 && history.Last().Author == "ai" && history.Last().OperationId is not null, "Duplicate or unattributed revision history");
        Check(history.First().Sources.Values.Single() == target.Answer && answer.Answer == "新说明保留 $x$。", "Latest and original content not reliably persisted");
        Check(history.Last().Rows.Single().Links.Single() is { Offset: 3 } link && link.Id == branch.Id, "Relocated branch link was lost or duplicated");
        Check(saved.Turns[1].Answer == other.Answer && saved.Turns.Skip(1).All(t => t.RevisionHistory is null), "History was saved on a question or clarification turn");
        Check(saved.Turns.Last().RevisionRequest is { AwaitingReply: false } finished && finished.TaskId == first.RevisionRequest!.TaskId, "Task identity changed or permission stayed open");
        await Send("继续改稿：测试任务已经结束。", false, _ => handler.Say(ignored));

        await Send("把第 2 轮回答改成一句话", true, read, results =>
        {
            var draft = JsonNode.Parse(results.Last())!;
            var saveUserChange = panel.Dispatcher.Invoke(() =>
            {
                var original = book.Turns.Single(t => t.Id == other.Id);
                var current = panel.BeginRevision(original);
                return panel.CommitRevisionAsync(other.Id, current.Version, [current.Rows.Single() with { Text = "用户在等待期间保存了另一种表述。" }], "user");
            });
            saveUserChange.GetAwaiter().GetResult();
            return Apply(other.Id, draft["version"]!.ToString(), draft["rows"]![0]!["id"]!.ToString(), [new { kind = "text", value = "过时的版本。" }]);
        }, results =>
        {
            Check(results.Last().Contains("版本已变化"), "A stale AI tool write replaced a concurrent user edit");
            return handler.Say("用户已经更新此轮。");
        });
        var concurrent = AiStorage.Load(project).Turns[1];
        Check(concurrent.Answer == "用户在等待期间保存了另一种表述。" && concurrent.RevisionHistory!.Last().Author == "user", "Concurrent edit wasn't reliably saved");
        var actual = book.Turns.Single(t => t.Id == other.Id);
        actual.UserRevisionDraft = panel.BeginRevision(actual);
        await Send("请整理第2轮。", true, read, results =>
        {
            var draft = JsonNode.Parse(results.Last())!;
            return Apply(other.Id, draft["version"]!.ToString(), draft["rows"]![0]!["id"]!.ToString(), [new { kind = "text", value = "与手动草稿冲突。" }]);
        }, results =>
        {
            Check(results.Last().Contains("未完成的手动整理"), "Concurrent unsaved user draft was clobbered");
            return handler.Say("请先处理手动草稿。");
        });
        Check(AiStorage.Load(project).Turns[1].UserRevisionDraft is not null, "Manual draft disappeared after conflict");
        await Send("请改写第1轮：请先向我确认。", true, wait, _ => handler.Say("请回复想保留的内容。"));
        await Send("取消这次改稿。", false, _ => handler.Say("已取消。"));
        await Send("继续改稿：这次已经取消。", false, _ => handler.Say(ignored));
        var locked = new AiRevisionRow("formula", "s", "$$x=1$$", 0, true, []);
        var lockedDraft = new AiRevisionDraft("turn", "version", [locked]);
        var added = AiPanel.ApplyRevisionChanges(lockedDraft, JsonSerializer.SerializeToElement(
            new[] { new { row = "formula", position = "before", paragraphs = new[] { new { level = 1, parts = new[] { new { kind = "text", value = "公式前的标题" } } } } },
                new { row = "formula", position = "after", paragraphs = new[] { new { level = 0, parts = new[] { new { kind = "text", value = "公式后的正文。" } } } } } }));
        Check(AiRevisionText.Normalize(lockedDraft, added) is [{ Level: 1 }, { Locked: true }, { Text: "公式后的正文。" }], "Could not insert on both sides of a locked standalone formula");
        Console.WriteLine("AI revision HTTP/WPF regression passed. Verified ordinary chat, binding, clarification after reopening, form-only replies, protected formula/reference, history, duplicate execution, concurrent saves and manual drafts.");
    }

    private sealed class RevisionFixtureServer : HttpMessageHandler
    {
        internal Queue<Func<string[], JsonObject>> Steps = new();
        internal bool Authorized;
        internal Exception? Failure;
        private int _callId;
        internal JsonObject Call(string name, object? args) => new() { ["type"] = "function_call", ["name"] = name,
            ["call_id"] = "test-" + ++_callId, ["arguments"] = JsonSerializer.Serialize(args) };
        internal JsonObject Say(string text) => new() { ["type"] = "message", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try
            {
                if (request.RequestUri!.AbsolutePath != "/v1/responses") throw new InvalidOperationException("Unexpected network endpoint");
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                if (payload["tools"]!.ToJsonString().Contains("replace_source") || !payload["tools"]!.ToJsonString().Contains("ask_user"))
                    throw new InvalidOperationException("The request didn't exercise the native read-only workspace permission.");
                if (payload["tools"]!.ToJsonString().Contains("read_answer_revision") != Authorized ||
                    payload["instructions"]!.ToString().Contains(AiAnswerRevision.Instructions) != Authorized)
                    throw new InvalidOperationException("Revision schema and instructions disagreed with user authorization.");
                var results = payload["input"]!.AsArray().Where(n => n?["type"]?.ToString() == "function_call_output")
                    .Select(n => n!["output"]!.ToString()).ToArray();
                var reply = Steps.Dequeue()(results);
                var body = new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(reply) } };
                return new(HttpStatusCode.OK) { Content = new StringContent("data: " + body.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
            catch (Exception ex) { Failure = ex; throw; }
        }
    }
}
