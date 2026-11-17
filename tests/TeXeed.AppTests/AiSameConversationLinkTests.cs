using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckSameConversationLinksAsync(Window owner, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        await CheckAcceptedAnnotationRecoveryAsync(output);
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Content = panel, Width = 860, Height = 700 };
        host.Show();
        try
        {
            var project = Path.Combine(output, "same-conversation-links-" + Guid.NewGuid().ToString("N") + ".tex");
            await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            var conversation = book.Active;
            var source = new AiTurn("第一问", "这里是原文甲，公式 $a+b$，重复词和重复词。\n\n| 项目 | 数值 |\n| --- | --- |\n| 表格重点 | 42 |");
            conversation.Turns.Add(source);
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[source.Id].Answers.Single(); await answer.RenderTask; host.UpdateLayout();
            var plain = answer.CopySource(answer.Document.ContentStart, answer.Document.ContentEnd);
            CheckCopySourceLengths(answer);
            foreach (var nested in Descendants<MarkdownView>(answer)) CheckCopySourceLengths(nested);
            var segment = source.DisplaySegments.Single();
            AiAnswerAnchor Anchor(string quote)
            {
                var start = plain.IndexOf(quote, StringComparison.Ordinal);
                Check(start >= 0, "Missing quoted fixture: " + quote);
                return new(source.Id, segment.Id, 0, quote.Length, quote, answer.Revision) { TextOffset = start };
            }
            var text = Anchor("原文甲"); var formula = Anchor("$a+b$"); var table = Anchor("表格重点"); var ambiguous = Anchor("重复词");
            var target = new AiTurn("第二问", "第二轮答复") { Collapsed = true, SentAnnotations =
                [new AiEntry { AnswerAnchor = text }, new AiEntry { AnswerAnchor = formula }, new AiEntry { AnswerAnchor = table }, new AiEntry { AnswerAnchor = ambiguous }] };
            var liveField = typeof(AiPanel).GetField("_foldingLiveTurn", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var readyField = typeof(AiPanel).GetField("_liveBookmarkCheckpointReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
            liveField.SetValue(panel, target); readyField.SetValue(panel, false);
            Invoke(panel, "InvalidateSameConversationLinks"); panel.RefreshVisibleBranchLinks();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(answer.BranchLinkBlocks.Count == 0, "An uncheckpointed or failed send created links");
            ((FrameworkElement)panel.FindName("LiveContainer")).Visibility = Visibility.Visible;
            readyField.SetValue(panel, true); Invoke(panel, "InvalidateSameConversationLinks"); panel.RefreshVisibleBranchLinks();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(answer.BranchLinkBlocks.Count == 3 && answer.BranchLinkBlocks.Values.All(v => Equals(((Button)v.Child).Content, "🗨️ [2]")), "Checkpointed text, formula and table references did not produce the expected live links");
            Check(answer.CopySource(answer.Document.ContentStart, answer.Document.ContentEnd) == plain && !answer.CopyForClipboard(answer.Document.ContentStart, answer.Document.ContentEnd).Contains("🗨️"), "Source links changed copied content");
            CheckCopySourceLengths(answer);
            foreach (var nested in Descendants<MarkdownView>(answer)) CheckCopySourceLengths(nested);
            Check(await panel.NavigateSameConversationTurnAsync(target.Id), "Could not navigate to a checkpointed live turn");
            conversation.Turns.Add(target); readyField.SetValue(panel, false); liveField.SetValue(panel, null);
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(await panel.NavigateSameConversationTurnAsync(target.Id) && !target.Collapsed, "Navigating to a saved target did not expand it");
            var branch = new AiConversation { ParentConversationId = conversation.Id, BranchAnchor = text, Title = "旁支" };
            book.Conversations.Add(branch);
            var foreign = new AiConversation { Title = "其他会话" }; book.Conversations.Add(foreign);
            var foreignEntry = AiSessionExcerptReferences.Create(foreign.Id, text, null, null);
            var third = new AiTurn("第三问", "第三轮答复") { SentAnnotations = [new AiEntry { AnswerAnchor = text }, foreignEntry] };
            conversation.Turns.Add(third); Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(answer.BranchLinkBlocks.Count == 5 && answer.BranchLinkBlocks.Values.Count(v => Equals(((Button)v.Child).Content, "🗨️ [3]")) == 1 &&
                answer.BranchLinkBlocks.Values.Any(v => Equals(((Button)v.Child).Content, "🗨️ 旁支")), "Same-session links conflicted with an existing branch or foreign reference");
            conversation.Turns.Remove(target); Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!; answer = views[source.Id].Answers.Single();
            Check(answer.BranchLinkBlocks.Count == 2 && answer.BranchLinkBlocks.Values.Any(v => Equals(((Button)v.Child).Content, "🗨️ [2]")) &&
                answer.BranchLinkBlocks.Values.Any(v => Equals(((Button)v.Child).Content, "🗨️ 旁支")), "Deleting a target did not remove its links and renumber later targets");
            third.Collapsed = true;
            await panel.SaveAsync();
            var saved = AiStorage.Load(project);
            Check(saved.Active.Turns.Last().SentAnnotations[0].AnswerAnchor == text, "Saved source anchor did not survive reload");
            await panel.SetProjectAsync(project); host.UpdateLayout();
            Check(await panel.NavigateBranchSourceAsync(text), "Could not revisit the source after reopening");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!; answer = views[source.Id].Answers.Single();
            var reopenedLink = answer.BranchLinkBlocks.Values.Single(item => Equals(((Button)item.Child).Content, "🗨️ [2]"));
            Check(answer.BranchLinkBlocks.Values.Any(item => Equals(((Button)item.Child).Content, "🗨️ 旁支")), "Reopening lost the existing cross-session branch link");
            Capture(host, Path.Combine(output, "same-conversation-inline.png"));
            ((Button)reopenedLink.Child).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 20 && ((AiNotebook)Field(panel, "_book")!).Active.Turns.Last().Collapsed; i++) await Task.Delay(50);
            Check(!((AiNotebook)Field(panel, "_book")!).Active.Turns.Last().Collapsed, "Clicking a reopened inline link did not navigate to and expand the target turn");
            Console.WriteLine("PASS: same-session inline links, checkpoint, text, formula, table, ambiguity, navigation, cross-session filtering, deletion and reload.");
        }
        finally { host.Close(); }
        await CheckSameConversationSubmissionAsync(owner, output);
    }

    private static async Task CheckAcceptedAnnotationRecoveryAsync(string output)
    {
        var project = Path.Combine(output, "annotation-recovery-" + Guid.NewGuid().ToString("N") + ".tex");
        var anchor = new AiAnswerAnchor("turn", "segment", 1, 4, "引用文字", "revision")
        { TextOffset = 1, PreviewBefore = "前文", PreviewAfter = "后文", PreviewParts = [new AiSelectionPart("text", "引用文字")] };
        var accepted = new AiEntry { Title = "引用", Question = "请解释", AnswerAnchor = anchor };
        var changed = new AiEntry { Title = "另一处引用", Question = "保留修改", AnswerAnchor = anchor };
        var unsent = new AiEntry { Title = "未发送引用", Question = "保持待发", AnswerAnchor = anchor };
        var acceptedSnapshot = accepted.Snapshot(); acceptedSnapshot.SentReferencePart = true; acceptedSnapshot.SentAttachmentPart = false;
        var changedSnapshot = changed.Snapshot();
        changed.AnswerAnchor = anchor with { PreviewParts = [new AiSelectionPart("text", "修改后的预览")] };
        var book = new AiNotebook(); book.Entries.AddRange([accepted, changed, unsent]);
        AiStorage.Save(project, book);
        var pending = new AiTurn("问题", "[本轮未完成]") { Prompt = "问题", Incomplete = true, SentAnnotations = [acceptedSnapshot, changedSnapshot] };
        var checkpoint = await AiTurnCheckpoint.StartAsync(project, book.Active.Id, book.Active.Title, pending, new AiStreamBuffer());
        await checkpoint.StopAsync();
        var recovered = AiStorage.Load(project);
        if (recovered.Entries.Any(entry => entry.Id == accepted.Id) ||
            !recovered.Entries.Any(entry => entry.Id == changed.Id && entry.AnswerAnchor?.PreviewParts?.Single().Text == "修改后的预览") ||
            !recovered.Entries.Any(entry => entry.Id == unsent.Id) || recovered.Turns.Single().SentAnnotations.Count != 2)
            throw new Exception("Checkpoint recovery did not consume exactly matching accepted annotations while preserving edited and unsent drafts");
        Console.WriteLine("PASS: deserialized accepted annotation recovery preserves edited and unsent references.");
    }

    private static async Task CheckSameConversationSubmissionAsync(Window owner, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new FollowUpHandler();
        using var panel = new AiPanel(new AiConnection(handler));
        var project = Path.Combine(output, "same-session-submit-" + Guid.NewGuid().ToString("N") + ".tex");
        await panel.SetProjectAsync(project);
        var models = (ComboBox)panel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 850, Height = 650, Content = panel }; host.Show();
        var tmp = AiTurnCheckpoint.PathFor(project) + ".tmp";
        try
        {
            var book = (AiNotebook)Field(panel, "_book")!;
            var source = new AiTurn("原问题", "这是需要继续追问的原文。");
            book.Turns.Add(source); Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var view = views[source.Id].Answers.Single(); await view.RenderTask;
            var text = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            const string quote = "继续追问";
            var anchor = new AiAnswerAnchor(source.Id, source.DisplaySegments.Single().Id, 0, quote.Length, quote, view.Revision) { TextOffset = text.IndexOf(quote, StringComparison.Ordinal) };
            Check(panel.AddEntry(new AiEntry { AnswerAnchor = anchor, Question = "为什么？" }), "Could not create a pending annotation");
            var composer = (TextBox)panel.FindName("Composer"); composer.Text = "请接着解释";
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!); Directory.CreateDirectory(tmp);
            await panel.SubmitAsync();
            Check(composer.Text == "请接着解释" && handler.Requests == 0 && view.BranchLinkBlocks.Count == 0, "Failed checkpoint lost the draft, contacted the model or created a link");
            Directory.Delete(tmp);
            var operation = panel.SubmitAsync();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(await panel.NavigateBranchSourceAsync(anchor), "Could not revisit the source while the next turn was generating");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!; view = views[source.Id].Answers.Single();
            var liveLink = view.BranchLinkBlocks.Values.Single(item => Equals(((Button)item.Child).Content, "🗨️ [2]"));
            var readingVersion = (int)Field(panel, "_readingRestoreVersion")!;
            ((Button)liveLink.Child).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check((int)Field(panel, "_readingRestoreVersion")! > readingVersion, "Clicking the real live link did not initiate navigation");
            var pending = (AiTurn)Field(panel, "_foldingLiveTurn")!;
            Check(await panel.NavigateSameConversationTurnAsync(pending.Id), "A real generating turn could not be reached from its source");
            handler.Release.TrySetResult(); await operation.WaitAsync(TimeSpan.FromSeconds(20));
            Check(book.Turns.Count == 2 && book.Turns[1].SentAnnotations.Single().AnswerAnchor == anchor, "Completing a real turn lost the sent annotation");
            await panel.StopAndSaveAsync();
            var restored = AiStorage.Load(project);
            Check(restored.Active.Turns.Count == 2 && restored.Active.Turns[1].SentAnnotations.Single().AnswerAnchor == anchor, "Reloading a real turn lost its source anchor");
            Console.WriteLine("PASS: real submission, checkpoint failure, draft retention, live navigation and saved link source.");
        }
        finally { handler.Release.TrySetResult(); if (Directory.Exists(tmp)) Directory.Delete(tmp); host.Close(); }
    }
}
