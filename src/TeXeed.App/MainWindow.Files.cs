using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private readonly Dictionary<string, bool> _fileTreeExpanded = new(StringComparer.OrdinalIgnoreCase);
    private string? _fileTreeProject;
    private bool _fileTreeFiltering, _buildingFileTree;
    private ProjectFile[] _fileTreeFiles = [];
    private string? _fileTreeQuery, _fileTreeCaption;
    private int _fileTreeMatchingCount = -1;

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetSidebarTreeExpanded(true);
    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetSidebarTreeExpanded(false);
    private void SetSidebarTreeExpanded(bool expanded)
    {
        if (_sidebar == "outline") OutlineTree.SetAllExpanded(expanded);
        else if (_sidebar == "files")
            foreach (var item in FileTreeNodes(FileTree.Items).Where(item => item.Tag is null)) item.IsExpanded = expanded;
    }
    private static IEnumerable<TreeViewItem> FileTreeNodes(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            yield return item;
            foreach (var child in FileTreeNodes(item.Items)) yield return child;
        }
    }
    private void FileSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && !_buildingFileTree && _project is not null && _sidebar == "files") BuildFileTree(CurrentFiles());
    }
    private void FileSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FileSearchBox.Text.Length != 0) { FileSearchBox.Clear(); e.Handled = true; }
    }
    private void ClearFileSearch_Click(object sender, RoutedEventArgs e) { FileSearchBox.Clear(); FileSearchBox.Focus(); }
    private void FileTree_LeftUp(object sender, MouseButtonEventArgs e)
    {
        // A retained selected row must still open its file after another editor tab
        // became active: SelectedItemChanged alone does not fire for that row again.
        for (var target = e.OriginalSource as DependencyObject; target is not null && target != FileTree;
            target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target))
            if (target is TreeViewItem item)
            {
                if (item.Tag is string path && !string.Equals(path, Editor.CurrentFilePath, StringComparison.OrdinalIgnoreCase)) ActivateFile(path);
                break;
            }
    }
    private void UpdateFileTreeEmptyState()
    {
        EmptyIndex.Text = "没有匹配的文件";
        EmptyIndex.Visibility = _fileTreeMatchingCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BuildFileTree(IEnumerable<ProjectFile> files)
    {
        if (_project is null || _buildingFileTree) return;
        _buildingFileTree = true;
        try
        {
            var catalog = files.ToArray();
            var changedProject = !string.Equals(_fileTreeProject, _project.MainPath, StringComparison.OrdinalIgnoreCase);
            if (!changedProject && _fileTreeQuery == FileSearchBox.Text.Trim() && _fileTreeCaption == _project.Name
                && _fileTreeFiles.Length == catalog.Length && _fileTreeFiles.Zip(catalog).All(pair =>
                    pair.First.Path == pair.Second.Path && pair.First.RelativePath == pair.Second.RelativePath && pair.First.Kind == pair.Second.Kind))
            { UpdateFileTreeEmptyState(); return; }
            if (changedProject)
            {
                _fileTreeProject = _project.MainPath; _fileTreeExpanded.Clear(); _fileTreeFiltering = false;
                FileSearchBox.Clear();
            }
            else if (!_fileTreeFiltering)
                foreach (var item in FileTreeNodes(FileTree.Items).Where(item => item.Tag is null)) _fileTreeExpanded[item.Uid] = item.IsExpanded;
            var query = FileSearchBox.Text.Trim(); _fileTreeFiltering = query.Length != 0;
            var matches = catalog.Where(file => !_fileTreeFiltering || file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            _fileTreeFiles = catalog; _fileTreeQuery = query; _fileTreeCaption = _project.Name; _fileTreeMatchingCount = matches.Length;
            FileTree.Items.Clear();
            UpdateFileTreeEmptyState();
            if (matches.Length == 0) return;
            TreeViewItem Folder(string title, string id, bool root = false) => new()
            {
                Header = title, Uid = id, IsExpanded = _fileTreeFiltering || _fileTreeExpanded.GetValueOrDefault(id, true),
                FontWeight = root ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 4, 0, 0)
            };
            var root = Folder(_project.Name, "", true);
            foreach (var file in matches)
            {
                var segments = file.RelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries); var parent = root;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    var id = string.Join('/', segments.Take(i + 1));
                    var child = parent.Items.OfType<TreeViewItem>().FirstOrDefault(item => item.Tag is null && item.Uid == id);
                    if (child is null) { child = Folder(segments[i], id); parent.Items.Add(child); }
                    parent = child;
                }
                var item = new TreeViewItem
                {
                    Header = file.Name + (file.Path.Equals(_project.MainPath, StringComparison.OrdinalIgnoreCase) ? "  · 主文件" : ""),
                    Tag = file.Path, ToolTip = file.RelativePath, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 4, 0, 0)
                };
                item.SetResourceReference(Control.ForegroundProperty, file.IsEditable ? "Ink" : "Muted"); parent.Items.Add(item);
            }
            FileTree.Items.Add(root);
        }
        finally { _buildingFileTree = false; }
    }
}
