using System.Windows;
using System.Windows.Threading;
using TeXeed.Core;

namespace TeXeed.App.Controls;

public sealed partial class LatexEditor
{
    private DispatcherOperation? _viewportNotification;
    private SourceLocation? _lastViewportLocation;

    public event EventHandler<SourceLocation>? ViewportLocationChanged;
    public event EventHandler? VisibleRangeChanged;

    public (string Path, int FirstLine, int LastLine)? VisibleSourceRange
    {
        get
        {
            var view = TextArea.TextView;
            if (_changingDocument || CurrentFilePath is not { } path || !view.VisualLinesValid
                || !view.IsMeasureValid || !view.IsArrangeValid || view.ActualHeight <= 0) return null;
            var lines = view.VisualLines.Where(line => line.VisualTop + line.Height > view.ScrollOffset.Y
                && line.VisualTop < view.ScrollOffset.Y + view.ActualHeight).ToArray();
            return lines.Length == 0 ? null : (path, lines[0].FirstDocumentLine.LineNumber, lines[^1].LastDocumentLine.LineNumber);
        }
    }

    /// <summary>
    /// Source position near the viewport's vertical center, easing toward the final
    /// visible row near the scroll limit so following can reach the document's end.
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
            var anchorY = view.ScrollOffset.Y + view.ActualHeight / 2;
            var scrollableHeight = ExtentHeight - ViewportHeight;
            if (scrollableHeight > 0)
            {
                // In the final half-screen of scrolling, move the anchor continuously
                // toward the bottom edge. Otherwise the last half-screen of source
                // can never reach a center-only anchor (e.g. a short final PDF page).
                // Use scroll geometry rather than line counts for wrapped/folded text.
                var transition = Math.Min(view.ActualHeight / 2, scrollableHeight);
                // Target the final display row's center, not the empty space below
                // its baseline. This also handles fractional rows at the lower edge.
                var remaining = Math.Max(0, scrollableHeight - VerticalOffset - view.DefaultLineHeight / 2);
                anchorY += view.ActualHeight / 2 * Math.Clamp(1 - remaining / transition, 0, 1);
            }
            // Keep short documents and the blank space after the final line anchored
            // to rendered text. AvalonEdit's hit testing handles wrapping and folding.
            var y = Math.Clamp(anchorY,
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
            VisibleRangeChanged?.Invoke(this, EventArgs.Empty);
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
