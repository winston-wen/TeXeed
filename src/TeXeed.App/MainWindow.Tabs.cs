using System.Windows;
using TeXeed.App.Controls;

namespace TeXeed.App;

public partial class MainWindow
{
    private void InitializeTabs()
    {
        FileTabs.Selected += path => { ActivateFile(path); Editor.TextArea.Focus(); };
        FileTabs.CloseRequested += CloseTabs;
        FileTabs.ReorderRequested += MoveTab;
    }
    private void CloseTabs(string path, TabCloseScope scope)
    {
        if (_transitioning || _closing || _allowClose) return;
        var index = _tabs.FindIndex(tab => string.Equals(tab, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var active = Editor.CurrentFilePath;
        var activeIndex = _tabs.FindIndex(tab => string.Equals(tab, active, StringComparison.OrdinalIgnoreCase));
        var close = scope switch
        {
            TabCloseScope.All => _tabs.ToArray(),
            TabCloseScope.Others => _tabs.Where(tab => !string.Equals(tab, path, StringComparison.OrdinalIgnoreCase)).ToArray(),
            TabCloseScope.Left => _tabs.Take(index).ToArray(),
            TabCloseScope.Right => _tabs.Skip(index + 1).ToArray(),
            _ => new[] { path }
        };
        var closed = close.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _tabs.RemoveAll(tab => closed.Contains(tab));
        if (active is not null && closed.Contains(active))
        {
            _syncTimer.Stop(); CancelForwardSync();
            if (_tabs.Count > 0) ActivateFile(_tabs[Math.Clamp(activeIndex, 0, _tabs.Count - 1)]);
            else
            {
                Editor.CloseView(); Editor.Visibility = Visibility.Collapsed; NoOpenFile.Visibility = Visibility.Visible;
                CursorStatus.Text = "未打开文件";
                OutlineTree.MarkCaret(null); OutlineTree.HighlightViewport(null);
            }
        }
        RefreshTabs();
        if (_tabs.Count > 0) Editor.TextArea.Focus();
        if (closed.Any(tab => _changes.ContainsKey(tab))) Status("标签已关闭 · 未保存内容仍保留，重新打开可继续编辑");
    }
    private void MoveTab(string path, int target)
    {
        if (_transitioning || _closing || _allowClose) return;
        var from = _tabs.FindIndex(tab => string.Equals(tab, path, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || target < 0 || target >= _tabs.Count || from == target) return;
        var tab = _tabs[from]; _tabs.RemoveAt(from); _tabs.Insert(target, tab); RefreshTabs();
    }
}
