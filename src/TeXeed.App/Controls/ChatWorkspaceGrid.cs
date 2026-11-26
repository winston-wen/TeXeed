using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TeXeed.App.Controls;

/// <summary>Report movement of the reading row in the same layout pass as the surrounding controls.</summary>
public sealed class ChatWorkspaceGrid : Grid
{
    internal FrameworkElement? ReadingViewport { get; set; }
    internal Action<double>? ViewportMoved { get; set; }
    private double? _previousTop;

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        if (ReadingViewport is not { IsVisible: true, ActualHeight: > 0 } viewport)
        {
            _previousTop = null;
            return size;
        }
        var top = VisualTreeHelper.GetOffset(viewport).Y;
        var previous = _previousTop;
        _previousTop = top;
        if (previous is double oldTop && Math.Abs(top - oldTop) > .01)
            ViewportMoved?.Invoke(top - oldTop);
        return size;
    }
}
