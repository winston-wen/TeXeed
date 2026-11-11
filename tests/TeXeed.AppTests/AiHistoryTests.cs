using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;
internal static partial class Program
{
    private static async Task CheckAiHistoryAsync(Window owner,string output)
    {
        var count=0;void Check(bool ok,string message){count++;if(!ok)throw new Exception(message);}
        var newerProject = Path.Combine(output, "newer-notebook.tex");
        var newerPath = AiStorage.NotebookPath(newerProject);
        Directory.CreateDirectory(Path.GetDirectoryName(newerPath)!);
        var newerJson = JsonNode.Parse("""
        {"ActiveConversationId":"conversation-1","FutureBook":{"version":4},"Conversations":[
          {"Id":"conversation-1","Title":"旧标题","Draft":"待发草稿",
           "SharedDocument":{"Source":"AI 文稿","Comments":[{"Text":"评论"}]},
           "CommentDrafts":{"draft":"未发评论"},"FutureConversation":[1,2,3],
           "Entries":[{"Id":"entry-1","Question":"批注","FutureEntry":{"a":1}}],
           "Turns":[{"Id":"turn-1","Question":"问题","Answer":"回答",
             "MentorRequest":{"Source":"原请求快照","References":[{"Comment":"意见"}]},
             "FutureTurn":true,"Segments":[{"Id":"segment-1","Kind":"final","Text":"回答","FutureSegment":"保留"}]}]}]}
        """)!;
        File.WriteAllText(newerPath, newerJson.ToJsonString());
        var newerStore = await AiWorkspaceStore.LoadAsync(newerProject);
        newerStore.Book.Active.Title = "新标题";
        newerStore.Book.Active.Draft = "更新草稿";
        await newerStore.SaveAsync();
        var savedNewer = JsonNode.Parse(File.ReadAllText(newerPath))!;
        var beforeConversation = newerJson["Conversations"]![0]!;
        var afterConversation = savedNewer["Conversations"]![0]!;
        Check(JsonNode.DeepEquals(newerJson["FutureBook"], savedNewer["FutureBook"]), "Unknown notebook data was lost");
        foreach (var key in new[] { "SharedDocument", "CommentDrafts", "FutureConversation" })
            Check(JsonNode.DeepEquals(beforeConversation[key], afterConversation[key]), $"Conversation data {key} was lost");
        Check(JsonNode.DeepEquals(beforeConversation["Entries"]![0]!["FutureEntry"], afterConversation["Entries"]![0]!["FutureEntry"]), "Unknown annotation data was lost");
        foreach (var key in new[] { "MentorRequest", "FutureTurn" })
            Check(JsonNode.DeepEquals(beforeConversation["Turns"]![0]![key], afterConversation["Turns"]![0]![key]), $"Turn data {key} was lost");
        Check(JsonNode.DeepEquals(beforeConversation["Turns"]![0]!["Segments"]![0]!["FutureSegment"], afterConversation["Turns"]![0]!["Segments"]![0]!["FutureSegment"]), "Unknown segment data was lost");
        Check((string?)afterConversation["Title"] == "新标题" && (string?)afterConversation["Draft"] == "更新草稿", "Known changes were not saved alongside newer data");
        var blankProject = Path.Combine(output, "blank-cleanup-" + Guid.NewGuid().ToString("N") + ".tex");
        var blankBook = new AiNotebook();
        var firstBlank = blankBook.Active;
        var secondBlank = blankBook.NewConversation();
        var withDraft = new AiConversation { Draft = " " };
        var withReference = new AiConversation { Entries = [new AiEntry { Question = "引用草稿" }] };
        var withAttachment = new AiConversation { Entries = [new AiEntry { Files = [new AiAttachedFile("资料.txt", "text/plain", "YQ==")] }] };
        var withBookmark = new AiConversation { Bookmarks = [new AiUserBookmark { Label = "保留" }] };
        var withTitle = new AiConversation { Title = "用户命名" };
        var withSettings = new AiConversation { BookmarkBallX = 0.4 };
        var withFuture = new AiConversation { AdditionalData = new() { ["FutureData"] = System.Text.Json.JsonDocument.Parse("{\"draft\":true}").RootElement.Clone() } };
        var relatedParent = new AiConversation();
        var relatedChild = new AiConversation { ParentConversationId = relatedParent.Id };
        blankBook.Conversations.AddRange([withDraft, withReference, withAttachment, withBookmark, withTitle, withSettings, withFuture, relatedParent, relatedChild]);
        AiStorage.Save(blankProject, blankBook);
        var beforeCleanup = File.ReadAllBytes(AiStorage.NotebookPath(blankProject));
        var skipped = await AiWorkspaceStore.LoadAsync(blankProject, () => true);
        Check(skipped.Book.Conversations.Any(c => c.Id == firstBlank.Id) && File.ReadAllBytes(AiStorage.NotebookPath(blankProject)).SequenceEqual(beforeCleanup), "Concurrent TeXeed instance did not prevent automatic cleanup");
        var backupFailed = await AiWorkspaceStore.LoadAsync(blankProject, () => false, (_, _) => throw new IOException("simulated backup failure"));
        Check(backupFailed.EmptyCleanupBackupFailed && backupFailed.Book.Conversations.Any(c => c.Id == firstBlank.Id) && File.ReadAllBytes(AiStorage.NotebookPath(blankProject)).SequenceEqual(beforeCleanup), "Backup failure blocked loading or changed the notebook");
        var cleaned = await AiWorkspaceStore.LoadAsync(blankProject, () => false);
        var backup = Directory.GetFiles(Path.GetDirectoryName(AiStorage.NotebookPath(blankProject))!, Path.GetFileName(AiStorage.NotebookPath(blankProject)) + ".blank-cleanup-*.bak");
        Check(backup.Length == 1 && File.ReadAllBytes(backup[0]).SequenceEqual(beforeCleanup), "Cleanup did not preserve an exact backup before saving");
        Check(cleaned.Book.Active.Id == secondBlank.Id && cleaned.Book.Conversations.All(c => c.Id != firstBlank.Id), "Duplicate blank sessions were not cleaned or active blank was lost");
        Check(new[] { withDraft, withReference, withAttachment, withBookmark, withTitle, withSettings, withFuture, relatedParent, relatedChild }.All(c => cleaned.Book.Conversations.Any(x => x.Id == c.Id)), "Cleanup removed a draft, reference, attachment, bookmark, custom title, settings, unknown data, or family relationship");
        Check(AiStorage.Load(blankProject).Conversations.All(c => c.Id != firstBlank.Id), "Blank session cleanup was not saved");
        var closingProject = Path.Combine(output, "blank-closing-" + Guid.NewGuid().ToString("N") + ".tex");
        var closingBook = new AiNotebook(); var closingFirst = closingBook.Active; var closingSecond = closingBook.NewConversation();
        var closingStore = new AiWorkspaceStore(closingProject, closingBook); await closingStore.SaveAsync();
        Check(!closingStore.RemoveClosedDuplicateBlank(closingFirst.Id, () => true) && closingBook.Conversations.Count == 2, "Closing cleaned up while another instance was running");
        Check(!closingStore.RemoveClosedDuplicateBlank(closingFirst.Id, () => false, (_, _) => throw new UnauthorizedAccessException("simulated backup failure")) && closingStore.EmptyCleanupBackupFailed && closingBook.Conversations.Count == 2, "Failed backup should preserve duplicate blank while closing");
        Check(closingStore.RemoveClosedDuplicateBlank(closingSecond.Id, () => false) && closingBook.Active.Id == closingFirst.Id, "Closing a duplicate blank failed to keep a valid active session");
        await closingStore.SaveAsync();
        Check(AiStorage.Load(closingProject).Conversations.Count == 1 && Directory.GetFiles(Path.GetDirectoryName(AiStorage.NotebookPath(closingProject))!, Path.GetFileName(AiStorage.NotebookPath(closingProject)) + ".blank-cleanup-*.bak").Length == 1, "Closing cleanup was not backed up and saved");
        var main=(MainWindow)owner;
        var store=(AiWorkspaceStore)Field(main,"_aiStore")!;var tabs=(TabWorkspace)Field(main,"_workspaceTabs")!;
        var settings=(AiSettingsPanel)Field(main,"_aiSettings")!;
        var models=(ComboBox)settings.FindName("Models");models.ItemsSource=new[]{new AiModel("fixture","测试模型")};models.SelectedIndex=0;
        for(var i=0;i<75;i++)store.Book.Conversations.Add(new(){Id="multi-"+i,Title="会话_"+i,LastSentAt=DateTimeOffset.Parse("2026-10-20T12:00:00+08:00").AddMinutes(-i),Turns=[new("问题_"+i,"回答_"+i){SentAt=DateTimeOffset.Parse("2026-10-20T12:00:00+08:00").AddMinutes(-i)}]});
        await store.SaveAsync();
        await CallAsync(main,"OpenAiConversationAsync","multi-0");var first=(AiPanel)Field(main,"_aiPanel")!;
        ((TextBox)first.FindName("Composer")).Text="草稿_A";await first.SaveAsync();
        await CallAsync(main,"OpenAiConversationAsync","multi-1");var second=(AiPanel)Field(main,"_aiPanel")!;
        ((TextBox)second.FindName("Composer")).Text="草稿_B";await second.SaveAsync();
        Check(first!=second && first.ConversationId=="multi-0" && second.ConversationId=="multi-1","Conversation views are not independent");
        Check(tabs.Tabs(TabSide.Right).Count(TabWorkspace.IsAiConversation)==2,"Multiple AI tabs did not open");
        await CallAsync(main,"OpenAiConversationAsync","multi-0");
        Check(ReferenceEquals(Field(main,"_aiPanel"),first)&&tabs.Tabs(TabSide.Right).Count(TabWorkspace.IsAiConversation)==2,"Reopening duplicated a conversation tab");
        Invoke(main,"MoveWorkspaceTab",TabWorkspace.AiConversationKey("multi-0"),TabSide.Left,0,false);main.UpdateLayout();
        Check(first.IsVisible&&second.IsVisible,"Two conversations cannot be visible in opposite panes");
        Check(first.FindName("ChatHeader") is null&&first.FindName("HeadingPeek") is null,"Removed body header is still present");
        var disk=AiStorage.Load(store.Project);
        Check(disk.Conversations.Single(c=>c.Id=="multi-0").Draft=="草稿_A"&&disk.Conversations.Single(c=>c.Id=="multi-1").Draft=="草稿_B","Saving one view overwrote another view's draft");
        Check(!ReferenceEquals(first.ShellApprovals,second.ShellApprovals),"Permission approvals leaked between conversations");
        var fullTitle=new string('汉',16)+"_标题";
        await CallAsync(main,"RenameAiConversationAsync","multi-0",fullTitle);main.UpdateLayout();
        var strip=(EditorTabStrip)main.FindName("FileTabs");var tab=((StackPanel)strip.FindName("Items")).Children.OfType<Border>().Single(v=>Equals(v.Tag,TabWorkspace.AiConversationKey("multi-0")));
        Check(Descendants<TextBlock>(tab).Single().Text==new string('汉',15)+"…","Open tab did not track rename/truncation");
        Check(Equals(tab.ToolTip,fullTitle),"Truncated tab lost its complete title");
        Check(new AiConversation{Title=new string('汉',15)}.TabTitle==new string('汉',15),"Exactly 15 characters were truncated");
        Check(new AiConversation{Title="😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀"}.TabTitle=="😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀…","Truncation split a text element");
        Invoke(main,"AiRecent_Opened",main,new RoutedEventArgs());
        var recent=(MenuItem)main.FindName("AiRecentMenu");
        Check(recent.Items.OfType<MenuItem>().Take(5).Select(i=>i.Header.ToString()).SequenceEqual(store.Book.Conversations.Where(c=>c.LastSend is not null).OrderByDescending(c=>c.LastSend).Take(5).Select(c=>c.TabTitle)),"Recent five are not sorted by last send");
        var originalSend=store.Book.Conversations.Single(c=>c.Id=="multi-0").LastSend;
        await CallAsync(main,"RenameAiConversationAsync","multi-20","改名不应挤进最近记录");
        Check(store.Book.Conversations.Single(c=>c.Id=="multi-0").LastSend==originalSend&&store.Book.Conversations.Single(c=>c.Id=="multi-20").LastSend<originalSend,"Rename changed send ordering");
        Invoke(main,"OpenAiHistory");main.UpdateLayout();
        var history=(AiHistoryView)Field(main,"_aiHistory")!;
        Check(tabs.Owner(TabWorkspace.AiHistoryKey)is not null && history.IsVisible && main.IsEnabled,"History is not a nonmodal workspace tab");
        Check(history.FindName("HistoryScrollbar")is null&&Descendants<ScrollBar>(history).All(s=>!s.IsVisible),"History still has a visible scrollbar");
        var list=(ItemsControl)history.FindName("ConversationList");
        Check(list.Items.Count<=history.Page.Capacity && list.Items.Count<75,"History constructs offscreen rows");
        var row=list.Items.Cast<Grid>().Single(r=>Equals(r.Tag,"multi-0"));
        var checkbox=(CheckBox)row.Children[0];
        Check(!checkbox.IsEnabled&&checkbox.ToolTip!.ToString()!.Contains("关闭"),"Open conversation deletion lacks an explanation");
        var rejected=false;try{await CallAsync(main,"DeleteAiConversationsAsync",(object)new[]{"multi-0"});}catch(InvalidOperationException){rejected=true;}
        Check(rejected&&store.Book.Conversations.Any(c=>c.Id=="multi-0"),"An open conversation was deleted");
        history.BeginRename("multi-0");var input=(TextBox)Field(history,"_editor")!;input.Text="窗口跟踪_历史改名";await history.CommitRenameAsync();
        Check(Descendants<TextBlock>(tab).Single().Text=="窗口跟踪_历史改名","History rename did not update the opened window");
        history.Page.Scroll(24);history.RefreshExternal();var anchor=history.Page.Visible.First().Id;
        var previousHeight=main.Height;main.Height=650;main.UpdateLayout();
        Check(history.Page.Visible.First().Id==anchor,"Resizing lost the first visible title");main.Height=previousHeight;
        ((TextBox)history.FindName("SearchBox")).Text="回答_74";main.UpdateLayout();
        Check(history.Page.Matches.Single().Id=="multi-74","History answer search failed");
        ((TextBox)history.FindName("SearchBox")).Clear();
        Invoke(history,"SelectAll_Click",history,new RoutedEventArgs());
        Check(!history.Page.Selected.Contains("multi-0")&&!history.Page.Selected.Contains("multi-1"),"Select all included opened conversations");
        Invoke(history,"ClearSelection_Click",history,new RoutedEventArgs());
        Invoke(main,"CloseTabs",TabWorkspace.AiConversationKey("multi-0"),TabCloseScope.This);
        for(var i=0;i<200 && ((HashSet<string>)Field(main,"_closingAi")!).Contains("multi-0");i++)await Task.Delay(10);
        Invoke(main,"OpenAiRelations","multi-0");
        var relationKey = TabWorkspace.AiRelationsKey("multi-0");
        Check(tabs.Owner(relationKey) is not null, "Could not open relation view of a closed conversation");
        await CallAsync(main,"DeleteAiConversationsAsync",(object)new[]{"multi-0"});
        Check(tabs.Owner(relationKey) is null && !((Dictionary<string,FrameworkElement>)Field(main,"_aiRelationViews")!).ContainsKey(relationKey), "Deleting a conversation left its relation view open");
        Check(store.Book.Conversations.All(c=>c.Id!="multi-0")&&store.Book.Conversations.Single(c=>c.Id=="multi-1").Draft=="草稿_B","Closed conversation deletion lost another conversation");
        Check(AiStorage.Load(store.Project).Conversations.All(c=>c.Id!="multi-0"),"Deletion was not persisted");
        Check(Descendants<Button>(history).Any(b => Equals(b.Content, "刷新")), "History refresh button did not display its text label: " + string.Join(", ", Descendants<Button>(history).Select(b => b.Content?.ToString())));
        Capture(main,Path.Combine(output,"ai-history-tab.png"));
        await CallAsync(main,"OpenAiConversationAsync","multi-1");Invoke(main,"OpenAiSettings");main.UpdateLayout();
        Check(tabs.Owner(TabWorkspace.AiSettingsKey)is not null,"AI settings menu cannot open its tab");
        new AiCredentials{ApiKey="fixture-never-send",UseApiKey=true}.Save();
        var concurrentProject=Path.Combine(output,"concurrent.tex");var book=new AiNotebook();var a=book.Active;var b=book.NewConversation();book.ActiveConversationId=a.Id;
        var shared=new AiWorkspaceStore(concurrentProject,book);
        using var handlerA=new StalledAiHandler();using var handlerB=new StalledAiHandler();
        using var paneA=new AiPanel(new AiConnection(handlerA));using var paneB=new AiPanel(new AiConnection(handlerB));
        foreach(var pane in new[]{paneA,paneB}){var m=(ComboBox)pane.Settings.FindName("Models");m.ItemsSource=new[]{new AiModel("fixture","测试模型")};m.SelectedIndex=0;await pane.Settings.SaveSelectionAsync(CancellationToken.None);}
        paneA.Bind(shared,a.Id);paneB.Bind(shared,b.Id);
        ((TextBox)paneA.FindName("Composer")).Text="会话 A 的独立请求";((TextBox)paneB.FindName("Composer")).Text="会话 B 的独立请求";
        var sendA=paneA.SubmitAsync();await handlerA.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sendB=paneB.SubmitAsync();await handlerB.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(AiTurnCheckpoint.PathsFor(concurrentProject).Count()==2,"Concurrent requests share one recovery journal");
        var recovering=AiStorage.Load(concurrentProject);
        Check(recovering.Conversations.Single(c=>c.Id==a.Id).Turns.Single().Question.Contains("会话 A")&&recovering.Conversations.Single(c=>c.Id==b.Id).Turns.Single().Question.Contains("会话 B"),"Concurrent crash recovery crossed conversations");
        await paneA.StopAndSaveAsync();await sendA;
        Check(!sendB.IsCompleted&&AiTurnCheckpoint.PathsFor(concurrentProject).Count()==1,"Stopping one request stopped another or deleted its journal");
        await paneB.StopAndSaveAsync();await sendB;
        var settled=AiStorage.Load(concurrentProject);
        Check(settled.Conversations.Count==2&&settled.Conversations.All(c=>c.Turns.Count==1)&&!AiTurnCheckpoint.PathsFor(concurrentProject).Any(),"Concurrent completion lost or duplicated turns");
        Check(a.LastSend is not null&&b.LastSend>=a.LastSend,"Last-send time was not recorded at acceptance");
        await CallAsync(main,"OpenAiConversationAsync","multi-2");
        Invoke(main,"CloseTabs",TabWorkspace.AiConversationKey("multi-1"),TabCloseScope.All);
        var closing=(HashSet<string>)Field(main,"_closingAi")!;
        Check(closing.Contains("multi-1")&&closing.Contains("multi-2"),"Bulk close did not protect every closing conversation before awaiting saves");
        for(var i=0;i<200&&closing.Count>0;i++)await Task.Delay(10);
        Check(closing.Count==0&&second.Parent is null&&!((Dictionary<string,AiPanel>)Field(main,"_aiPanels")!).Values.Contains(second),"Bulk close left disposed views attached");
        await CallAsync(main,"OpenNewAiConversationAsync");
        var blankId = store.Book.Active.Id;
        await CallAsync(main,"OpenNewAiConversationAsync");
        Check(store.Book.Active.Id == blankId && store.Book.Conversations.Count(store.Book.IsEmptyNewConversation) == 1, "Opening a new conversation repeatedly created duplicate empty sessions");
        var blankPanel = (AiPanel)Field(main,"_aiPanel")!;
        ((TextBox)blankPanel.FindName("Composer")).Text = "尚未发送的草稿";
        await CallAsync(main,"OpenNewAiConversationAsync");
        Check(store.Book.Active.Id != blankId && store.Book.Conversations.Single(c => c.Id == blankId).Draft == "尚未发送的草稿", "Creating a new conversation lost an existing draft");
        Console.WriteLine($"AI workspace: {count} checks passed (multiple tabs, identity, rename, deletion guards, recent sends, paging, shared writes and concurrent recovery).");
    }
}
