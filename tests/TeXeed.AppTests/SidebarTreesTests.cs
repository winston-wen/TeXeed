using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckSidebarTreesAsync(MainWindow window, string output)
    {
        var folder = Path.Combine(output, "file-search-project"); Directory.CreateDirectory(Path.Combine(folder, "chapters"));
        var main = Path.Combine(folder, "main.tex");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\n\\section{Root}\n\\subsection{Child}\n\\subsubsection{Grandchild}\n\\input{Alpha}\n\\input{chapters/AlphaNotes}\n\\input{chapters/第三节}\n\\end{document}\n");
        foreach (var file in new[] { "Alpha.tex", "chapters/AlphaNotes.tex", "chapters/第三节.tex" }) File.WriteAllText(Path.Combine(folder, file), "Body\n");
        await CallAsync(window, "OpenAsync", main); window.UpdateLayout();
        var editor = (LatexEditor)window.FindName("Editor");
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        var expand = (Button)window.FindName("ExpandAllButton"); var collapse = (Button)window.FindName("CollapseAllButton");
        var icons = ((Panel)window.FindName("WorkspaceIconBar")).Children.OfType<Button>().ToArray();
        void Mode(string mode)
        {
            if ((string)Field(window, "_sidebar")! != mode || !(bool)Field(window, "_workspaceExpanded")!)
                icons.Single(button => Equals(button.Tag, mode)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
        }
        IEnumerable<OutlineNode> Nodes(IEnumerable<OutlineNode> roots) => roots.SelectMany(root => new[] { root }.Concat(Nodes(root.Children)));
        var tree = (TreeView)outline.FindName("Tree"); Mode("outline");
        var caret = editor.CaretOffset;
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Nodes(tree.Items.Cast<OutlineNode>()).Any(node => node.IsExpanded)) throw new Exception("Collapse-all left outline descendants expanded");
        expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Nodes(tree.Items.Cast<OutlineNode>()).Any(node => !node.IsExpanded) || editor.CaretOffset != caret)
            throw new Exception("Expand-all missed outline descendants or navigated the editor");
        Capture(window, Path.Combine(output, "outline-bulk-buttons.png"));
        Mode("files");
        var files = (TreeView)window.FindName("FileTree"); var search = (TextBox)window.FindName("FileSearchBox");
        IEnumerable<TreeViewItem> Items(ItemCollection items) => items.OfType<TreeViewItem>().SelectMany(item => new[] { item }.Concat(Items(item.Items)));
        string[] Paths() => Items(files.Items).Select(item => item.Tag).OfType<string>().Select(Path.GetFileName).OrderBy(name => name).ToArray()!;
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Items(files.Items).Where(item => item.Tag is null).Any(item => item.IsExpanded)) throw new Exception("Collapse-all missed file directories");
        search.Text = "aLpHa"; window.UpdateLayout();
        if (!Paths().SequenceEqual(new[] { "Alpha.tex", "AlphaNotes.tex" }) || Items(files.Items).Where(item => item.Tag is null).Any(item => !item.IsExpanded))
            throw new Exception("File-name filter lost case-insensitive matches or their directory context");
        var selected = Items(files.Items).Single(item => item.Tag is string path && path.EndsWith("AlphaNotes.tex")); selected.IsSelected = true;
        if (editor.CurrentFilePath != Path.Combine(folder, "chapters", "AlphaNotes.tex")) throw new Exception("Filtered file does not open its source");
        var retainedRoot = files.Items[0]; Mode("outline"); Mode("files");
        if (!ReferenceEquals(retainedRoot, files.Items[0])) throw new Exception("Unchanged file tree was rebuilt on sidebar switching");
        Invoke(window, "ActivateFile", main);
        selected.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        if (editor.CurrentFilePath != Path.Combine(folder, "chapters", "AlphaNotes.tex")) throw new Exception("Retained selected file cannot be reopened after another tab became active");
        search.Text = "第三"; window.UpdateLayout();
        if (!Paths().SequenceEqual(new[] { "第三节.tex" })) throw new Exception("Chinese file-name filter failed");
        Capture(window, Path.Combine(output, "project-files-search.png"));
        search.Text = "chapters";
        if (files.Items.Count != 0 || ((TextBlock)window.FindName("EmptyIndex")).Visibility != Visibility.Visible) throw new Exception("File-name search matched only a directory or lacks its empty state");
        var key = CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent); search.RaiseEvent(key);
        if (!key.Handled || search.Text.Length != 0 || Paths().Length != 4 || Items(files.Items).Where(item => item.Tag is null).Any(item => item.IsExpanded))
            throw new Exception("Clearing filter failed to restore all files and original fold state");
        expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Items(files.Items).Where(item => item.Tag is null).Any(item => !item.IsExpanded)) throw new Exception("Expand-all missed file directories");
        search.Text = "Alpha"; Mode("search"); ((TextBox)window.FindName("SearchBox")).Text = "Body"; Mode("files");
        if (search.Text != "Alpha" || Paths().Length != 2) throw new Exception("File-name and full-text searches share state");
        search.Clear(); Mode("outline");
        window.Width = 820; window.UpdateLayout();
        if (!expand.IsVisible || !collapse.IsVisible || expand.ActualWidth < 25 || collapse.ActualWidth < 25)
            throw new Exception("Bulk action icons are unavailable in a narrow workspace");
        Capture(window, Path.Combine(output, "outline-bulk-narrow.png"));
        Console.WriteLine("PASS: outline/file-tree expand and collapse all, nested state, filename case/Chinese filtering, context and opening, empty state, Esc restore, isolated queries and narrow header buttons");
    }
}
