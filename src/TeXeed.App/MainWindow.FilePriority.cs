using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App.Controls;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class MainWindow
{
    private const string ExplorerDragFormat = "TeXeed.ProjectFile";
    private string[] _priorityFiles = [];
    private string? _priorityProject;
    private readonly SemaphoreSlim _priorityGate = new(1, 1);
    private Point _explorerDragStart;
    private string? _explorerDragPath;
    private bool _suppressExplorerClick;

    private ContextMenu ExplorerFileMenu(string path)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => ShowExplorerRename(path);
        var priority = new MenuItem();
        priority.Click += async (_, _) => await SetPriorityFilesAsync([path], !_priorityFiles.Contains(path, StringComparer.OrdinalIgnoreCase));
        menu.Items.Add(rename); menu.Items.Add(priority);
        menu.Opened += (_, _) =>
        {
            rename.IsEnabled = priority.IsEnabled = File.Exists(path) && !_transitioning && !_closing;
            priority.Header = _priorityFiles.Contains(path, StringComparer.OrdinalIgnoreCase) ? "取消优先显示" : "优先显示";
        };
        return menu;
    }
    private void RefreshPriorityFiles()
    {
        if (_project is null) return;
        PriorityList.Children.Clear();
        foreach (var path in _priorityFiles.Where(File.Exists))
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new ProjectFileIcon { FilePath = path, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); grid.Children.Add(label);
            var button = new Button { Content = grid, Tag = path, Height = 28, Margin = new Thickness(0), Padding = new Thickness(4, 0, 4, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = Path.GetRelativePath(_project.Root, path), Style = (Style)FindResource("PriorityFileButton"), ContextMenu = ExplorerFileMenu(path) };
            System.Windows.Automation.AutomationProperties.SetName(button, "优先显示：" + Path.GetFileName(path));
            button.Click += (_, _) => OpenExplorerFile(path); PriorityList.Children.Add(button);
        }
        PriorityEmpty.Visibility = PriorityList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task SetPriorityFilesAsync(IEnumerable<string> paths, bool add)
    {
        if (_project is null || _transitioning || _closing) return;
        var root = _project.Root; var session = _session;
        await _priorityGate.WaitAsync();
        try
        {
            if (session != _session || _closing || _transitioning) return;
            var eligible = paths.Where(path => File.Exists(path) && ProjectExplorerSettings.Resolve(root, Path.GetRelativePath(root, path)) is not null).ToArray();
            var next = add ? _priorityFiles.Concat(eligible).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : _priorityFiles.Except(paths, StringComparer.OrdinalIgnoreCase).ToArray();
            await Task.Run(() => ProjectExplorerSettings.Save(root, next));
            if (session != _session) return;
            _priorityFiles = next; _priorityProject = root; RefreshPriorityFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("优先显示暂未保存：" + ex.Message); }
        finally { _priorityGate.Release(); }
    }
    private string[] PriorityDropPaths(IDataObject data)
    {
        if (_project is null) return [];
        var paths = data.GetDataPresent(ExplorerDragFormat) ? new[] { (string)data.GetData(ExplorerDragFormat) }
            : data.GetData(DataFormats.FileDrop) as string[] ?? [];
        return paths.Where(path => File.Exists(path) && ProjectExplorerSettings.Resolve(_project.Root, Path.GetRelativePath(_project.Root, path)) is not null).ToArray();
    }
    private void Priority_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = PriorityDropPaths(e.Data).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Priority_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true; await SetPriorityFilesAsync(PriorityDropPaths(e.Data), true);
    }
    private void FileTree_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _explorerDragPath is null) return;
        var position = e.GetPosition(FileTree);
        if (Math.Abs(position.X - _explorerDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _explorerDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var path = _explorerDragPath; _explorerDragPath = null; _suppressExplorerClick = true;
        DragDrop.DoDragDrop(FileTree, new DataObject(ExplorerDragFormat, path), DragDropEffects.Link); e.Handled = true;
    }
}
