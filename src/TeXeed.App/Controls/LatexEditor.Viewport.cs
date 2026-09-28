using System.Windows;
using System.Windows.Threading;
using TeXeed.Core;

namespace TeXeed.App.Controls;

public sealed partial class LatexEditor
{
    private DispatcherOperation? _viewportNotification;
    private SourceLocation? _lastViewportLocation;

    public event EventHandler<SourceLocation>? ViewportLocationChanged;

    /// <summary>
    /// Source position at the start of the display row nearest the viewport's vertical center.
    /// Returns null until layout is current; does not depend on or move the caret.
    /// </summary>
    public SourceLocation? ViewportLocation
    {
        get
        {
            var view = TextArea.TextView;
            if (_changingDocument || CurrentFilePath is not { } path || Document is null
                || !view.IsMeasureValid || !view.IsArrangeValid || !view.VisualLinesValid
                || view.ActualHeight <= 0 || view.ActualWidth <= 0 || view.VisualLines.Count == 0)
                return null;

            var first = view.VisualLines[0];
            var last = view.VisualLines[^1];
            // Keep short documents and the blank space after the final line anchored
            // to rendered text. AvalonEdit's hit testing handles wrapping and folding.
            var y = Math.Clamp(view.ScrollOffset.Y + view.ActualHeight / 2,
                first.VisualTop, Math.Max(first.VisualTop, last.VisualTop + last.Height - 0.001));
            var position = view.GetPosition(new Point(0, y));
            if (position is not { } source) return null;
            var line = Document.GetLineByNumber(Math.Clamp(source.Line, 1, Document.LineCount));
            return new SourceLocation(path, line.LineNumber, Math.Clamp(source.Column, 1, line.Length + 1));
        }
    }

    private void InitializeViewportTracking()
    {
        var view = TextArea.TextView;
        view.ScrollOffsetChanged += (_, _) => QueueViewportLocationChanged();
        view.VisualLinesChanged += (_, _) => QueueViewportLocationChanged();
        view.SizeChanged += (_, _) => QueueViewportLocationChanged();
        Loaded += (_, _) => QueueViewportLocationChanged();
        Unloaded += (_, _) => ResetViewportTracking();
    }

    private void QueueViewportLocationChanged()
    {
        if (_viewportNotification is { Status: DispatcherOperationStatus.Pending }) return;
        // ScrollOffsetChanged and VisualLinesChanged can fire inside MeasureOverride.
        // Let rendering finish before consumers read an anchor or start synchronization.
        _viewportNotification = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _viewportNotification = null;
            if (ViewportLocation is not { } location || location == _lastViewportLocation) return;
            _lastViewportLocation = location;
            ViewportLocationChanged?.Invoke(this, location);
        }));
    }

    private void ResetViewportTracking()
    {
        _viewportNotification?.Abort();
        _viewportNotification = null;
        _lastViewportLocation = null;
    }
}
