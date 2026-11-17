using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    // Exercises the same physical pointer path used to tear a preview out of its tab strip.
    private static async Task CheckPreviewDockDragAsync(MainWindow window, string root, string output, bool physical)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var folder = Path.Combine(output, "preview-dock-drag-project"); Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "main.tex"); var b = Path.Combine(folder, "paper.tex");
        File.WriteAllText(a, "\\documentclass{article}\n\\begin{document}\nFirst preview.\n\\end{document}\n");
        File.WriteAllText(b, "\\documentclass{article}\n\\begin{document}\nSecond preview.\\newpage Second page.\n\\end{document}\n");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var settings = new AppSettings { Engine = TexEngine.Tectonic, EnginePath = Path.Combine(root, ".tools", "tectonic", "tectonic.exe"), LivePreview = false, FollowMode = PreviewFollowMode.None };
        typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, settings);
        await CallAsync(window, "OpenAsync", a); await CallAsync(window, "BuildAsync");
        await CallAsync(window, "OpenProjectEntryAsync", b, false, false, true); await CallAsync(window, "BuildAsync");
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var secondKey = TabWorkspace.PreviewKeyFor(b);
        Check(tabs.Owner(TabWorkspace.PreviewKey) == TabSide.Right && tabs.Owner(secondKey) == TabSide.Right,
            "The two previews did not start in the same existing group");
        var overlay = (FrameworkElement)window.FindName("DockDropOverlay");
        var cue = (FrameworkElement)window.FindName("DockDropCue");
        var originalPointer = new NativePoint(); GetCursorPos(out originalPointer); var pressed = false;
        async Task Settle() { await Task.Delay(100); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
        IList Docks(MainWindow w) => (IList)Field(w, "_previewDocks")!;
        PdfPreview View(MainWindow w, string path) => (PdfPreview)Field(Invoke(w, "EntryState", path)!, "View")!;
        bool Rendered(PdfPreview view)
        {
            if ((bool)Field(view, "_rendering")! || ((DispatcherTimer)Field(view, "_renderTimer")!).IsEnabled) return false;
            var scroll = (ScrollViewer)Field(view, "_scroll")!; var viewport = new Rect(new Point(), scroll.RenderSize);
            var pages = Descendants<Image>(view).Where(image => image.ActualWidth > 0 && image.ActualHeight > 0 &&
                image.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), image.RenderSize)).IntersectsWith(viewport)).ToArray();
            return pages.Length > 0 && pages.All(image => image.Source is BitmapSource);
        }
        EditorTabStrip StripFor(string key)
        {
            var side = tabs.Owner(key) ?? throw new Exception("Preview tab has no owner");
            return (EditorTabStrip)Invoke(window, "GroupStrip", side)!;
        }
        Point TabPoint(string key)
        {
            var strip = StripFor(key); var items = (StackPanel)strip.FindName("Items");
            var tab = items.Children.OfType<Border>().Single(x => string.Equals(x.Tag as string, key, StringComparison.OrdinalIgnoreCase));
            return tab.PointToScreen(new Point(Math.Min(25, tab.ActualWidth / 3), tab.ActualHeight / 2));
        }
        async Task Drag(string key, Point end, bool expectCue = true)
        {
            if (!physical)
            {
                Check((bool)Invoke(window, "UpdateDockDrag", key, end)!, "No destination cue appeared during simulated drag");
                if (expectCue) Check(cue.IsVisible, "The destination cue was not visible");
                Invoke(window, "DropWorkspaceTab", key, end); await Settle(); return;
            }
            var start = TabPoint(key);
            Check(SetCursorPos((int)start.X, (int)start.Y), "Cannot position pointer on preview title");
            await Task.Delay(70); GetCursorPos(out var actual);
            Check(Math.Abs(actual.X - start.X) < 4 && Math.Abs(actual.Y - start.Y) < 4, "Pointer moved before preview drag");
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); pressed = true; await Task.Delay(50);
            for (var i = 1; i <= 8; i++)
            {
                var f = i / 8.0;
                Check(SetCursorPos((int)(start.X + (end.X - start.X) * f), (int)(start.Y + (end.Y - start.Y) * f)), "Cannot move pointer while dragging preview");
                await Task.Delay(30);
            }
            if (expectCue)
            {
                Check(cue.IsVisible, "No destination cue appeared during physical drag");
                Capture(window, Path.Combine(output, "preview-dock-drag-cue.png"));
            }
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); pressed = false; await Settle();
        }
        Point EdgePoint(Dock edge) => overlay.PointToScreen(edge switch
        {
            Dock.Left => new Point(8, overlay.ActualHeight / 2), Dock.Right => new Point(overlay.ActualWidth - 8, overlay.ActualHeight / 2),
            Dock.Top => new Point(overlay.ActualWidth / 2, 8), _ => new Point(overlay.ActualWidth / 2, overlay.ActualHeight - 8)
        });
        async Task ClickView(PdfPreview view, string entry)
        {
            if (physical)
            {
                var point = view.PointToScreen(new Point(view.ActualWidth / 2, view.ActualHeight / 2));
                Check(SetCursorPos((int)point.X, (int)point.Y), "Cannot position pointer on PDF preview");
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            }
            else view.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await WaitUntilAsync(() => string.Equals(Field(window, "_activePreviewEntry") as string, entry, StringComparison.OrdinalIgnoreCase)
                && !(bool)Field(window, "_switchingEntryPreview")!, "Clicking a visible preview did not activate its own PDF");
        }
        try
        {
            if (physical) window.Activate(); await Settle();
            foreach (var edge in new[] { Dock.Left, Dock.Right, Dock.Top, Dock.Bottom })
            {
                if (edge == Dock.Right)
                {
                    Invoke(window, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKey);
                    await WaitUntilAsync(() => string.Equals(Field(window, "_activePreviewEntry") as string, a, StringComparison.OrdinalIgnoreCase)
                        && !(bool)Field(window, "_switchingEntryPreview")!, "Could not activate the first preview before dragging an inactive tab");
                }
                await Drag(secondKey, EdgePoint(edge));
                Check(Docks(window).Count == 1 && (Dock)Field(Docks(window)[0]!, "Edge")! == edge,
                    $"Dragging to {edge} did not create the requested preview area");
                await WaitUntilAsync(() => string.Equals(Field(window, "_activePreviewEntry") as string, b, StringComparison.OrdinalIgnoreCase)
                    && !(bool)Field(window, "_switchingEntryPreview")!, "Dragging the preview did not activate the requested entry");
                Check(tabs.FocusedSide == tabs.Owner(secondKey), "The new preview area did not receive focus");
                await WaitUntilAsync(() => View(window, a).IsVisible && View(window, b).IsVisible && View(window, a).PageCount == 1 && View(window, b).PageCount == 2,
                    $"Two different PDFs were not simultaneously visible after dragging to {edge}");
                var aView = View(window, a); var bView = View(window, b);
                Check(!ReferenceEquals(aView, bView), "Both preview areas used the same PDF control");
                aView.SetZoom(1.1); bView.SetZoom(1.35); bView.ShowLocation(new PdfLocation(2, 0, 0), highlight: false); await Settle();
                Check(Math.Abs(aView.Zoom - 1.1) < .01 && Math.Abs(bView.Zoom - 1.35) < .01 && bView.VisiblePage == 2,
                    "Independent preview zoom or position was lost");
                await WaitUntilAsync(() => Rendered(aView) && Rendered(bView), $"Both PDFs did not render after dragging to {edge}");
                Capture(window, Path.Combine(output, $"preview-dock-{edge.ToString().ToLowerInvariant()}.png"));
                if (edge == Dock.Bottom)
                {
                    await ClickView(aView, a); await ClickView(bView, b);
                    Invoke(window, "SetManualZoom", 1.42); await Settle();
                    Check(Math.Abs(aView.Zoom - 1.1) < .01 && Math.Abs(bView.Zoom - 1.42) < .01,
                        "The global zoom setting did not act on the focused preview");
                    Invoke(window, "SetManualZoom", 1.35); await Settle();
                    if (physical)
                    {
                        var dock = Docks(window)[0]!; var pane = (FrameworkElement)Field(dock, "Pane")!;
                        var split = (Grid)Field(dock, "Split")!; var divider = split.Children.OfType<GridSplitter>().Single();
                        var before = pane.ActualHeight; var start = divider.PointToScreen(new Point(divider.ActualWidth / 2, divider.ActualHeight / 2));
                        var end = divider.PointToScreen(new Point(divider.ActualWidth / 2, divider.ActualHeight / 2 - 70));
                        Check(SetCursorPos((int)start.X, (int)start.Y), "Cannot position pointer on divider");
                        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); pressed = true; await Task.Delay(80);
                        for (var step = 1; step <= 8; step++) { var f = step / 8.0; SetCursorPos((int)(start.X + (end.X - start.X) * f), (int)(start.Y + (end.Y - start.Y) * f)); await Task.Delay(35); }
                        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); pressed = false; await Settle();
                        Check(pane.ActualHeight > before + 20 && aView.IsVisible && bView.IsVisible,
                            "Dragging the divider did not resize the split while keeping both previews visible");
                        Capture(window, Path.Combine(output, "preview-dock-divider-resized.png"));
                    }
                    break;
                }
                var originalPane = (FrameworkElement)window.FindName("PreviewPane");
                await Drag(secondKey, originalPane.PointToScreen(new Point(originalPane.ActualWidth / 2, originalPane.ActualHeight / 2)));
                Check(Docks(window).Count == 0 && tabs.Owner(secondKey) == TabSide.Right,
                    $"Moving the {edge} preview back did not remove its empty area");
            }
            await CloseAndObserveAsync(window, "preview split project close");
            var reopened = new MainWindow(a) { Width = 1100, Height = 800, Left = physical ? 80 : -3000, Top = physical ? 80 : -3000, ShowActivated = physical, WindowStartupLocation = WindowStartupLocation.Manual };
            reopened.Show();
            try
            {
                await WaitUntilAsync(() => Field(reopened, "_project") is TexProject && !(bool)Field(reopened, "_transitioning")! && Docks(reopened).Count == 1,
                    "Restart did not restore the preview area");
                await WaitUntilAsync(() => View(reopened, a).IsVisible && View(reopened, b).IsVisible && View(reopened, a).PageCount == 1 && View(reopened, b).PageCount == 2,
                    "Restart did not restore both visible PDF previews");
                var restoredA = View(reopened, a); var restoredB = View(reopened, b);
                Check((Dock)Field(Docks(reopened)[0]!, "Edge")! == Dock.Bottom && Math.Abs(restoredA.Zoom - 1.1) < .01 && Math.Abs(restoredB.Zoom - 1.35) < .01 && restoredB.VisiblePage == 2,
                    "Restart lost the split orientation or independent preview positions");
                Invoke(reopened, "CloseTabs", secondKey, TabCloseScope.This);
                await WaitUntilAsync(() => Docks(reopened).Count == 0, "Closing the final preview did not remove its empty area");
                await CloseAndObserveAsync(reopened, "reopened preview split project");
            }
            finally { if (reopened.IsVisible) reopened.Close(); }

            var store = typeof(MainWindow).Assembly.GetType("TeXeed.App.Services.ProjectReadingStore")!;
            var historyPath = (string)store.GetMethod("PathFor", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [a])!;
            var history = JsonNode.Parse(File.ReadAllText(historyPath))!.AsObject();
            history["PreviewDocks"] = new JsonArray(
                null,
                new JsonObject { ["Edge"] = "Left", ["Tabs"] = new JsonArray(null, TabWorkspace.PreviewKey, secondKey, secondKey), ["Active"] = secondKey, ["Focused"] = true },
                new JsonObject { ["Edge"] = "Top", ["Tabs"] = new JsonArray(secondKey), ["Active"] = secondKey, ["Focused"] = true });
            history["ActivePreviewEntry"] = b; File.WriteAllText(historyPath, history.ToJsonString());
            var repaired = new MainWindow(a) { Width = 1100, Height = 800, Left = physical ? 80 : -3000, Top = physical ? 80 : -3000, ShowActivated = physical, WindowStartupLocation = WindowStartupLocation.Manual };
            repaired.Show();
            try
            {
                await WaitUntilAsync(() => Field(repaired, "_project") is TexProject && !(bool)Field(repaired, "_transitioning")! && Docks(repaired).Count == 1,
                    "Malformed saved groups were not reduced to one valid preview area");
                var repairedTabs = (TabWorkspace)Field(repaired, "_workspaceTabs")!;
                var all = new[] { TabSide.Left, TabSide.Right }.Concat(repairedTabs.ExtraGroups).SelectMany(repairedTabs.Tabs).ToArray();
                Check(all.Count(key => key == TabWorkspace.PreviewKey) == 1 && all.Count(key => key == secondKey) == 1 &&
                    repairedTabs.Owner(TabWorkspace.PreviewKey) == TabSide.Right && repairedTabs.FocusedSide == repairedTabs.Owner(secondKey),
                    "Malformed saved groups duplicated a preview or lost focus");
                await CloseAndObserveAsync(repaired, "repaired preview split project");
            }
            finally { if (repaired.IsVisible) repaired.Close(); }
            Console.WriteLine($"PASS: {(physical ? "physical" : "simulated")} four-edge preview drag and merge, simultaneous PDFs, independent zoom/page and restart restoration");
        }
        finally
        {
            if (pressed) mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            if (physical) SetCursorPos(originalPointer.X, originalPointer.Y);
        }
    }
}
