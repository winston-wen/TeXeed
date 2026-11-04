using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiBookmarkUiAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        async Task Idle() { await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); owner.UpdateLayout(); }
        IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch
        { Paragraph paragraph => [paragraph], Section section => Paragraphs(section.Blocks), _ => [] });
        IEnumerable<Run> Runs(InlineCollection inlines) => inlines.SelectMany(i => i is Run r ? [r] : i is Span s ? Runs(s.Inlines) : []);
        AiBookmarkTarget Target(object row) => (AiBookmarkTarget)row.GetType().GetProperty("Target")!.GetValue(row)!;
        var project = Path.Combine(output, "bookmark-ui.tex");
        var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Content = panel, Width = 680, Height = 560, Title = "AI 书签交互测试" };
        host.Show(); host.Activate();
        GetCursorPos(out var originalPointer);
        var mouseDown = false;
        try
        {
            await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            const string answerText = "# 相同标题\n\n甲标签正文，用户可选择。\n\n## 相同标题\n\n乙标签正文，用户可选择。\n\n$$a+b$$";
            for (var i = 0; i < 120; i++)
                book.Turns.Add(new AiTurn("测试问题第 " + i + " 轮", answerText) { SentAt = DateTimeOffset.UnixEpoch,
                    Segments = [new AiSegment { Id = "ui-final-" + i, Kind = "final", Text = answerText }] });
            ((TextBox)panel.FindName("Composer")).Text = "用户草稿保留";
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await Idle();
            var originalConversation = book.Active.Id;
            var overlay = (Canvas)Field(panel, "_bookmarkOverlay")!;
            var ball = (Button)Field(panel, "_bookmarkBall")!;
            async Task Drag(Point destination)
            {
                host.Activate(); await Task.Delay(120);
                var start = ball.PointToScreen(new Point(ball.ActualWidth / 2, ball.ActualHeight / 2));
                var end = overlay.PointToScreen(destination);
                Check(SetCursorPos((int)start.X, (int)start.Y), "Cannot place pointer on bookmark ball");
                GetCursorPos(out var actualPointer);
                Console.WriteLine($"Bookmark drag setup: ball={start}, screen pointer={actualPointer.X},{actualPointer.Y}, foreground={GetForegroundWindow()}, host={new System.Windows.Interop.WindowInteropHelper(host).Handle}, visible={ball.IsVisible}, bounds={ball.ActualWidth}x{ball.ActualHeight}");
                await Task.Delay(60); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouseDown = true;
                for (var i = 0; i < 25 && !ReferenceEquals(Mouse.Captured, ball); i++) await Task.Delay(40);
                Check(ReferenceEquals(Mouse.Captured, ball), "Bookmark ball did not capture pointer");
                Check(SetCursorPos((int)Math.Max(0, end.X), (int)Math.Max(0, end.Y)), "Cannot move bookmark pointer");
                var expectedX = destination.X < 0 ? 0 : Math.Max(0, overlay.ActualWidth - ball.ActualWidth);
                var expectedY = destination.Y < 0 ? 0 : Math.Max(0, overlay.ActualHeight - ball.ActualHeight);
                for (var i = 0; i < 25 && (Math.Abs(Canvas.GetLeft(ball) - expectedX) > .5 || Math.Abs(Canvas.GetTop(ball) - expectedY) > .5); i++) await Task.Delay(40);
                Check((bool)Field(panel, "_bookmarkMoved")!, "Bookmark ball did not receive the physical drag");
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); mouseDown = false; await Task.Delay(80); await Idle();
                Console.WriteLine($"Bookmark drag start={start}, end={end}, actual={Canvas.GetLeft(ball):F2},{Canvas.GetTop(ball):F2}, overlay={overlay.ActualWidth:F2}x{overlay.ActualHeight:F2}");
            }
            var physical = Environment.GetEnvironmentVariable("TEXEED_BOOKMARK_PHYSICAL") == "1";
            if (physical)
            {
                await Drag(new Point(-600, -600));
                Check(Canvas.GetLeft(ball) <= .5 && Canvas.GetTop(ball) <= .5, "Bookmark ball escaped top/left bounds");
                await Drag(new Point(overlay.ActualWidth + 400, overlay.ActualHeight + 400));
                Check(Math.Abs(Canvas.GetLeft(ball) - Math.Max(0, overlay.ActualWidth - ball.ActualWidth)) < 1 &&
                    Math.Abs(Canvas.GetTop(ball) - Math.Max(0, overlay.ActualHeight - ball.ActualHeight)) < 1, "Bookmark ball escaped bottom/right bounds");
            }
            else
            {
                book.Active.BookmarkBallX = 1; book.Active.BookmarkBallY = 1;
                Invoke(panel, "RestoreBookmarkBallPosition"); host.UpdateLayout(); await Idle();
                Console.WriteLine("Physical bookmark drag and Ctrl+G skipped; set TEXEED_BOOKMARK_PHYSICAL=1 in an interactive foreground desktop.");
            }
            var savedX = book.Active.BookmarkBallX; var savedY = book.Active.BookmarkBallY;
            Check(savedX > .99 && savedY > .99, "Dragging did not save normalized ball position");

            var beforeOpen = GC.GetAllocatedBytesForCurrentThread(); var openWatch = Stopwatch.StartNew();
            Invoke(panel, "OpenBookmarkUi", false); host.UpdateLayout(); await Idle(); openWatch.Stop();
            var coldOpenMs = openWatch.Elapsed.TotalMilliseconds;
            var coldOpenBytes = GC.GetAllocatedBytesForCurrentThread() - beforeOpen;
            var initialDefaults = (ListBox)Field(panel, "_defaultBookmarks")!;
            Check(initialDefaults.Items.Count == 360, "The performance fixture did not index all 120 turns");
            var realizedRows = Descendants<ListBoxItem>(initialDefaults).Count();
            Check(realizedRows < 60, $"Bookmark menu realizes too many of 360 default rows: {realizedRows}");
            var rowHeights = Descendants<ListBoxItem>(initialDefaults).Select(item => item.ActualHeight).ToArray();
            var controlsOpen = Descendants<Control>(panel).Count();
            var visibleControlsOpen = Descendants<Control>(panel).Count(c => c.IsVisible && VisibleBounds(c, panel) is { Width: > 0, Height: > 0 });
            var searchTiming = new Dictionary<string, List<double>> { ["no_match_ms"] = [], ["clear_ms"] = [], ["no_match_bytes"] = [], ["clear_bytes"] = [] };
            var performanceSearch = (TextBox)Field(panel, "_bookmarkSearch")!;
            for (var i = 0; i < 8; i++)
            foreach (var (text, prefix) in new[] { ("不存在的合成书签", "no_match"), ("", "clear") })
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                performanceSearch.Text = text; host.UpdateLayout(); await Idle(); watch.Stop();
                if (i > 0) { searchTiming[prefix + "_ms"].Add(watch.Elapsed.TotalMilliseconds); searchTiming[prefix + "_bytes"].Add(GC.GetAllocatedBytesForCurrentThread() - allocated); }
            }
            File.WriteAllText(Path.Combine(output, "bookmark-ui-performance.json"), JsonSerializer.Serialize(new
            { turns = 120, indexed = initialDefaults.Items.Count, coldOpenMs, coldOpenBytes, realizedRows, rowHeights, controlsOpen, visibleControlsOpen, searchTiming }, new JsonSerializerOptions { WriteIndented = true }));
            ball.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();

            if (physical)
            {
                host.Activate(); ((TextBox)panel.FindName("Composer")).Focus();
                var originalLanguage = InputLanguageManager.Current.CurrentInputLanguage;
                try
                {
                    InputLanguageManager.Current.CurrentInputLanguage = System.Globalization.CultureInfo.GetCultureInfo("en-US");
                    keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x47, 0, 0, UIntPtr.Zero);
                    keybd_event(0x47, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
                    await Task.Delay(120); await Idle();
                }
                finally { keybd_event(0x11, 0, 2, UIntPtr.Zero); InputLanguageManager.Current.CurrentInputLanguage = originalLanguage; }
            }
            else { Invoke(panel, "OpenBookmarkUi", true); await Idle(); }
            var surface = (Border)Field(panel, "_bookmarkSurface")!;
            var search = (TextBox)Field(panel, "_bookmarkSearch")!;
            var defaults = (ListBox)Field(panel, "_defaultBookmarks")!;
            var users = (ListBox)Field(panel, "_userBookmarks")!;
            var order = (ComboBox)Field(panel, "_bookmarkOrder")!;
            Check(surface.IsVisible && (!physical || search.IsKeyboardFocusWithin), "Opening bookmarks did not focus the search field");
            Check(defaults.Items.Count == 360 && users.Items.Count == 0, "Automatic and user bookmark groups are not separate");
            var first = defaults.Items.Cast<object>().Select(Target).Where(t => t.TurnIndex == 0).ToArray();
            Check(first.Select(t => t.Label).SequenceEqual(["1:h1", "1:h2", "1:eq1"]) &&
                first[0].Description == first[1].Description && first[0].Id != first[1].Id,
                "Repeated heading text did not produce distinct grouped destinations");
            search.Text = "1:h"; await Idle();
            var tab = CreateKey(host, Key.Tab, Keyboard.PreviewKeyDownEvent); search.RaiseEvent(tab);
            Check(tab.Handled && search.Text == "1:h1" && defaults.Items.Count == 1, "Tab did not complete an exact bookmark prefix");
            search.Text = "31:h2"; await Idle();
            var targetTurn = book.Turns[30]; targetTurn.Collapsed = true;
            var outer = panel.GetBookmarks().Single(t => t.TurnIndex == 30 && t.Label == "31:h1");
            targetTurn.CollapsedSections[outer.SegmentId + ":" + outer.Position] = true;
            var enter = CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent); search.RaiseEvent(enter);
            for (var i = 0; i < 40 && targetTurn.Collapsed; i++) { await Task.Delay(30); host.UpdateLayout(); }
            await Idle();
            Check(enter.Handled && !targetTurn.Collapsed && !targetTurn.CollapsedSections[outer.SegmentId + ":" + outer.Position],
                "Search jump did not realize the offscreen turn and reopen folded ancestors");
            Check(!surface.IsVisible && ((ScrollViewer)panel.FindName("MessagesScroll")).VerticalOffset > 0, "Search jump did not close the menu or move the viewport");
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var view = views[targetTurn.Id].Answers.Single();
            await view.RenderTask; host.UpdateLayout(); await Idle();

            async Task AddLabel(string quote, string label)
            {
                var paragraph = Paragraphs(view.Document.Blocks).Single(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains(quote));
                var run = Runs(paragraph.Inlines).Single(r => r.Text.Contains(quote));
                view.Selection.Select(run.ContentStart, run.ContentEnd);
                var menu = view.ContextMenu ?? throw new Exception("Answer has no context menu");
                menu.PlacementTarget = view; menu.IsOpen = true; await Idle();
                var add = menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.Contains("添加标签") == true);
                Check(add.IsEnabled, "Selected text did not enable the add-bookmark command");
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = host.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    try
                    {
                        var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "添加标签" && w.IsVisible);
                        var input = Descendants<TextBox>(dialog).Single();
                        Check(!input.IsReadOnly, "Bookmark label dialog cannot be edited");
                        input.Text = label;
                        Descendants<Button>(dialog).Single(b => Equals(b.Content, "添加")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        completed.SetResult();
                    }
                    catch (Exception ex) { completed.SetException(ex); }
                }));
                add.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); menu.IsOpen = false; await Idle();
            }
            await AddLabel("乙标签正文", "A标签");
            await AddLabel("甲标签正文", "Z标签");
            await AddLabel("甲标签正文", "Z标签");
            Check(book.Active.Bookmarks.Count == 3, "Adding duplicate user labels changed or lost selections");
            ball.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Check(defaults.Items.Count == 360 && users.Items.Count == 3, "Bookmark menu did not refresh separate groups");
            Check(users.Items.Cast<object>().Select(Target).Select(t => t.Label).SequenceEqual(["Z标签", "Z标签", "A标签"]), "Document order is incorrect");
            order.SelectedIndex = 1; await Idle();
            Check(book.Active.BookmarkSort == "alphabetical", "Bookmark sort choice was not saved in the session");
            Check(users.Items.Cast<object>().Select(Target).Select(t => t.Label).SequenceEqual(["A标签", "Z标签", "Z标签"]), "Alphabetical order is incorrect");
            Capture(host, Path.Combine(output, "bookmark-menu-wide.png"));
            host.Width = 400; host.UpdateLayout(); await Idle();
            Capture(host, Path.Combine(output, "bookmark-menu-narrow.png"));
            host.Width = 680; host.UpdateLayout(); await Idle();
            var duplicate = users.Items.Cast<object>().First(row => Target(row).Label == "Z标签");
            users.ScrollIntoView(duplicate); host.UpdateLayout(); await Idle();
            var duplicateContainer = (ListBoxItem)users.ItemContainerGenerator.ContainerFromItem(duplicate);
            Descendants<Button>(duplicateContainer).Single(b => Equals(b.Content, "×")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Idle();
            Check(book.Active.Bookmarks.Count == 2 && book.Active.Bookmarks.Count(b => b.Label == "Z标签") == 1, "Deleting one duplicate removed the wrong labels");

            await panel.SaveAsync(); await panel.NewConversationAsync(); host.UpdateLayout(); await Idle();
            Check(!surface.IsVisible && book.Active.Id != originalConversation && book.Active.Bookmarks.Count == 0, "Switching conversation leaked menu or bookmarks");
            ball.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            Check(users.Items.Count == 0 && order.SelectedIndex == 0 && Canvas.GetTop(ball) < overlay.ActualHeight - ball.ActualHeight - 1, "New conversation inherited old sort or ball position");
            await panel.SelectConversationAsync(originalConversation); host.UpdateLayout(); await Idle();
            Check(!surface.IsVisible && book.Active.Bookmarks.Count == 2 && book.Active.BookmarkSort == "alphabetical" &&
                Math.Abs(Canvas.GetTop(ball) - (overlay.ActualHeight - ball.ActualHeight)) < 1, "Returning to a conversation lost its menu state or ball position");
            await panel.SaveAsync(); host.Content = null; panel.Dispose();
            panel = new AiPanel(new AiConnection(new AiFakeHandler())); host.Content = panel; host.UpdateLayout(); await panel.SetProjectAsync(project); await Idle();
            book = (AiNotebook)Field(panel, "_book")!;
            Check(book.Active.Id == originalConversation && book.Active.Bookmarks.Count == 2 && book.Active.BookmarkSort == "alphabetical" && book.Draft == "用户草稿保留", "Reload lost session bookmarks, order, or draft");
            var reloadedBall = (Button)Field(panel, "_bookmarkBall")!; var reloadedOverlay = (Canvas)Field(panel, "_bookmarkOverlay")!;
            Check(Math.Abs(Canvas.GetTop(reloadedBall) - (reloadedOverlay.ActualHeight - reloadedBall.ActualHeight)) < 1, "Reload lost the bookmark ball position");
            Console.WriteLine($"AI bookmark UI: {checks} checks passed ({(physical ? "physical drag and Ctrl+G, " : "")}groups, completion, navigation, labels, sort, isolation and reload).");
        }
        finally
        {
            if (mouseDown) mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Mouse.Capture(null); SetCursorPos(originalPointer.X, originalPointer.Y);
            panel.Dispose(); host.Close();
        }
    }
}
