using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class MainWindow
{
    private readonly Button[] _defaultSidebarButtons;
    private Button? _sidebarDragButton;
    private Point _sidebarDragStart;
    private int _sidebarDragOrigin;

    private void RestoreSidebarOrder(string[] order)
    {
        EndSidebarDrag(false);
        var buttons = order.Select(id => Array.Find(_defaultSidebarButtons, b => Equals(b.Tag, id)))
            .OfType<Button>().Concat(_defaultSidebarButtons).Distinct().ToArray();
        for (var i = 0; i < buttons.Length; i++) MoveSidebarButton(buttons[i], i);
    }

    private void MoveSidebarButton(Button button, int index)
    {
        var buttons = WorkspaceIconBar.Children;
        if (buttons.IndexOf(button) == index) return;
        buttons.Remove(button);
        buttons.Insert(index, button);
    }

    private void SidebarDrag_Down(object sender, MouseButtonEventArgs e)
    {
        if (_project is null || _transitioning || _closing) return;
        for (var target = e.OriginalSource as DependencyObject; target is not null && target != WorkspaceIconBar; target = SidebarParent(target))
        {
            if (target is not Button button) continue;
            _sidebarDragButton = button;
            _sidebarDragOrigin = WorkspaceIconBar.Children.IndexOf(button);
            _sidebarDragStart = e.GetPosition(WorkspaceIconBar);
            break;
        }
    }

    private void SidebarDrag_Move(object sender, MouseEventArgs e)
    {
        if (_sidebarDragButton is not { } button || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(WorkspaceIconBar);
        if (!WorkspaceIconBar.IsMouseCaptured)
        {
            if (Math.Abs(point.Y - _sidebarDragStart.Y) < SystemParameters.MinimumVerticalDragDistance
                && Math.Abs(point.X - _sidebarDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            if (!WorkspaceIconBar.CaptureMouse()) return;
        }
        e.Handled = true;
        if (!WithinSidebarRail(e.GetPosition(WorkspaceRail))) return;
        var rowHeight = WorkspaceIconBar.ActualHeight / WorkspaceIconBar.Children.Count;
        if (rowHeight > 0)
            MoveSidebarButton(button, Math.Clamp((int)(point.Y / rowHeight), 0, WorkspaceIconBar.Children.Count - 1));
    }

    private bool WithinSidebarRail(Point point) => new Rect(WorkspaceRail.RenderSize).Contains(point);

    private void SidebarDrag_Up(object sender, MouseButtonEventArgs e)
    {
        if (!WorkspaceIconBar.IsMouseCaptured) { _sidebarDragButton = null; return; }
        e.Handled = true;
        EndSidebarDrag(WithinSidebarRail(e.GetPosition(WorkspaceRail)) && !_transitioning && !_closing);
    }

    private void SidebarDrag_LostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, WorkspaceIconBar)) EndSidebarDrag(false);
    }

    private void EndSidebarDrag(bool save)
    {
        var button = _sidebarDragButton;
        _sidebarDragButton = null;
        if (WorkspaceIconBar.IsMouseCaptured) WorkspaceIconBar.ReleaseMouseCapture();
        if (button is null || WorkspaceIconBar.Children.IndexOf(button) == _sidebarDragOrigin) return;
        if (save && _project is not null)
        {
            try
            {
                var order = WorkspaceIconBar.Children.OfType<Button>().Select(b => (string)b.Tag).ToArray();
                ProjectExplorerSettings.SaveSidebarOrder(_project.Root, order);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Status("侧栏顺序未保存：" + ex.Message); }
        }
        MoveSidebarButton(button, _sidebarDragOrigin);
    }
}
