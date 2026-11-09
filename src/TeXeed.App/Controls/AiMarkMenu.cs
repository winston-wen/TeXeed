using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>Delay the context menu while marking so a right double-click can exit the mode.</summary>
internal sealed class AiMarkMenu
{
    private readonly DispatcherTimer _timer;
    private Point _position;
    private bool _suppressRelease;

    internal AiMarkMenu(FrameworkElement target, Func<bool> isActive, Action exit)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 20) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!isActive() || target.ContextMenu is not { } menu || !target.IsVisible) return;
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.RelativePoint;
            menu.HorizontalOffset = _position.X;
            menu.VerticalOffset = _position.Y;
            menu.IsOpen = true;
        };
        target.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (!isActive() || e.ClickCount < 2) return;
            _timer.Stop();
            _suppressRelease = true;
            exit();
            e.Handled = true;
        };
        target.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (_suppressRelease) { _suppressRelease = false; e.Handled = true; return; }
            if (!isActive()) return;
            e.Handled = true;
            _position = e.GetPosition(target);
            _timer.Stop();
            _timer.Start();
        };
        target.ContextMenuOpening += (_, e) => { if (isActive()) e.Handled = true; };
        target.Unloaded += (_, _) => _timer.Stop();
        if (target.ContextMenu is { } context)
            context.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape || !isActive()) return;
                _timer.Stop(); exit(); context.IsOpen = false; e.Handled = true;
            };
    }

    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
}
