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
    private static async Task CheckAiFormCollisionsAsync(MainWindow owner, string output)
    {
        const string markdown = "单行 {{text:same}} 同步 {{text:same}}\n\n{{textarea:same}}\n\n{{radio:same:a:甲}} {{radio:same:b:乙}} {{radio:same:a:甲重复}}\n\n{{checkbox:same:多选甲}} {{checkbox:same:多选重复}}";
        var old = AiForms.WithSubmission(new AiTurn("旧问题", markdown), null);
        old.AdditionalData![AiForms.DraftKey] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["same"] = "无法确定的旧值" });
        var oldSnapshot = AiForms.Snapshot(old) ?? throw new Exception("Legacy collision snapshot missing");
        if (oldSnapshot.AmbiguousLegacy?.GetValueOrDefault("same") != "无法确定的旧值" ||
            AiForms.Value(oldSnapshot.Values, "text", "same", false) != "" || AiForms.Value(oldSnapshot.Values, "textarea", "same", false) != "" ||
            !AiForms.Describe(oldSnapshot).Contains("无法确定归属")) throw new Exception("An ambiguous legacy value was lost or wrongly assigned");
        var unique = AiForms.WithSubmission(new AiTurn("旧", "{{text:solo}}"), null);
        unique.AdditionalData![AiForms.DraftKey] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["solo"] = "旧值" });
        if (AiForms.Snapshot(unique)?.Values.GetValueOrDefault("text:solo") != "旧值") throw new Exception("Unambiguous old draft stopped working");
        var oldTurn = AiForms.WithSubmission(new AiTurn("旧提交", "答复"), new AiForms.Submission("old", new() { ["solo"] = "旧答案" }));
        if (AiForms.Value(AiForms.FromTurn(oldTurn)!.Values, "text", "solo") != "旧答案") throw new Exception("Legacy submitted answers stopped working");

        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new FormHandler(); using var panel = new AiPanel(new AiConnection(handler));
        var project = Path.Combine(output, "form-collision.tex"); await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        var source = AiForms.WithSubmission(new AiTurn("练习", markdown), null); book.Turns.Add(source);
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 900, Height = 700, Content = panel }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var view = views[source.Id].Answers.Single(); await view.RenderTask; host.UpdateLayout();
            var fields = Descendants<TextBox>(view).Where(t => System.Windows.Automation.AutomationProperties.GetName(t) == "same").ToArray();
            var singles = fields.Where(t => !t.AcceptsReturn).ToArray(); var multi = fields.Single(t => t.AcceptsReturn);
            if (singles.Length != 2) throw new Exception("Collision fixture did not render duplicate text controls");
            var radios = Descendants<RadioButton>(view).ToArray(); var a = radios.Single(r => Equals(r.Content, "甲")); var a2 = radios.Single(r => Equals(r.Content, "甲重复")); var b = radios.Single(r => Equals(r.Content, "乙"));
            var checks = Descendants<CheckBox>(view).ToArray();
            singles[0].Text = "single-answer";
            if (singles[1].Text != "single-answer" || multi.Text.Length != 0) throw new Exception("Duplicate text controls did not sync independently from textarea");
            multi.Text = "multi-answer";
            a.IsChecked = true;
            if (a2.IsChecked != true || b.IsChecked == true) throw new Exception("Duplicate radio choices did not sync");
            b.IsChecked = true;
            if (a.IsChecked == true || a2.IsChecked == true || b.IsChecked != true) throw new Exception("Radio choices did not remain mutually exclusive");
            checks[0].IsChecked = true;
            if (checks[1].IsChecked != true || singles[0].Text != "single-answer" || multi.Text != "multi-answer") throw new Exception("Checkboxes did not sync independently from text");
            await panel.SaveAsync();
            var restored = AiStorage.Load(project).Active.Turns.Single(); var restoredDraft = AiForms.Draft(restored);
            if (AiForms.Value(restoredDraft, "text", "same", false) != "single-answer" || AiForms.Value(restoredDraft, "textarea", "same", false) != "multi-answer" ||
                AiForms.Value(restoredDraft, "radio", "same", false) != "b" || AiForms.Value(restoredDraft, "checkbox", "same", false) != "true")
                throw new Exception("Cross-type draft values were overwritten on reopen");
            ((TextBox)panel.FindName("Composer")).Text = "请检查";
            var send = panel.SubmitAsync(); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (singles.Any(t => !t.IsReadOnly) || !multi.IsReadOnly || radios.Any(r => r.IsEnabled) || checks.Any(c => c.IsEnabled)) throw new Exception("Cross-type controls were not frozen");
            var path = AiTurnCheckpoint.PathsFor(project).Distinct(StringComparer.OrdinalIgnoreCase).Single();
            var pending = JsonSerializer.Deserialize<AiTurnCheckpoint.Pending>(File.ReadAllText(path))!;
            var snapshot = AiForms.FromTurn(pending.Turn)!;
            if (snapshot.Values.GetValueOrDefault("text:same") != "single-answer" || snapshot.Values.GetValueOrDefault("textarea:same") != "multi-answer" ||
                snapshot.Values.GetValueOrDefault("radio:same") != "b" || snapshot.Values.GetValueOrDefault("checkbox:same") != "true") throw new Exception("Checkpoint combined different types with the same id");
            var recovered = AiStorage.Load(project).Active;
            if (AiForms.SubmittedFor(recovered, source.Id)?.Values.GetValueOrDefault("textarea:same") != "multi-answer") throw new Exception("Crash recovery lost a typed value");
            var description = AiForms.HistoryQuestion(pending.Turn);
            if (!description.Contains("text:same") || !description.Contains("textarea:same") || !description.Contains("radio:same") || !description.Contains("checkbox:same") ||
                !handler.Body.Contains("single-answer") || !handler.Body.Contains("multi-answer")) throw new Exception("Model context did not clearly identify typed form values");
            handler.Release.TrySetResult(); await send.WaitAsync(TimeSpan.FromSeconds(20)); await panel.StopAndSaveAsync();
            Console.WriteLine("PASS: cross-type id isolation, duplicate synchronization, legacy ambiguity, save, request, freeze and recovery.");
        }
        finally { handler.Release.TrySetResult(); host.Close(); }
    }
}
