using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckFilePriorityAsync(MainWindow window, string output)
    {
        var checks = 0; void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var root = Path.Combine(output, "priority-project"); Directory.CreateDirectory(Path.Combine(root, ".texeed", "jobs"));
        var main = Path.Combine(root, "main.tex"); var note = Path.Combine(root, "notes.md"); var extra = Path.Combine(root, "extra.tex");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\nTest\n\\end{document}\n"); File.WriteAllText(note, "# 笔记\n"); File.WriteAllText(extra, "额外源码\n");
        await CallAsync(window, "OpenAsync", main);
        ((Panel)window.FindName("WorkspaceIconBar")).Children.OfType<Button>().Single(button => Equals(button.Tag, "files")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await EnsureDirectoryTreeAsync(window);
        var tree = (TreeView)window.FindName("FileTree"); var priority = (StackPanel)window.FindName("PriorityList");
        TreeViewItem Row(string path) => tree.Items.OfType<TreeViewItem>().Single(row => Equals(row.Tag, path));
        async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); await Task.Delay(40); window.UpdateLayout(); }
        var originalRow = Row(main);
        Check(Row(note).ContextMenu.Items.Count == 2 && (string)((MenuItem)Row(note).ContextMenu.Items[0]).Header == "重命名", "File menu contains extra commands or lacks rename");
        await CallAsync(window, "SetPriorityFilesAsync", new[] { note }, true); await Settle();
        Check(priority.Children.OfType<Button>().Single().Tag is string path && path == note, "Context command did not add the file to the priority section");
        var data = new DataObject("TeXeed.ProjectFile", extra);
        var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Link, (DependencyObject)window.FindName("PrioritySection"), new Point(5, 5) }, null)!;
        drop.RoutedEvent = DragDrop.DropEvent;
        ((UIElement)window.FindName("PrioritySection")).RaiseEvent(drop);
        for (var i = 0; i < 100 && priority.Children.Count < 2; i++) await Task.Delay(20);
        Check(drop.Handled && priority.Children.OfType<Button>().Select(button => (string)button.Tag).SequenceEqual(new[] { note, extra }), "Dropping a file did not add it without moving or opening it");
        Check(File.Exists(extra) && File.ReadAllText(extra) == "额外源码\n", "Priority drop moved or modified the file");
        Check(ProjectExplorerSettings.Load(root).SequenceEqual(new[] { note, extra }), "Priority section did not persist ordered relative paths");
        await CallAsync(window, "SetPriorityFilesAsync", new[] { note }, true);
        Check(priority.Children.Count == 2, "Repeated priority action duplicated the file");
        var pinnedMenu = priority.Children.OfType<Button>().First().ContextMenu;
        pinnedMenu.IsOpen = true; await Settle();
        Check((string)((MenuItem)pinnedMenu.Items[1]).Header == "取消优先显示" && pinnedMenu.Items.Count == 2, "Pinned file menu does not offer the two expected actions"); pinnedMenu.IsOpen = false;
        await CallAsync(window, "SetPriorityFilesAsync", new[] { extra }, false);
        Check(ProjectExplorerSettings.Load(root).SequenceEqual(new[] { note }) && File.Exists(extra), "Cancel priority removed the actual file or failed to persist");
        var editor = (LatexEditor)window.FindName("Editor");
        Invoke(window, "OpenExplorerFile", note);
        for (var i = 0; i < 100 && editor.CurrentFilePath != note; i++) await Task.Delay(20);
        editor.Document.Insert(editor.Document.TextLength, "未保存内容\n"); var document = editor.Document; var caret = editor.CaretOffset;
        var renamed = Path.Combine(root, "renamed_note.md");
        await CallAsync(window, "RenameExplorerFileAsync", note, "renamed_note.md"); await Settle();
        Check(File.Exists(renamed) && !File.Exists(note) && File.ReadAllText(renamed) == "# 笔记\n", "Rename changed bytes, lost a file or failed to move its name");
        Check(ReferenceEquals(editor.Document, document) && editor.CurrentFilePath == renamed && editor.CaretOffset == caret && editor.Document.UndoStack.CanUndo && editor.Text.Contains("未保存内容"), "Rename lost editor buffer/undo/caret state");
        Check(((Dictionary<string, string>)Field(window, "_changes")!).ContainsKey(renamed) && !((Dictionary<string, string>)Field(window, "_changes")!).ContainsKey(note), "Rename orphaned unsaved edits");
        Check(ProjectExplorerSettings.Load(root).SequenceEqual(new[] { renamed }) && ((TabWorkspace)Field(window, "_workspaceTabs")!).Owner(renamed) is not null, "Rename lost priority or open-tab identity");
        await CallAsync(window, "RenameExplorerFileAsync", renamed, "main.tex");
        Check(File.Exists(renamed) && File.ReadAllText(main).Contains("documentclass"), "Rename overwrote an existing file");
        await CallAsync(window, "RenameExplorerFileAsync", renamed, "../escape.md"); Check(File.Exists(renamed), "Invalid rename escaped the directory");
        editor.Undo(); await CallAsync(window, "SaveAsync");
        var store = (AiWorkspaceStore)Field(window, "_aiStore")!; ((TextBox)((AiPanel)Field(window, "_aiPanel")!).FindName("Composer")).Text = "主文件改名也要保留会话"; await store.SaveAsync();
        await CallAsync(window, "RenameExplorerFileAsync", main, "renamed_main.tex");
        var nextMain = Path.Combine(root, "renamed_main.tex");
        Check(((TexProject)Field(window, "_project")!).MainPath == nextMain && File.Exists(nextMain) && !File.Exists(main), "Main-file rename did not update the project entry");
        Check(store.Project == nextMain && AiStorage.Load(nextMain).Active.Draft == "主文件改名也要保留会话", "Main-file rename lost or detached conversation state");
        Check(((AppSettings)Field(window, "_settings")!).RecentProjects.Contains(nextMain), "Main-file rename did not update recent projects");

        await Task.Delay(900); await EnsureDirectoryTreeAsync(window);
        var scans = (int)Field(window, "_directoryScanCount")!; var applies = (int)Field(window, "_fileTreeApplyCount")!;
        var retained = Row(nextMain); var gaps = new List<double>(); var clock = Stopwatch.StartNew(); var last = clock.Elapsed.TotalMilliseconds;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; gaps.Add(now - last); last = now; }; timer.Start();
        await Task.Run(() => { for (var i = 0; i < 800; i++) File.WriteAllText(Path.Combine(root, ".texeed", "jobs", $"burst-{i:0000}.svg"), "<svg/>"); });
        await Task.Delay(750); timer.Stop();
        Check((int)Field(window, "_directoryScanCount")! == scans && (int)Field(window, "_fileTreeApplyCount")! == applies && ReferenceEquals(retained, Row(nextMain)), "Hidden build/cache activity rescanned or rebuilt the visible file tree");
        Check(gaps.Count > 10 && gaps.Max() < 200, "Bulk file generation blocked UI input frames: " + (gaps.Count == 0 ? 0 : gaps.Max()));
        var cacheFolder = tree.Items.OfType<TreeViewItem>().Single(row => row.Uid == ".texeed"); cacheFolder.IsExpanded = true;
        for (var i = 0; i < 100 && ((DirectoryEntry[])Field(window, "_directoryEntries")!).Count(entry => entry.Path.Contains("burst-")) < 800; i++) await Task.Delay(30);
        Check(((DirectoryEntry[])Field(window, "_directoryEntries")!).Count(entry => entry.Path.Contains("burst-")) == 800, "Opening a dirty folder did not discover deferred files");
        Check(ReferenceEquals(retained, Row(nextMain)), "Visible directory refresh replaced unchanged rows"); cacheFolder.IsExpanded = false;
        foreach (var theme in new[] { "ristretto", "github-light-default" }) { ThemeManager.Apply(theme); await Settle(); Capture(window, Path.Combine(output, "priority-" + theme + ".png")); }
        await CheckMiddleClickTabsAsync(window, renamed, output);
        await CallAsync(window, "SaveAsync", false, false);
        Check(((Dictionary<string, string>)Field(window, "_changes")!).Count == 0, "Test fixture still has unsaved edits before reopening");
        await CallAsync(window, "OpenAsync", nextMain); await EnsureDirectoryTreeAsync(window);
        Check(priority.Children.OfType<Button>().Select(button => (string)button.Tag).SequenceEqual(new[] { renamed }), "Reopening the renamed project lost its priority section");
        Console.WriteLine($"PASS: priority/rename/performance {checks} checks; hidden 800-file burst: no scan or tree rebuild, max input frame gap {gaps.Max():F1}ms. Menus, drop, persistence, buffers and main-entry/history rename verified.");
    }

    private static async Task CheckMiddleClickTabsAsync(MainWindow window, string source, string output)
    {
        void Middle(EditorTabStrip strip, string path)
        {
            window.UpdateLayout();
            var view = ((StackPanel)strip.FindName("Items")).Children.Cast<Border>().Single(border => Equals(border.Tag, path));
            var label = (TextBlock)((Button)((Grid)view.Child).Children[0]).Content;
            label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        }
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        Invoke(window, "ActivateFile", source); var editor = (LatexEditor)window.FindName("Editor"); editor.Document.Insert(editor.Document.TextLength, "中键关闭前的未保存内容"); var document = editor.Document;
        var side = tabs.Owner(source)!.Value; Middle((EditorTabStrip)window.FindName(side == TabSide.Left ? "FileTabs" : "RightFileTabs"), source);
        if (tabs.Owner(source) is not null) throw new Exception("Middle-click did not close the source title");
        Invoke(window, "ActivateFile", source); if (!ReferenceEquals(editor.Document, document) || !editor.Text.Contains("中键关闭前")) throw new Exception("Middle-click discarded a dirty source buffer"); editor.Undo();
        var previewSide = tabs.Owner(TabWorkspace.PreviewKey)!.Value; Middle((EditorTabStrip)window.FindName(previewSide == TabSide.Left ? "FileTabs" : "RightFileTabs"), TabWorkspace.PreviewKey);
        if (tabs.Owner(TabWorkspace.PreviewKey) is null) throw new Exception("Middle-click closed the protected preview");
        await CallAsync(window, "OpenNewAiConversationAsync"); var ai = (AiPanel)Field(window, "_aiPanel")!; var id = ai.ConversationId;
        ((TextBox)ai.FindName("Composer")).Text = "中键关闭仍保留这份输入";
        var key = TabWorkspace.AiConversationKey(id); side = tabs.Owner(key)!.Value;
        Middle((EditorTabStrip)window.FindName(side == TabSide.Left ? "FileTabs" : "RightFileTabs"), key);
        for (var i = 0; i < 100 && ((HashSet<string>)Field(window, "_closingAi")!).Contains(id); i++) await Task.Delay(20);
        if (tabs.Owner(key) is not null) throw new Exception("Middle-click did not close the AI title");
        await CallAsync(window, "OpenAiConversationAsync", id);
        if (ReferenceEquals(ai, Field(window, "_aiPanel"))) throw new Exception("AI close/reopen reused the closing or disposed view");
        if (((TextBox)((AiPanel)Field(window, "_aiPanel")!).FindName("Composer")).Text != "中键关闭仍保留这份输入") throw new Exception("Middle-click lost AI conversation input");
        side = tabs.Owner(key)!.Value;
        Middle((EditorTabStrip)window.FindName(side == TabSide.Left ? "FileTabs" : "RightFileTabs"), key);
        Console.WriteLine("PASS: middle-click on source and AI titles; dirty buffers, AI input and protected preview preserved.");
    }
}
