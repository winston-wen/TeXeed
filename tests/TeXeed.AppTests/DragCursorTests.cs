using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TeXeed.App;

internal static partial class Program
{
    private static Cursor QueryCursorFor(UIElement target)
    {
        var query = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.QueryCursorEvent, Cursor = Cursors.Arrow };
        target.RaiseEvent(query);
        return query.Cursor;
    }

    private static void AssertHandThumb(Track track, string label)
    {
        if (QueryCursorFor(track.Thumb) != Cursors.Hand)
            throw new Exception(label + " thumb does not use the hand cursor");
        try
        {
            if (!track.Thumb.CaptureMouse()) throw new Exception("Could not capture " + label);
            if (QueryCursorFor(track.Thumb) != Cursors.Hand)
                throw new Exception(label + " lost the hand cursor during capture");
        }
        finally { track.Thumb.ReleaseMouseCapture(); }
    }

    private static void CheckZoomDragCursor(MainWindow window)
    {
        var slider = (Slider)window.FindName("ZoomSlider");
        slider.ApplyTemplate();
        AssertHandThumb((Track)slider.Template.FindName("PART_Track", slider), "Zoom slider");
    }
}
