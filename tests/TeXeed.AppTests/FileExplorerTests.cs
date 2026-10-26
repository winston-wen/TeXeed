using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task EnsureDirectoryTreeAsync(MainWindow window)
    {
        Invoke(window, "BuildFileTree", CurrentProjectFiles(window));
        if (Field(window, "_fileCatalogTask") is Task catalog) await catalog;
        Invoke(window, "BuildFileTree", CurrentProjectFiles(window));
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private static async Task CheckFileExplorerAsync(MainWindow window, string output)
    {
        var count = 0;
        void Check(bool ok, string message) { count++; if (!ok) throw new Exception(message); }
        var root = Path.Combine(output, "很长的项目目录_用来验证省略号与鼠标悬浮轮播_完整目录名"); Directory.CreateDirectory(root);
        foreach (var directory in new[] { "chapters", "empty", "pdf", ".vscode", "_build", "2026-资产" }) Directory.CreateDirectory(Path.Combine(root, directory));
        var samples = new Dictionary<string, string>
        {
            ["main.tex"] = "\\documentclass{article}\n\\begin{document}\n\\section{测试}\nBody\n\\end{document}\n",
            ["unreferenced.tex"] = "未引用的源码\n", ["chapters/section.tex"] = "章节\n", ["notes.md"] = "# 笔记\n正文\n",
            [".vscode/settings.json"] = "{}", [".gitignore"] = "_build/", ["_build/main.log"] = "generated",
            ["2026-资产/image.png"] = "not decoded by the explorer", ["pdf/main.pdf"] = "%PDF-1.7", ["references.bib"] = "@book{x,title={Test}}"
        };
        foreach (var (relative, text) in samples) File.WriteAllText(Path.Combine(root, relative), text);
        await CallAsync(window, "OpenAsync", Path.Combine(root, "main.tex"));
        var icons = ((Panel)window.FindName("WorkspaceIconBar")).Children.OfType<Button>().ToArray();
        void Mode(string name)
        {
            if ((string)Field(window, "_sidebar")! != name || !(bool)Field(window, "_workspaceExpanded")!) icons.Single(button => Equals(button.Tag, name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
        }
        IEnumerable<TreeViewItem> Nodes(ItemCollection items) => items.OfType<TreeViewItem>().SelectMany(item => new[] { item }.Concat(Nodes(item.Items)));
        Mode("files"); await EnsureDirectoryTreeAsync(window);
        var tree = (TreeView)window.FindName("FileTree"); var search = (TextBox)window.FindName("FileSearchBox");
        var title = (HoverMarquee)window.FindName("SidebarTitle");
        Check(title.Text == Path.GetFileName(root) && title.ToolTip.ToString()!.Contains(root), "File explorer heading is not the full root directory name");
        Check(tree.Items.OfType<TreeViewItem>().All(node => (string)node.Header != title.Text), "Explorer duplicated the root directory row");
        Check(tree.Items.OfType<TreeViewItem>().TakeWhile(node => node.Tag is null).Count() >= 6, "Directories are not grouped before files or empty/hidden folders are absent");
        Check(!Descendants<ToggleButton>(tree).Any(), "Native collapse chevrons remain in file rows");
        Check(ReferenceEquals(((Button)window.FindName("ExpandAllButton")).Content, window.FindResource("ExpandAllIcon")) && ReferenceEquals(((Button)window.FindName("CollapseAllButton")).Content, window.FindResource("CollapseAllIcon")), "File explorer bulk actions differ from the other sidebar tabs");
        var catalog = (DirectoryEntry[])Field(window, "_directoryEntries")!;
        Check(samples.Keys.All(path => catalog.Any(entry => entry.RelativePath.Replace('\\', '/') == path)), "Explorer omitted a non-TeX, hidden, generated or unreferenced file");
        var folder = tree.Items.OfType<TreeViewItem>().Single(node => (string)node.Header == "chapters");
        folder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }); window.UpdateLayout();
        Check(folder.IsExpanded && folder.Items.OfType<TreeViewItem>().Single().Tag is string path && path.EndsWith("section.tex"), "Folder click did not open/populate its children");
        Check(Descendants<ProjectFileIcon>(folder).First().Kind == "folder-open", "Expanded directory does not have an open-folder icon");
        folder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }); window.UpdateLayout();
        Check(!folder.IsExpanded && Descendants<ProjectFileIcon>(folder).First().Kind == "folder-closed", "Folder click did not restore its closed icon");
        var first = tree.Items[0]; Mode("outline"); Mode("files"); await EnsureDirectoryTreeAsync(window);
        Check(ReferenceEquals(first, tree.Items[0]), "Warm sidebar switch rebuilt the unchanged directory tree");
        title.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        await Task.Delay(650); Check(title.IsScrolling && title.Offset > 0, "Hovering a clipped directory name did not scroll it");
        title.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        Check(!title.IsScrolling && title.Offset == 0, "Directory title did not restore ellipsis on mouse leave");
        search.Text = "section.tex"; window.UpdateLayout();
        Check(Nodes(tree.Items).Any(node => node.Tag is string path && path.EndsWith("section.tex")) && Nodes(tree.Items).Where(node => node.Tag is null).All(node => node.IsExpanded), "Search did not include the ancestor folders of an unreferenced file");
        search.Text = "notes.md"; window.UpdateLayout();
        Nodes(tree.Items).Single(node => node.Tag is string path && path.EndsWith("notes.md")).IsSelected = true;
        var editor = (LatexEditor)window.FindName("Editor");
        for (var i = 0; i < 100 && editor.CurrentFilePath != Path.Combine(root, "notes.md"); i++) await Task.Delay(20);
        Check(editor.CurrentFilePath == Path.Combine(root, "notes.md") && editor.Text.Contains("笔记"), "An unreferenced text file did not open on demand");
        search.Clear();
        foreach (var theme in new[] { "ristretto", "github-light-default" })
        {
            ThemeManager.Apply(theme); ((Button)window.FindName("ExpandAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Capture(window, Path.Combine(output, "file-explorer-" + theme + ".png"));
            Check(Descendants<ProjectFileIcon>(tree).Select(icon => icon.Kind).Distinct().Count() >= 5, "File types do not have distinct icons");
        }
        var newFile = Path.Combine(root, "created-after-open.txt"); File.WriteAllText(newFile, "watcher");
        for (var i = 0; i < 100 && !Nodes(tree.Items).Any(node => Equals(node.Tag, newFile)); i++) await Task.Delay(30);
        Check(Nodes(tree.Items).Any(node => Equals(node.Tag, newFile)), "File-system create did not refresh the explorer");
        File.Delete(newFile);
        for (var i = 0; i < 100 && Nodes(tree.Items).Any(node => Equals(node.Tag, newFile)); i++) await Task.Delay(30);
        Check(!Nodes(tree.Items).Any(node => Equals(node.Tag, newFile)), "File-system delete left a stale row");
        Check(!((Dictionary<string, TreeViewItem>)Field(window, "_fileNodeCache")!).ContainsKey("created-after-open.txt"), "Deleted row leaked in the file-node cache");
        foreach (var mode in new[] { "outline", "files", "search", "bookmarks", "labels", "todos" })
        {
            Mode(mode);
            var previousWidth = ((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth;
            var previousSearch = ((TextBox)window.FindName("SearchBox")).Text;
            var previousFileSearch = search.Text;
            var previousTreeRow = tree.Items[0];
            var box = mode == "files" ? (UIElement)search : (TextBox)window.FindName("SearchBox");
            var onControl = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(onControl, 2); box.RaiseEvent(onControl);
            Check((bool)Field(window, "_workspaceExpanded")!, "Double-clicking a control collapsed the sidebar: " + mode);
            var blank = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(blank, 2); ((UIElement)window.FindName("SidebarPanel")).RaiseEvent(blank);
            Check((bool)Field(window, "_workspaceExpanded")! && !blank.Handled, "Content-area blank double-click still collapses sidebar mode: " + mode);
            var rail = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(rail, 2); ((UIElement)window.FindName("WorkspaceRail")).RaiseEvent(rail);
            Check(!(bool)Field(window, "_workspaceExpanded")! && rail.Handled, "Rail blank double-click did not collapse sidebar mode: " + mode);
            var restore = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(restore, 2); ((UIElement)window.FindName("WorkspaceRail")).RaiseEvent(restore); window.UpdateLayout();
            Check((bool)Field(window, "_workspaceExpanded")! && restore.Handled && (string)Field(window, "_sidebar")! == mode, "Rail blank double-click did not reopen the previous sidebar mode: " + mode);
            Check(Math.Abs(((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth - previousWidth) < 1
                && ((TextBox)window.FindName("SearchBox")).Text == previousSearch && search.Text == previousFileSearch
                && ReferenceEquals(tree.Items[0], previousTreeRow), "Reopening reset sidebar width, searches or directory rows: " + mode);
        }
        Mode("files"); ThemeManager.Apply("ristretto");
        Console.WriteLine($"PASS: file explorer {count} checks: all files, sorted lazy directories, type/open-folder icons, search, on-demand opening, live updates, heading marquee, both palettes and all sidebar blank-double-click modes.");
    }
}
