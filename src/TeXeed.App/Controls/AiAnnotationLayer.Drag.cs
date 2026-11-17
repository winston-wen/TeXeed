using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

internal sealed partial class AiAnnotationLayer
{
    private bool IsAnswerPopup => _margin && _host is MarkdownView;
    private bool _draggingAnswerPopup;
    private Point _answerDragStart, _answerPopupStart;

    private void ConfigureAnswerPopup(Border surface, StackPanel stack, Rect anchor)
    {
        var window = IsAnswerPopup ? Window.GetWindow(_host) : null;
        if (window is null)
        {
            _popup.PlacementTarget = _host; _popup.Child = surface;
            _popup.HorizontalOffset = anchor.X; _popup.VerticalOffset = anchor.Bottom + 4;
            return;
        }
        surface.Child = null;
        surface.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _popup.PlacementTarget = window; _popup.Child = surface;
        var point = _host.TranslatePoint(new Point(anchor.X, anchor.Bottom + 4), window);
        _popup.HorizontalOffset = point.X; _popup.VerticalOffset = point.Y;
        surface.Loaded += (_, _) => FitAnswerPopup();
        surface.SizeChanged += (_, _) => FitAnswerPopup();
    }

    private void EnableAnswerPopupDrag(Grid header)
    {
        header.Name = "AnnotationDragHeader"; header.Background = Brushes.Transparent; header.Cursor = Cursors.SizeAll;
        header.ToolTip = "按住拖动浮窗";
        System.Windows.Automation.AutomationProperties.SetName(header, "拖动批注浮窗");
        header.PreviewMouseLeftButtonDown += (_, e) =>
        {
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, header); node = node switch
                 { Visual visual => VisualTreeHelper.GetParent(visual), FrameworkContentElement content => content.Parent, ContentElement content => ContentOperations.GetParent(content), _ => LogicalTreeHelper.GetParent(node) })
                if (node is ButtonBase) return;
            if (_popup.PlacementTarget is not Window window || !header.CaptureMouse()) return;
            _answerDragStart = window.PointFromScreen(header.PointToScreen(e.GetPosition(header)));
            _answerPopupStart = new Point(_popup.HorizontalOffset, _popup.VerticalOffset);
            _draggingAnswerPopup = true; e.Handled = true;
        };
        header.PreviewMouseMove += (_, e) =>
        {
            if (!_draggingAnswerPopup || _popup.PlacementTarget is not Window window) return;
            var point = window.PointFromScreen(header.PointToScreen(e.GetPosition(header)));
            var delta = point - _answerDragStart;
            _popup.HorizontalOffset = _answerPopupStart.X + delta.X;
            _popup.VerticalOffset = _answerPopupStart.Y + delta.Y;
            FitAnswerPopup(); e.Handled = true;
        };
        header.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_draggingAnswerPopup) return;
            _draggingAnswerPopup = false; header.ReleaseMouseCapture(); e.Handled = true;
        };
        header.LostMouseCapture += (_, _) => _draggingAnswerPopup = false;
    }

    private void QueueAnswerPopupFit()
    {
        if (!_popup.IsOpen) return;
        _host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitAnswerPopup));
    }

    private void RefreshAnswerPopupPlacement()
    {
        if (!_popup.IsOpen) return;
        // A WPF Popup is a separate window; changing its offset refreshes screen placement.
        var x = _popup.HorizontalOffset; _popup.HorizontalOffset = x + .01; _popup.HorizontalOffset = x;
        QueueAnswerPopupFit();
    }

    private void FitAnswerPopup()
    {
        if (!_popup.IsOpen || _popup.PlacementTarget is not Window window || _popup.Child is not Border surface || !window.IsVisible) return;
        const double margin = 8;
        surface.Width = Math.Min(Math.Clamp(_host.ActualWidth - 24, 220, 340), Math.Max(80, window.ActualWidth - 2 * margin));
        surface.MaxHeight = Math.Max(60, window.ActualHeight - 2 * margin);
        var width = surface.ActualWidth > 0 ? surface.ActualWidth : surface.Width;
        var height = surface.ActualHeight > 0 ? Math.Min(surface.ActualHeight, surface.MaxHeight) : 0;
        _popup.HorizontalOffset = Math.Clamp(_popup.HorizontalOffset, margin, Math.Max(margin, window.ActualWidth - width - margin));
        _popup.VerticalOffset = Math.Clamp(_popup.VerticalOffset, margin, Math.Max(margin, window.ActualHeight - height - margin));
    }
}
