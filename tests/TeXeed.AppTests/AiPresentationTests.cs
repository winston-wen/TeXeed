using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiPresentationAsync(MainWindow owner, string output)
    {
        var count = 0; void Check(bool condition, string message) { count++; if (!condition) throw new Exception(message); }
        void DoubleClick(UIElement target, RoutedEvent? routed = null)
        {
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed ?? Mouse.MouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(click, 2); target.RaiseEvent(click);
        }
        var turn = new AiTurn("合成问题：检查排版与交互", "合成答复") { SentAt = DateTimeOffset.UnixEpoch,
            AnsweredAt = DateTimeOffset.UnixEpoch.AddMinutes(1), Segments = [new() { Kind = "activity", Text = "合成工具记录", Details = "参数\n{}\n\n结果\n合成文件：sample.tex" }, new() { Kind = "final", Text = "合成答复。所有资料均为测试生成。" }] };
        var view = new AiTurnView { Turn = turn }; var host = new Window { Owner = owner, Width = 800, Height = 680, Content = view };
        host.Show(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
        var sent = Descendants<TextBlock>(view).Single(t => t.Text.StartsWith("发送时间："));
        var row = Descendants<AiActivityView>(view).Single(); var header = (TextBlock)row.Header;
        var rich = Descendants<MarkdownView>(view).Where(v => v.IsVisible).ToArray();
        var reply = Descendants<AiReplyStatus>(view).Single(); var stamp = Descendants<TextBlock>(reply).Single();
        Check(sent.FontSize == 16 && header.FontSize == 16 && stamp.FontSize == 16 && rich.All(r => r.FontSize == 16), "Chat text sizes diverge");
        var xs = rich.Select(r => r.TranslatePoint(r.Document.Blocks.OfType<Paragraph>().First().ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft, view).X)
            .Append(sent.TranslatePoint(new Point(), view).X).Append(header.TranslatePoint(new Point(), view).X).Append(stamp.TranslatePoint(new Point(), view).X).ToArray();
        Console.WriteLine("Chat text left edges: " + string.Join(", ", xs.Select(x => x.ToString("F2"))));
        Check(xs.Max() - xs.Min() < 1, "Chat text left edges are not aligned");
        DoubleClick(sent); Check(turn.Collapsed && !row.IsVisible, "Double-clicking send time does not fold the turn");
        DoubleClick(reply); Check(!turn.Collapsed, "Answer-time expansion stopped working");
        foreach (var theme in new[] { "ristretto", "github-light-default" })
        {
            ThemeManager.Apply(theme); row.IsExpanded = true; host.UpdateLayout();
            Check(Descendants<AiWaveRule>(reply).Single().ActualWidth > 100, "Wave separator does not fill the available width");
            Capture(host, Path.Combine(output, "ai-presentation-" + theme + ".png"));
        }
        host.Close(); ThemeManager.Apply("ristretto");
        var store = (AiWorkspaceStore)Field(owner, "_aiStore")!;
        var conversation = new AiConversation { Id = "rename-fixture-" + Guid.NewGuid().ToString("N"), Title = "Original synthetic title", Draft = "Preserved draft" };
        store.Book.Conversations.Add(conversation); await store.SaveAsync();
        await CallAsync(owner, "OpenAiConversationAsync", conversation.Id); owner.UpdateLayout();
        var tabs = (TabWorkspace)Field(owner, "_workspaceTabs")!; var key = TabWorkspace.AiConversationKey(conversation.Id);
        var strip = (EditorTabStrip)owner.FindName(tabs.Owner(key) == TabSide.Left ? "FileTabs" : "RightFileTabs");
        var tab = ((StackPanel)strip.FindName("Items")).Children.OfType<Border>().Single(b => Equals(b.Tag, key));
        async Task Rename(bool accept)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
            {
                Window? dialog = null;
                try
                {
                    dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "重命名 AI 对话");
                    Check(dialog.Owner == owner && System.Windows.Interop.ComponentDispatcher.IsThreadModal, "Tab rename is not an owned modal dialog");
                    Check(!Descendants<TextBox>(tab).Any(), "An editor was inserted into the tab");
                    var input = Descendants<TextBox>(dialog).Single();
                    Check(input.Text == conversation.Title && input.MaxLength == 200, "Rename dialog lost the full title or limit");
                    input.Text = "  "; var save = Descendants<Button>(dialog).Single(b => Equals(b.Content, "确定"));
                    Check(!save.IsEnabled, "Blank title may be committed"); input.Text = "Renamed synthetic title";
                    if (accept)
                    {
                        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        for (var i = 0; dialog.IsVisible && i < 100; i++) await Task.Delay(10);
                        Check(!dialog.IsVisible, "Rename dialog did not close after saving");
                    }
                    else dialog.Close();
                    completed.SetResult();
                }
                catch (Exception ex) { dialog?.Close(); completed.SetException(ex); }
            }));
            DoubleClick(tab, UIElement.PreviewMouseLeftButtonDownEvent); await completed.Task;
        }
        await Rename(false); Check(conversation.Title == "Original synthetic title", "Cancel renamed the conversation");
        await Rename(true); owner.UpdateLayout();
        Check(conversation.Title == "Renamed synthetic title" && AiStorage.Load(store.Project).Conversations.Single(c => c.Id == conversation.Id).Title == conversation.Title, "Rename did not persist");
        Check(Equals(tab.ToolTip, conversation.Title), "Tab did not follow dialog rename");
        Check(AiStorage.Load(store.Project).Conversations.Single(c => c.Id == conversation.Id).Draft == "Preserved draft", "Rename lost the saved draft");
        Console.WriteLine($"AI presentation: {count} checks passed (type, alignment, time folds, waves, modal rename, persistence and draft).");
    }
}
