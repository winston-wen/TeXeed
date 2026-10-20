using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckAiHistoryAsync(Window owner, string output)
    {
        var count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MouseButtonEventArgs MouseDown(int clicks)
        {
            var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(e, clicks); return e;
        }
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Content = panel, Width = 820, Height = 840 };
        host.Show();
        var project = Path.Combine(output, "history.tex"); await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Conversations.Clear();
        for (var i = 0; i < 75; i++) book.Conversations.Add(new()
        { Id = "history-" + i, Title = "历史_" + i, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i), Turns = [new("问题_" + i, "回答_" + i)] });
        book.ActiveConversationId = "history-0"; book.Active.HeaderPinned = true; Invoke(panel, "ShowCurrentConversation"); await panel.SaveAsync();
        ((TextBlock)panel.FindName("ConversationTitle")).RaiseEvent(MouseDown(2)); var editor = (TextBox)panel.FindName("TitleEditor");
        Check(editor.IsVisible, "Double-clicking the header did not show its title editor");
        editor.Text = "自定义_标题超过二十个字符也要原样显示不被截断";
        await panel.CommitTitleRenameAsync(); host.UpdateLayout();
        Check(((TextBlock)panel.FindName("ConversationTitle")).Text == editor.Text, "Header rename truncated or interpreted literal text");
        Check(AiStorage.Load(project).Active.Title == editor.Text, "Header rename was not saved");
        Invoke(panel, "RefreshTranscript");
        Check(((TextBlock)panel.FindName("ConversationTitle")).Text == editor.Text, "Transcript refresh truncated a custom title");
        var previous = editor.Text; panel.BeginTitleRename(); editor.Text = "取消改名";
        editor.RaiseEvent(CreateKey(host, Key.Escape, Keyboard.PreviewKeyDownEvent));
        Check(book.Active.Title == previous, "Escape committed a canceled header rename");
        var border = (Border)panel.FindName("ComposerBorder");
        panel.BeginTitleRename(); editor.Text = "标题_点击空白提交";
        var headerOutside = MouseDown(1); headerOutside.RoutedEvent = Mouse.PreviewMouseDownEvent; border.RaiseEvent(headerOutside);
        for (var i = 0; i < 50 && ((FrameworkElement)panel.FindName("TitleEditControls")).IsVisible; i++) await Task.Delay(10);
        Check(book.Active.Title == editor.Text && !((FrameworkElement)panel.FindName("TitleEditControls")).IsVisible, "Header blank-area click did not submit its title");
        Check(border.CornerRadius == new CornerRadius(0) && border.Padding == new Thickness(0), "Composer must be square without surrounding editor padding");
        foreach (var width in new[] { 820d, 420d })
        {
            host.Width = width; host.UpdateLayout();
            var send = (Button)panel.FindName("SubmitButton"); var toolbar = (Border)panel.FindName("ComposerToolbar");
            Check(send.ActualWidth == 28 && send.ActualHeight == 28 && ((ComboBox)panel.FindName("PermissionPicker")).ActualHeight == send.ActualHeight, "Composer controls are not equal height");
            var chrome = Descendants<Border>(send).First();
            Check(chrome.CornerRadius == new CornerRadius(0), "Send button is not square");
            Check(toolbar.ActualHeight <= (width > 500 ? 34 : 65), "Composer toolbar reserves excess vertical space");
            var area = (Grid)panel.FindName("ComposerEditor");
            Check(Math.Abs(area.ActualWidth - border.ActualWidth + border.BorderThickness.Left + border.BorderThickness.Right) < .2, "Editor does not fill the border interior");
            Capture(host, Path.Combine(output, "composer-" + (int)width + ".png"));
        }
        var dialog = new AiHistoryWindow(book, panel.DeleteConversationsAsync, panel.RenameConversationAsync) { Owner = host };
        dialog.Show(); dialog.UpdateLayout();
        var list = (ItemsControl)dialog.FindName("ConversationList"); var scroll = (ScrollBar)dialog.FindName("HistoryScrollbar");
        Check(list.Items.Count == dialog.Page.Capacity && list.Items.Count < 75, "History eagerly constructs offscreen rows");
        Check(dialog.Page.Selected.Count == 0 && !((Button)dialog.FindName("DeleteButton")).IsEnabled, "History implicitly checked its active item");
        var firstTitle = (TextBlock)((Grid)list.Items[0]).Children[1];
        firstTitle.RaiseEvent(MouseDown(1));
        Check(dialog.Page.Selected.Count == 0, "Title click selected a bulk checkbox");
        firstTitle.RaiseEvent(MouseDown(2)); await Task.Delay(550);
        Check(dialog.IsVisible && Field(dialog, "_editor") is TextBox, "Double-click opened the conversation instead of renaming");
        var rename = (TextBox)Field(dialog, "_editor")!; rename.Text = "保留_下划线_双击改名";
        await dialog.CommitRenameAsync();
        Check(book.Conversations[0].Title == rename.Text && AiStorage.Load(project).Conversations[0].Title == rename.Text, "History rename was not persisted literally");
        Check(dialog.Page.Selected.Count == 0, "Rename changed bulk selection");
        dialog.BeginRename("history-0"); var outsideEditor = (TextBox)Field(dialog, "_editor")!; outsideEditor.Text = "点击空白_提交";
        var outside = MouseDown(1); outside.RoutedEvent = Mouse.PreviewMouseDownEvent;
        ((Grid)dialog.FindName("DialogBody")).RaiseEvent(outside);
        for (var i = 0; i < 50 && Field(dialog, "_editor") is not null; i++) await Task.Delay(10);
        Check(book.Conversations[0].Title == "点击空白_提交" && Field(dialog, "_editor") is null, "Clicking outside failed to commit the rename");
        dialog.BeginRename("history-0"); var blankEditor = (TextBox)Field(dialog, "_editor")!; blankEditor.Text = " "; await dialog.CommitRenameAsync();
        Check(Field(dialog, "_editor") is not null && book.Conversations[0].Title == "点击空白_提交", "Empty rename overwrote the title");
        blankEditor.RaiseEvent(CreateKey(dialog, Key.Escape, Keyboard.PreviewKeyDownEvent));
        Check(Field(dialog, "_editor") is null && dialog.IsVisible, "Escape did not cancel the rename independently of closing history");
        var check = (CheckBox)((Grid)list.Items[0]).Children[0];
        check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(dialog.Page.Selected.SetEquals(new[] { "history-0" }), "Checkbox click did not exclusively update selection");
        scroll.Value = 24; dialog.UpdateLayout(); var anchor = dialog.Page.Visible.First().Id;
        dialog.Height = 430; dialog.UpdateLayout();
        Check(dialog.Page.Visible.First().Id == anchor && dialog.Page.Capacity < 10, "Resizing lost the first visible title");
        Check(dialog.Page.Selected.Contains("history-0"), "Resizing cleared an offscreen selection");
        ((TextBox)dialog.FindName("PageBox")).Text = "999"; Invoke(dialog, "Go_Click", dialog, new RoutedEventArgs());
        Check(dialog.Page.PageNumber == dialog.Page.PageCount, "Page jump did not clamp past the end");
        ((TextBox)dialog.FindName("PageBox")).Text = "not a number"; Invoke(dialog, "Go_Click", dialog, new RoutedEventArgs());
        Check(((TextBox)dialog.FindName("PageBox")).Text == dialog.Page.PageNumber.ToString(), "Invalid page input was not normalized");
        var query = (TextBox)dialog.FindName("SearchBox"); query.Text = "回答_74"; dialog.UpdateLayout();
        Check(list.Items.Count == 1 && dialog.Page.Visible.Single().Id == "history-74", "Full-answer history search failed");
        Check(dialog.Page.Selected.Contains("history-0"), "Filtering lost selected IDs on another page");
        Click(Descendants<Button>(dialog).Single(b => Equals(b.Content, "全选")));
        Check(dialog.Page.Selected.SetEquals(new[] { "history-0", "history-74" }), "Select all did not use filtered matches");
        Click((Button)dialog.FindName("DeleteButton"));
        for (var i = 0; i < 50 && !((Grid)dialog.FindName("DialogBody")).IsEnabled; i++) await Task.Delay(20);
        Check(book.Conversations.Count == 73 && book.Conversations.All(c => c.Id is not ("history-0" or "history-74")), "Bulk delete failed across pages or filters");
        Check(AiStorage.Load(project).Conversations.Count == 73 && book.Active.Id == "history-1", "Deleting active conversation lost the next active conversation or persistence");
        query.Clear(); dialog.Height = 600; dialog.UpdateLayout();
        var theme = ThemeManager.Current.Id;
        foreach (var name in new[] { "ristretto", "github-light-default" })
        {
            ThemeManager.Apply(name); dialog.UpdateLayout(); Capture(dialog, Path.Combine(output, "history-" + name + ".png"));
            var title = Descendants<TextBlock>(list).First(t => t.FontSize == 16);
            Check(title.FontSize == ((TextBlock)panel.FindName("ConversationTitle")).FontSize, "History title font size differs from header");
        }
        ThemeManager.Apply(theme);
        dialog.Close();
        // The actual toolbar entry must create a modal window, rather than the retired popup.
        Click((Button)panel.FindName("HistoryButton"));
        for (var i = 0; i < 100 && !Application.Current.Windows.OfType<AiHistoryWindow>().Any(); i++) await Task.Delay(10);
        var modal = Application.Current.Windows.OfType<AiHistoryWindow>().Single();
        Check(!IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(host).Handle) && modal.Owner == host, "History toolbar did not open an owned modal dialog");
        modal.Close(); await Task.Delay(30); host.Close();
        Console.WriteLine($"AI history: {count} checks passed (paging, selection, rename, persistence, modal entry, compact composer and themes).");
    }
}
