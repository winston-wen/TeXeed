using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private readonly Dictionary<string, bool> _fileTreeExpanded = new(StringComparer.OrdinalIgnoreCase);
    private string? _fileTreeProject;
    private bool _fileTreeFiltering, _buildingFileTree;

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
    private void BuildFileTree(IEnumerable<ProjectFile> files)
    {
        if (_project is null || _buildingFileTree) return;
        _buildingFileTree = true;
        try
        {
            if (!string.Equals(_fileTreeProject, _project.MainPath, StringComparison.OrdinalIgnoreCase))
            {
                _fileTreeProject = _project.MainPath; _fileTreeExpanded.Clear(); _fileTreeFiltering = false;
                FileSearchBox.Clear();
            }
            else if (!_fileTreeFiltering)
                foreach (var item in FileTreeNodes(FileTree.Items).Where(item => item.Tag is null)) _fileTreeExpanded[item.Uid] = item.IsExpanded;
            var query = FileSearchBox.Text.Trim(); _fileTreeFiltering = query.Length != 0;
            var matches = files.Where(file => !_fileTreeFiltering || file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            FileTree.Items.Clear();
            EmptyIndex.Text = "没有匹配的文件";
            EmptyIndex.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
