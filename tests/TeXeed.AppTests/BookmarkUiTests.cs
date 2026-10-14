using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckBookmarksAsync(MainWindow window, string output)
    {
        var folder = Path.Combine(output, "bookmark-project"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex"); var chapter = Path.Combine(folder, "chapter.tex");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\n\\pdfbookmark[0]{Algebra}{bm:algebra}\n\\section{Real heading}\n\\label{sec:real}\n\\input{chapter}\n\\end{document}\n");
        File.WriteAllText(chapter, "\\pdfbookmark[1]{Groups}{bm:groups}\n\\pdfbookmark[2]{Cyclic $x^2$}{bm:cyclic}\n\\bookmark[level=0,dest=other]{其他章节}\n");
        await CallAsync(window, "OpenAsync", main);
        var editor = (LatexEditor)window.FindName("Editor");
        var outline = (DocumentOutline)window.FindName("BookmarkTree"); var tree = (TreeView)outline.FindName("Tree");
        var search = (TextBox)window.FindName("BookmarkSearchBox");
        var icons = ((Panel)window.FindName("WorkspaceIconBar")).Children.OfType<Button>().ToArray();
        async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        async Task Mode(string mode)
        {
            if ((string)Field(window, "_sidebar")! != mode || !(bool)Field(window, "_workspaceExpanded")!)
                icons.Single(icon => Equals(icon.Tag, mode)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
        }
        if (icons.Length != 7 || !Equals(icons.Single(icon => Equals(icon.Tag, "bookmarks")).Content, window.FindResource("WorkspaceBookmarksIcon"))
            || !Equals(icons.Single(icon => Equals(icon.Tag, "labels")).Content, window.FindResource("WorkspaceLabelsIcon")))
            throw new Exception("Bookmark/tag and reference/anchor actions are not independent");
        await Mode("bookmarks");
        var roots = tree.Items.Cast<OutlineNode>().ToArray();
        if (roots.Length != 2 || roots[0].Children[0].Title != "Groups" || roots[0].Children[0].Children[0].Title != "Cyclic x^2")
            throw new Exception("Bookmark hierarchy did not span the main and child files");
        var first = roots[0]; var group = first.Children[0]; var cyclic = group.Children[0];
        ((Button)window.FindName("CollapseAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (first.IsExpanded || group.IsExpanded) throw new Exception("Bookmark collapse-all missed nested levels");
        ((Button)window.FindName("ExpandAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!first.IsExpanded || !group.IsExpanded) throw new Exception("Bookmark expand-all missed nested levels");
        group.IsExpanded = false; await Settle();
        search.Text = "cYcLiC"; await Settle();
        if (tree.Items.Count != 1 || !first.IsExpanded || !group.IsExpanded || outline.FilteredItemCount != 3)
            throw new Exception("Bookmark title search lost ancestor context or case-insensitive matching");
        Capture(window, Path.Combine(output, "bookmarks-filtered.png"));
        search.Clear(); await Settle();
        if (group.IsExpanded || tree.Items.Count != 2) throw new Exception("Clearing bookmark search lost original folds");
        search.Text = "bm:cyclic"; await Settle();
        if (outline.FilteredItemCount != 3) throw new Exception("Bookmark destination search failed");
        search.Text = "not-found"; await Settle();
        if (tree.Items.Count != 0 || ((TextBlock)window.FindName("EmptyIndex")).Visibility != Visibility.Visible)
            throw new Exception("Bookmark search has no empty state");
        search.RaiseEvent(CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent)); await Settle();
        if (search.Text.Length != 0 || group.IsExpanded) throw new Exception("Escape did not clear bookmark search and restore folds");
        search.Text = "cyclic"; await Mode("labels");
        var labels = ((ListBox)window.FindName("IndexList")).Items.Cast<IndexEntry>().ToArray();
        if (labels.Length != 1 || labels[0].Title != "sec:real") throw new Exception("PDF bookmark destinations leaked into reference labels");
        ((TextBox)window.FindName("SearchBox")).Text = "sec:";
        await Mode("bookmarks");
        if (search.Text != "cyclic") throw new Exception("Sidebar switching discarded the independent bookmark query");
        var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(first);
        var groupItem = (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromItem(group);
        var target = (TreeViewItem)groupItem.ItemContainerGenerator.ContainerFromItem(cyclic);
        DoubleClickHeading(window, (Button)target.Template.FindName("HeadingButton", target)); await Settle();
        if (editor.CurrentFilePath != chapter || editor.CurrentLocation?.Line != 2) throw new Exception("Bookmark double click failed to reach its source");
        editor.Document.Insert(0, "% inserted above bookmarks\n\n"); Invoke(window, "RefreshIndex"); await Settle();
        if (!ReferenceEquals(cyclic, group.Children[0]) || cyclic.Entry.Line != 4 || search.Text != "cyclic")
            throw new Exception("Live bookmark refresh lost node identity, query or updated source lines");
        editor.Undo(); Invoke(window, "RefreshIndex"); await Settle();
        if (cyclic.Entry.Line != 2) throw new Exception("Bookmark undo retained stale source positions");
        search.Text = "其他"; await Settle();
        if (tree.Items.Count != 1 || ((OutlineNode)tree.Items[0]).Title != "其他章节") throw new Exception("Chinese bookmark title filtering failed");
        search.Clear(); ((Button)window.FindName("ExpandAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        Capture(window, Path.Combine(output, "bookmarks-dark.png"));
        ThemeManager.Apply("github-light-default"); await Settle();
        Capture(window, Path.Combine(output, "bookmarks-light.png"));
        ThemeManager.Apply("ristretto");
        Console.WriteLine("PASS: six independent sidebar actions, cross-file bookmark hierarchy, bulk expansion/folds, title/id/Chinese search, ancestor context, fold/query restoration, empty state, label separation, source navigation and dark/light themes");
    }
}
