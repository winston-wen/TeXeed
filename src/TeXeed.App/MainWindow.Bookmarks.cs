using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private ProjectIndex? _bookmarkIndex;
    private string? _bookmarkProject;
    private bool _updatingBookmarks;
    private void RefreshBookmarks(ProjectIndex index)
    {
        if (!string.Equals(_bookmarkProject, _project!.MainPath, StringComparison.OrdinalIgnoreCase))
        {
            _updatingBookmarks = true; BookmarkSearchBox.Clear(); BookmarkTree.SetFilter(""); _updatingBookmarks = false;
            _bookmarkProject = _project.MainPath;
        }
        if (!ReferenceEquals(index, _bookmarkIndex))
        {
            BookmarkTree.Update(index.Bookmarks, _project.MainPath); _bookmarkIndex = index;
        }
        BookmarkTree.MarkCaret(Editor.CurrentLocation); BookmarkTree.HighlightViewport(Editor.VisibleSourceRange);
        BookmarkTree.SetFilter(BookmarkSearchBox.Text);
    }
    private void UpdateBookmarkEmptyState()
    {
        if (_sidebar != "bookmarks") return;
        EmptyIndex.Text = string.IsNullOrWhiteSpace(BookmarkSearchBox.Text) ? "文档中暂无书签" : "没有匹配的书签";
        EmptyIndex.Visibility = BookmarkTree.FilteredItemCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BookmarkSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _updatingBookmarks || _project is null) return;
        BookmarkTree.SetFilter(BookmarkSearchBox.Text); UpdateBookmarkEmptyState();
    }
    private void BookmarkSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && BookmarkSearchBox.Text.Length != 0) { BookmarkSearchBox.Clear(); e.Handled = true; }
    }
    private void ClearBookmarkSearch_Click(object sender, RoutedEventArgs e) { BookmarkSearchBox.Clear(); BookmarkSearchBox.Focus(); }
}
