using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckWorkspaceRestoreAsync(MainWindow window, string root, string output)
    {
        var directory = Path.Combine(output, "reading-project"); Directory.CreateDirectory(directory);
        var main = Path.Combine(directory, "main.tex"); var extra = Path.Combine(directory, "extra.tex");
        var rows = new List<string> { "\\documentclass{article}", "\\begin{document}" };
        for (var pageNumber = 1; pageNumber <= 5; pageNumber++)
        {
            if (pageNumber > 1) rows.Add("\\newpage");
            for (var line = 1; line <= 35; line++) rows.Add($"\\noindent Page {pageNumber} row {line}\\par");
        }
        rows.Add("\\end{document}"); File.WriteAllText(main, string.Join('\n', rows));
        File.WriteAllText(extra, string.Join('\n', Enumerable.Range(1, 100).Select(i => $"Extra line {i}")));
        var otherDirectory = Path.Combine(output, "other-project"); Directory.CreateDirectory(otherDirectory);
        var other = Path.Combine(otherDirectory, "other.tex"); File.WriteAllText(other, "\\documentclass{article}\n\\begin{document}Other\\end{document}");
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var settings = new AppSettings { LivePreview = false, Engine = TexEngine.Tectonic, EnginePath = engine, SynctexPath = Path.Combine(directory, "missing.exe") };
        window.GetType().GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, settings);
        await CallAsync(window, "OpenAsync", main);
        var workArea = SystemParameters.WorkArea;
        var width = Math.Max(window.MinWidth, Math.Min(1160, workArea.Width - 32));
        var height = Math.Max(window.MinHeight, Math.Min(760, workArea.Height - 32));
        var expectedBounds = new Rect(workArea.Left + Math.Min(40, Math.Max(0, workArea.Width - width) / 2),
            workArea.Top + Math.Min(40, Math.Max(0, workArea.Height - height) / 2), width, height);
        window.WindowState = WindowState.Normal;
        window.Width = width; window.Height = height; window.Left = expectedBounds.Left; window.Top = expectedBounds.Top;
        if ((bool)Field(window, "_workspaceExpanded")!) Invoke(window, "ToggleWorkspace");
        window.UpdateLayout();
        await CallAsync(window, "BuildAsync");
        var preview = (PdfPreview)window.FindName("Preview");
        if (preview.PageCount != 5) throw new Exception("Reading history fixture requires five PDF pages");
        Invoke(window, "OpenExplorerFile", extra);
        await WaitUntilAsync(() => ((TabWorkspace)Field(window, "_workspaceTabs")!).Owner(extra) is not null, "Extra source did not open");
        var model = (TabWorkspace)Field(window, "_workspaceTabs")!;
        Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, TabSide.Left, 0, false);
        Invoke(window, "MoveWorkspaceTab", main, TabSide.Right, 0, false);
        Invoke(window, "MoveWorkspaceTab", extra, TabSide.Right, 1, false);
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, extra);
        var right = (LatexEditor)window.FindName("RightEditor");
        right.CaretOffset = right.Document.GetLineByNumber(70).Offset;
        right.ScrollToVerticalOffset(500);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var extraCaret = right.CaretOffset; var extraScroll = right.VerticalOffset;
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, main);
        right.CaretOffset = right.Document.GetLineByNumber(25).Offset;
        right.ScrollToVerticalOffset(250);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var mainCaret = right.CaretOffset; var mainScroll = right.VerticalOffset;
        Invoke(window, "CancelPendingNavigation");
        var project = (TexProject)Field(window, "_project")!; var pdf = (string)Field(window, "_pdfPath")!;
        var point = await new SyncTexService().ForwardViewportAsync(project, pdf, new SourceLocation(main, 145), settings) ?? throw new Exception("Missing preview anchor");
        preview.SetZoom(1.25); preview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(window); var page = preview.VisiblePage;
        await CallAsync(window, "OpenAsync", other);
        var saved = ProjectReadingStore.Load(main) ?? throw new Exception("The first project did not save its reading state");
        var savedBounds = new Rect(saved.WindowLeft, saved.WindowTop, saved.WindowWidth, saved.WindowHeight);
        window.Width = Math.Max(window.MinWidth, width - 96); window.Height = Math.Max(window.MinHeight, height - 64);
        if (!(bool)Field(window, "_workspaceExpanded")!) Invoke(window, "ToggleWorkspace");
        window.UpdateLayout();
        await CallAsync(window, "OpenAsync", main);
        await WaitUntilAsync(() => preview.PageCount == 5 && Field(window, "_pendingPreviewPosition") is null, "Cached preview position was not restored");
        await Task.Delay(400);
        if (model.Owner(TabWorkspace.PreviewKey) != TabSide.Left || model.Owner(main) != TabSide.Right || model.Owner(extra) != TabSide.Right || model.Active(TabSide.Right) != main)
            throw new Exception("Project switch did not restore tab groups and selected source");
        var actualBounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        static bool Near(Rect a, Rect b) => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 &&
            Math.Abs(a.Width - b.Width) <= 2 && Math.Abs(a.Height - b.Height) <= 2;
        var expanded = (bool)Field(window, "_workspaceExpanded")!;
        var layout = $"requested={expectedBounds}, saved={savedBounds}, rendered={actualBounds}, work area={workArea}, " +
            $"minimum={window.MinWidth}x{window.MinHeight}, saved maximized={saved.Maximized}, saved fullscreen={saved.Fullscreen}, " +
            $"state={window.WindowState}, saved sidebar expanded={saved.SidebarExpanded}, actual sidebar expanded={expanded}";
        if (!Near(expectedBounds, savedBounds) || !Near(savedBounds, actualBounds) || saved.Maximized || saved.Fullscreen ||
            window.WindowState != WindowState.Normal || saved.SidebarExpanded || expanded)
            throw new Exception("Project switch did not restore sidebar or window layout: " + layout);
        Console.WriteLine("Restored workspace geometry: " + layout);
        if (right.CaretOffset != mainCaret || Math.Abs(right.VerticalOffset - mainScroll) > 3 || preview.VisiblePage != page || Math.Abs(preview.Zoom - 1.25) > .01)
            throw new Exception($"Project switch lost main or preview position: caret {right.CaretOffset}/{mainCaret}, scroll {right.VerticalOffset:F1}/{mainScroll:F1}, page {preview.VisiblePage}/{page}, zoom {preview.Zoom:F2}");
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, extra);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (right.CaretOffset != extraCaret || Math.Abs(right.VerticalOffset - extraScroll) > 3)
            throw new Exception("Inactive source position was not restored");
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, main);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Invoke(window, "CancelPendingNavigation");
        preview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(window);
        await Task.Delay(250);
        if (preview.VisiblePage != page) throw new Exception("Fixture could not establish the intended PDF page before closing");
        await CloseAndObserveAsync(window, "reading history project close");
        File.Delete(extra);
        var reopened = new MainWindow(main) { Width = 900, Height = 700, Left = 80, Top = 80, WindowStartupLocation = WindowStartupLocation.Manual };
        reopened.Show();
        await WaitUntilAsync(() => Field(reopened, "_project") is TexProject && !(bool)Field(reopened, "_transitioning")!, "New window did not reopen project");
        var reopenedPreview = (PdfPreview)reopened.FindName("Preview");
        await WaitUntilAsync(() => reopenedPreview.PageCount == 5 && Field(reopened, "_pendingPreviewPosition") is null, "New window did not restore PDF anchor");
        await Task.Delay(400);
        var reopenedModel = (TabWorkspace)Field(reopened, "_workspaceTabs")!;
        var reopenedEditor = (LatexEditor)reopened.FindName("RightEditor");
        if (reopenedModel.Owner(extra) is not null || reopenedModel.Owner(main) != TabSide.Right || reopenedPreview.VisiblePage != page || reopenedEditor.CaretOffset != mainCaret)
            throw new Exception($"Restart mismatch: extra={reopenedModel.Owner(extra)}, main={reopenedModel.Owner(main)}, page={reopenedPreview.VisiblePage}/{page}, caret={reopenedEditor.CaretOffset}/{mainCaret}");
        Capture(reopened, Path.Combine(output, "reading-history-restored.png"));
        reopenedEditor.TextArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        await reopened.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if ((bool)Field(reopened, "_followAwaitingSourceInteraction")!) throw new Exception("User source interaction did not resume following after restore");
        Invoke(reopened, "CancelPendingNavigation");
        reopenedPreview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(reopened);
        async Task ReopenWithoutCachedPdf()
        {
            await CallAsync(reopened, "OpenAsync", other);
            // This fixture can publish main.pdf in both the shared and the per-entry preview directory.
            var cache = Path.Combine(directory, ".texeed", "preview");
            foreach (var cached in Directory.EnumerateFiles(cache, "main.pdf", SearchOption.AllDirectories)) File.Delete(cached);
            await CallAsync(reopened, "OpenAsync", main);
            if (reopenedPreview.PageCount != 0 || Field(reopened, "_pendingPreviewPosition") is null)
                throw new Exception("Missing cached PDF did not retain a pending position");
        }
        await ReopenWithoutCachedPdf();
        Invoke(reopened, "SetManualZoom", 1.6);
        await CallAsync(reopened, "BuildAsync");
        await WaitUntilAsync(() => reopenedPreview.PageCount == 5 && Field(reopened, "_pendingPreviewPosition") is null, "First build did not restore pending preview position");
        if (reopenedPreview.VisiblePage != page || Math.Abs(reopenedPreview.Zoom - 1.6) > .01)
            throw new Exception("First build discarded saved preview page or user-selected zoom");
        Invoke(reopened, "MoveWorkspaceTab", main, TabSide.Left, int.MaxValue, false);
        Invoke(reopened, "SelectWorkspaceTab", TabSide.Left, main);
        if (reopenedPreview.IsVisible) throw new Exception("Hidden preview fixture did not hide preview");
        await CallAsync(reopened, "OpenAsync", other);
        await CallAsync(reopened, "OpenAsync", main);
        if (reopenedPreview.IsVisible || Field(reopened, "_pendingPreviewPosition") is null)
            throw new Exception("Hidden preview position was not retained until the tab becomes visible");
        Invoke(reopened, "SelectWorkspaceTab", TabSide.Left, TabWorkspace.PreviewKey);
        await WaitUntilAsync(() => Field(reopened, "_pendingPreviewPosition") is null && reopenedPreview.IsVisible, "Hidden preview did not restore when shown");
        if (reopenedPreview.VisiblePage != page) throw new Exception("Hidden preview lost its restored page");

        Invoke(reopened, "MoveWorkspaceTab", main, TabSide.Right, int.MaxValue, true);
        Invoke(reopened, "CancelPendingNavigation");
        reopenedPreview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(reopened);
        await ReopenWithoutCachedPdf();
        reopenedEditor = (LatexEditor)reopened.FindName("RightEditor");
        reopenedEditor.TextArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        reopenedEditor.CaretOffset = reopenedEditor.Document.GetLineByNumber(5).Offset;
        reopenedEditor.ScrollToVerticalOffset(0);
        await reopened.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (Field(reopened, "_pendingPreviewPosition") is not null) throw new Exception("Source interaction did not cancel deferred PDF position");
        await CallAsync(reopened, "BuildAsync");
        await WaitUntilAsync(() => reopenedPreview.PageCount == 5 && reopenedPreview.VisiblePage == 1, "First build overrode source interaction with old preview position");

        Invoke(reopened, "CancelPendingNavigation");
        reopenedPreview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(reopened);
        await ReopenWithoutCachedPdf();
        Invoke(reopened, "Outline_NavigateRequested", reopened, new SourceLocation(main, 5));
        await reopened.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (Field(reopened, "_pendingPreviewPosition") is not null || (bool)Field(reopened, "_followAwaitingSourceInteraction")!)
            throw new Exception("Outline navigation did not cancel deferred PDF position");
        await CallAsync(reopened, "BuildAsync");
        await WaitUntilAsync(() => reopenedPreview.PageCount == 5 && reopenedPreview.VisiblePage == 1, "First build overrode outline navigation");

        Invoke(reopened, "CancelPendingNavigation");
        reopenedPreview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(reopened);
        await ReopenWithoutCachedPdf();
        await CallAsync(reopened, "ForwardAsync", true);
        if (Field(reopened, "_pendingPreviewPosition") is not null || (bool)Field(reopened, "_followAwaitingSourceInteraction")!)
            throw new Exception("Explicit forward navigation did not cancel deferred PDF position");
        await CallAsync(reopened, "BuildAsync");
        await WaitUntilAsync(() => reopenedPreview.PageCount == 5 && reopenedPreview.VisiblePage == 1, "First build overrode explicit source navigation");
        var nestedDirectory = Path.Combine(directory, "independent"); Directory.CreateDirectory(nestedDirectory);
        var nested = Path.Combine(nestedDirectory, "nested.tex");
        File.WriteAllText(nested, "\\documentclass{article}\n\\begin{document}Nested\\end{document}");
        await CallAsync(reopened, "OpenAsync", nested);
        if (((TexProject)Field(reopened, "_project")!).Root != directory || reopenedModel.Owner(TabWorkspace.PreviewKeyFor(nested)) is null)
            throw new Exception("The default open command did not add a nested entry to the current project");
        if (reopened.FindName("OpenSeparateProjectMenuItem") is not MenuItem)
            throw new Exception("The independent-project menu entry is unavailable");
        await CallAsync(reopened, "OpenSeparateProjectAsync", nested);
        if (((TexProject)Field(reopened, "_project")!).Root != nestedDirectory || ((TexProject)Field(reopened, "_project")!).MainPath != nested)
            throw new Exception("Opening a nested file as an independent project kept the parent project");
        await CallAsync(reopened, "OpenSeparateProjectAsync", main);
        var recent = (MenuItem)reopened.FindName("RecentMenu");
        var nestedRecent = recent.Items.OfType<MenuItem>().First(item => Equals(item.Header, nested));
        nestedRecent.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(() => Field(reopened, "_project") is TexProject current && current.Root == nestedDirectory && !(bool)Field(reopened, "_transitioning")!, "Recent-project selection did not open the nested project independently");
        await CloseAndObserveAsync(reopened, "reading history reopened window");
        Console.WriteLine("PASS: per-project layout and positions, restart, hidden preview, missing cache/source, and pending PDF cancellation after user source or outline navigation");
    }
}
