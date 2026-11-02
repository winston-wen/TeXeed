using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private static async Task CheckScrollBounceAsync(MainWindow window, string output)
    {
        var root = Directory.GetCurrentDirectory();
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var fixture = Path.Combine(output, "wheel-project"); Directory.CreateDirectory(fixture);
        var main = Path.Combine(fixture, "main.tex");
        var lines = new List<string> { "\\documentclass{article}", "\\begin{document}" };
        for (var page = 0; page < 8; page++)
        {
            if (page > 0) lines.Add("\\newpage");
            for (var row = 0; row < 35; row++)
            {
                lines.Add("% " + string.Concat(Enumerable.Repeat("中文表格流程图 Ｗｉｄｅ emoji 🧮 and lower case ", row % 11 + 1)));
                lines.Add("\\noindent " + string.Concat(Enumerable.Repeat("Long wrapping sample text for testing source scrolling. ", row % 5 + 1)) + "\\par");
            }
        }
        lines.Add("\\end{document}"); File.WriteAllText(main, string.Join('\n', lines));
        window.GetType().GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window,
            new AppSettings { LivePreview = false, WordWrap = true, FollowMode = PreviewFollowMode.Viewport,
                AutoCropPdf = true, Engine = TexEngine.Tectonic, EnginePath = engine, SynctexPath = Path.Combine(fixture, "absent.exe") });
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync");
        var editor = (LatexEditor)window.FindName("Editor");
        var preview = (PdfPreview)window.FindName("Preview");
        var scroll = (ScrollViewer)Field(preview, "_scroll")!;
        editor.SetWordWrap(true); preview.AutoCrop = true;
        await WaitForPreviewRenderedAsync(window);
        window.Activate(); await Task.Delay(200);
        var physical = GetCursorPos(out var originalPointer);
        try
        {
            foreach (var mode in new[] { PreviewFollowMode.None, PreviewFollowMode.Viewport })
            {
                Invoke(window, "ChangeFollowMode", mode);
                foreach (var side in new[] { "editor", "preview" })
                {
                    editor.ScrollToVerticalOffset(editor.ExtentHeight * .35);
                    scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * .35);
                    await Task.Delay(500); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var element = side == "editor" ? (FrameworkElement)editor.TextArea.TextView : preview;
                    var pt = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
                    if (physical && !SetCursorPos((int)pt.X, (int)pt.Y)) throw new Exception("Cannot place pointer for mouse-wheel test");
                    await Task.Delay(100);
                    var changes = new List<double>();
                    EventHandler changed = (_, _) => changes.Add(editor.TextArea.TextView.ScrollOffset.Y);
                    ScrollChangedEventHandler pdfChanged = (_, e) => { if (e.VerticalChange != 0) changes.Add(scroll.VerticalOffset); };
                    if (side == "editor") editor.TextArea.TextView.ScrollOffsetChanged += changed; else scroll.ScrollChanged += pdfChanged;
                    try
                    {
                        for (var step = 0; step < 12; step++)
                        {
                            var delta = step < 6 ? -120 : 120;
                            var before = side == "editor" ? editor.VerticalOffset : scroll.VerticalOffset;
                            changes.Clear();
                            if (physical) mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                            else
                            {
                                var target = side == "editor" ? (UIElement)editor.TextArea.TextView : scroll;
                                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                                target.RaiseEvent(wheel);
                                if (!wheel.Handled) { wheel.RoutedEvent = Mouse.MouseWheelEvent; target.RaiseEvent(wheel); }
                            }
                            await Task.Delay(350); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            var after = side == "editor" ? editor.VerticalOffset : scroll.VerticalOffset;
                            Console.WriteLine($"WHEEL {mode} {side} {step}: {before:F1} -> {after:F1}, changes={string.Join(',', changes.Select(x => x.ToString("F1")))}");
                            if (delta < 0 ? after <= before + .1 : after >= before - .1)
                                throw new Exception($"Wheel {delta} failed to move {side} in {mode}: {before:F1} -> {after:F1}");
                            var path = new[] { before }.Concat(changes).Append(after).ToArray();
                            for (var i = 1; i < path.Length; i++)
                            {
                                var change = path[i] - path[i - 1];
                                if (delta < 0 ? change < -.5 : change > .5)
                                    throw new Exception($"Wheel {delta} reversed during {side} scrolling in {mode}: {string.Join(" -> ", path.Select(x => x.ToString("F2")))}");
                            }
                        }
                    }
                    finally { if (side == "editor") editor.TextArea.TextView.ScrollOffsetChanged -= changed; else scroll.ScrollChanged -= pdfChanged; }
                }
            }
            Invoke(window, "ChangeFollowMode", PreviewFollowMode.Viewport);
            editor.ScrollToVerticalOffset(editor.TextArea.TextView.DefaultLineHeight * 30);
            await Task.Delay(500); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // A source layout can have raised ScrollOffsetChanged but still have its
            // coalesced viewport notification queued. Manually scroll the preview
            // before that notification runs; it must not restart stale following.
            editor.ScrollToVerticalOffset(editor.TextArea.TextView.DefaultLineHeight * 150);
            window.UpdateLayout();
            var beforePreview = scroll.VerticalOffset;
            var pendingWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            scroll.RaiseEvent(pendingWheel);
            if (!pendingWheel.Handled) { pendingWheel.RoutedEvent = Mouse.MouseWheelEvent; scroll.RaiseEvent(pendingWheel); }
            await Task.Delay(650); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (scroll.VerticalOffset <= beforePreview + .1 || scroll.VerticalOffset > beforePreview + 60)
                throw new Exception($"Pending source viewport callback overrode preview wheel: {beforePreview:F1} -> {scroll.VerticalOffset:F1}");
            var heldPreview = scroll.VerticalOffset;
            if (!(bool)Field(window, "_previewUserOwnsView")!) throw new Exception("Actual preview wheel did not retain its reading position");
            Invoke(window, "QueueFollowSync");
            editor.TextArea.TextView.Redraw(); window.UpdateLayout();
            await Task.Delay(350); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Math.Abs(scroll.VerticalOffset - heldPreview) > 1) throw new Exception("A later layout/build follow pulled the manually scrolled preview back");
            // If a new source wheel arrives before that same coalesced callback,
            // it is newer than the preview interaction and must resume following.
            editor.ScrollToVerticalOffset(editor.TextArea.TextView.DefaultLineHeight * 30);
            window.UpdateLayout();
            pendingWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            scroll.RaiseEvent(pendingWheel);
            if (!pendingWheel.Handled) { pendingWheel.RoutedEvent = Mouse.MouseWheelEvent; scroll.RaiseEvent(pendingWheel); }
            var sourceWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            editor.TextArea.TextView.RaiseEvent(sourceWheel);
            if (!sourceWheel.Handled) { sourceWheel.RoutedEvent = Mouse.MouseWheelEvent; editor.TextArea.TextView.RaiseEvent(sourceWheel); }
            var oldPreview = scroll.VerticalOffset;
            await Task.Delay(650); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Math.Abs(scroll.VerticalOffset - oldPreview) < 200)
                throw new Exception("A newer source wheel did not resume following after preview input");
            if ((bool)Field(window, "_previewUserOwnsView")!) throw new Exception("Source wheel did not resume following");
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            preview.RaiseEvent(click);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if ((bool)Field(window, "_previewUserOwnsView")!) throw new Exception("A preview click without scrolling suspended following");
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            bar.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbTrack, scroll.VerticalOffset + 40) { RoutedEvent = ScrollBar.ScrollEvent });
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 40);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!(bool)Field(window, "_previewUserOwnsView")!) throw new Exception("Preview scrollbar drag was not treated as a manual scroll");

            // Hold the real SyncTeX index gate to ensure a prior inverse lookup is
            // still awaiting data when the user starts a new source-wheel gesture.
            var sync = (SyncTexService)Field(window, "_sync")!;
            var project = (TexProject)Field(window, "_project")!;
            var pdfPath = (string)Field(window, "_pdfPath")!;
            var settings = (AppSettings)Field(window, "_settings")!;
            var inverseTarget = await new SyncTexService().ForwardViewportAsync(project, pdfPath, new SourceLocation(main, 20), settings)
                ?? throw new Exception("Inverse-race fixture lacks a PDF location");
            var gate = (SemaphoreSlim)Field(sync, "cacheGate")!;
            await gate.WaitAsync();
            var released = false;
            try
            {
                var caretBefore = editor.CaretOffset;
                var inverse = CallAsync(window, "InverseAsync", inverseTarget);
                if ((int)Field(window, "_inverseQueryOwner")! == 0) throw new Exception("Inverse lookup did not enter its asynchronous wait");
                editor.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                gate.Release(); released = true;
                await inverse;
                if (editor.CaretOffset != caretBefore) throw new Exception("A delayed PDF inverse lookup overrode a newer source-wheel gesture");
            }
            finally { if (!released) gate.Release(); }

            // An external edit reloads the visible tab in the background. It is
            // not a new source navigation and must not revoke manual PDF reading.
            bar.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbTrack, scroll.VerticalOffset + 40) { RoutedEvent = ScrollBar.ScrollEvent });
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 40);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var manualOffset = scroll.VerticalOffset;
            var manualSequence = (long)Field(window, "_userNavigationSequence")!;
            File.AppendAllText(main, "\n% external-change");
            window.GetType().GetField("_externalChangePending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, true);
            await CallAsync(window, "ReloadExternalAsync");
            if (!editor.Text.EndsWith("% external-change", StringComparison.Ordinal)) throw new Exception("External source change was not reloaded");
            if (!(bool)Field(window, "_previewUserOwnsView")! || (long)Field(window, "_userNavigationSequence")! != manualSequence)
                throw new Exception("Background source reload revoked manual preview ownership");
            await CallAsync(window, "BuildAsync");
            await WaitForPreviewRenderedAsync(window); await Task.Delay(400);
            if (!(bool)Field(window, "_lastCompilePreviewPublished")!) throw new Exception("Reload fixture did not publish a fresh preview");
            if (!(bool)Field(window, "_previewUserOwnsView")! || Math.Abs(scroll.VerticalOffset - manualOffset) > 2)
                throw new Exception("Recompiling after background reload pulled the manually scrolled preview away");
        }
        finally { if (physical) SetCursorPos(originalPointer.X, originalPointer.Y); }
        Console.WriteLine($"PASS: {(physical ? "physical" : "routed")} mouse wheel advances source and preview in None and Viewport modes");
    }
}
