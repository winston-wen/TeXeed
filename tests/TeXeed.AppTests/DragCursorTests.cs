using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TeXeed.App;
using TeXeed.App.Controls;

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
        if (QueryCursorFor(track.DecreaseRepeatButton) != Cursors.Arrow || QueryCursorFor(track.IncreaseRepeatButton) != Cursors.Arrow)
            throw new Exception(label + " track does not use the ordinary pointer");
        if (!GrabCursor.GetIsEnabled(track.Thumb) || QueryCursorFor(track.Thumb) != Cursors.Arrow)
            throw new Exception(label + " thumb does not use the ordinary pointer at rest");
        try
        {
            if (!track.Thumb.CaptureMouse())
            {
                var pointerReadable = GetCursorPos(out _);
                var pointerError = pointerReadable ? 0 : Marshal.GetLastWin32Error();
                throw new Exception($"Could not capture {label}: active={Window.GetWindow(track)?.IsActive}, visible={track.Thumb.IsVisible}, enabled={track.Thumb.IsEnabled}, range={track.Minimum}..{track.Maximum}, viewport={track.ViewportSize}, pointer={pointerReadable}, Win32={pointerError}");
            }
            if (QueryCursorFor(track.Thumb) != Cursors.Arrow)
                throw new Exception(label + " changed its cursor before holding or dragging");
        }
        finally { track.Thumb.ReleaseMouseCapture(); }
    }

    private static void SaveCursor(Cursor cursor, string path)
    {
        var handle = (SafeHandle)typeof(Cursor).GetProperty("Handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cursor)!;
        var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle.DangerousGetHandle(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    private static async Task CheckGrabGesturesAsync(MainWindow window, string output)
    {
        var editor = (LatexEditor)window.FindName("Editor");
        editor.Document.Insert(editor.Document.TextLength, "\n" + string.Join('\n', Enumerable.Repeat(new string('x', 200), 150)));
        editor.SetWordWrap(false); editor.ScrollToHome(); window.UpdateLayout();
        var pdf = Path.Combine(output, "grab-preview.pdf"); WriteThemePdf(pdf);
        await ((PdfPreview)window.FindName("Preview")).LoadAsync(pdf); window.UpdateLayout();
        var strip = (EditorTabStrip)window.FindName("FileTabs");
        var title = (Button)((StackPanel)strip.FindName("Items")).Children.Cast<Border>()
            .Select(row => ((Grid)row.Child).Children[0]).First();
        var scrollThumbs = Descendants<ScrollBar>(window).Where(bar => bar.IsVisible)
            .Select(bar => ((Track)bar.Template.FindName("PART_Track", bar)).Thumb).ToArray();
        if (scrollThumbs.Length < 3) throw new Exception("Gesture fixture needs both editor scrollbars and the PDF scrollbar");
        var owners = new[] { (Owner: (FrameworkElement)title, Label: "tab") }
            .Concat(scrollThumbs.Select((thumb, index) => (Owner: (FrameworkElement)thumb, Label: "scroll-" + index)));
        var property = (DependencyProperty)typeof(GrabCursor).GetField("StateProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        foreach (var (owner, label) in owners)
        {
            var state = owner.GetValue(property) ?? throw new Exception(label + " lacks grab behavior");
            if (QueryCursorFor(owner) != Cursors.Arrow) throw new Exception(label + " hover was not an arrow");
            try
            {
                // Drive the same input-state transitions while holding actual WPF capture.
                // No desktop pointer movement: the user can keep using their mouse.
                if (!owner.CaptureMouse()) throw new Exception("Cannot capture " + label);
                Invoke(state, "Begin", new Point(20, 20));
                Invoke(state, "MovePointer", new Point(20, 20), true);
                if (QueryCursorFor(owner) != Cursors.Arrow) throw new Exception(label + " press changed cursor too early");
                Invoke(state, "CompleteHold", false);
                if (QueryCursorFor(owner) != Cursors.Arrow) throw new Exception(label + " short press showed a grab cursor");
                Invoke(state, "Begin", new Point(20, 20));
                Invoke(state, "CompleteHold", true);
                if (QueryCursorFor(owner) != DragCursors.Grab) throw new Exception(label + " hold did not show custom grab cursor");
                Invoke(state, "MovePointer", new Point(40, 40), true);
                if (QueryCursorFor(owner) != DragCursors.Grabbing) throw new Exception(label + " drag did not show custom grabbing cursor");
                owner.ReleaseMouseCapture();
                if (QueryCursorFor(owner) != Cursors.Arrow) throw new Exception(label + " capture loss did not restore arrow");
                owner.CaptureMouse(); Invoke(state, "Begin", new Point()); Invoke(state, "CompleteHold", true);
                owner.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
                if (QueryCursorFor(owner) != Cursors.Arrow || ((System.Windows.Threading.DispatcherTimer)Field(state, "_hold")!).IsEnabled)
                    throw new Exception(label + " release left cursor/timer behind");
            }
            finally { owner.ReleaseMouseCapture(); }
        }
        if (DragCursors.Grab == Cursors.Hand || DragCursors.Grabbing == Cursors.Hand || DragCursors.Grab == DragCursors.Grabbing)
            throw new Exception("Grab cursors are not distinct application-owned shapes");
        SaveCursor(DragCursors.Grab, Path.Combine(output, "cursor-grab.png"));
        SaveCursor(DragCursors.Grabbing, Path.Combine(output, "cursor-grabbing.png"));
        Capture(window, Path.Combine(output, "grab-gesture-layout.png"));
        await CallAsync(window, "SaveAsync");
        Console.WriteLine("PASS: WPF capture with hover/short press/hold/drag/capture loss/release transitions on tabs and both scrollbar axes; custom open/closed hand cursors loaded by Windows");
    }
}
