using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
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
        var other = Path.Combine(output, "other.tex"); File.WriteAllText(other, "\\documentclass{article}\n\\begin{document}Other\\end{document}");
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var settings = new AppSettings { LivePreview = false, Engine = TexEngine.Tectonic, EnginePath = engine, SynctexPath = Path.Combine(directory, "missing.exe") };
        window.GetType().GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, settings);
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync");
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
        Invoke(window, "ToggleWorkspace");
        window.Width = 1160; window.Height = 760; window.Left = 40; window.Top = 40; window.UpdateLayout();
        await CallAsync(window, "OpenAsync", other); await CallAsync(window, "OpenAsync", main);
        await WaitUntilAsync(() => preview.PageCount == 5 && Field(window, "_pendingPreviewPosition") is null, "Cached preview position was not restored");
        await Task.Delay(400);
        if (model.Owner(TabWorkspace.PreviewKey) != TabSide.Left || model.Owner(main) != TabSide.Right || model.Owner(extra) != TabSide.Right || model.Active(TabSide.Right) != main)
            throw new Exception("Project switch did not restore tab groups and selected source");
        if ((bool)Field(window, "_workspaceExpanded")! || Math.Abs(window.Width - 1160) > 2 || Math.Abs(window.Height - 760) > 2)
            throw new Exception("Project switch did not restore sidebar or window layout");
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
        reopenedEditor.TextArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        await reopened.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if ((bool)Field(reopened, "_followAwaitingSourceInteraction")!) throw new Exception("User source interaction did not resume following after restore");
        Invoke(reopened, "CancelPendingNavigation");
        reopenedPreview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(reopened);
        await CallAsync(reopened, "OpenAsync", other);
        File.Delete(pdf);
        await CallAsync(reopened, "OpenAsync", main);
        if (reopenedPreview.PageCount != 0 || Field(reopened, "_pendingPreviewPosition") is null)
            throw new Exception("Missing cached PDF did not retain a pending position");
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
        await CallAsync(reopened, "OpenAsync", other);
        File.Delete(pdf);
        await CallAsync(reopened, "OpenAsync", main);
        if (Field(reopened, "_pendingPreviewPosition") is null) throw new Exception("User interaction fixture has no deferred PDF position");
        reopenedEditor = (LatexEditor)reopened.FindName("RightEditor");
        reopenedEditor.TextArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        reopenedEditor.CaretOffset = reopenedEditor.Document.GetLineByNumber(5).Offset;
        reopenedEditor.ScrollToVerticalOffset(0);
        await reopened.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (Field(reopened, "_pendingPreviewPosition") is not null) throw new Exception("Source interaction did not cancel deferred PDF position");
        await CallAsync(reopened, "BuildAsync");
        await WaitUntilAsync(() => reopenedPreview.PageCount == 5 && reopenedPreview.VisiblePage == 1, "First build overrode source interaction with old preview position");

        Invoke(reopened, "CancelPendingNavigation");
        reopenedPreview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(reopened);
        await CallAsync(reopened, "OpenAsync", other);
        File.Delete(pdf);
        await CallAsync(reopened, "OpenAsync", main);
        Invoke(reopened, "Outline_NavigateRequested", reopened, new SourceLocation(main, 5));
        await reopened.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (Field(reopened, "_pendingPreviewPosition") is not null || (bool)Field(reopened, "_followAwaitingSourceInteraction")!)
            throw new Exception("Outline navigation did not cancel deferred PDF position");
        await CallAsync(reopened, "BuildAsync");
        await WaitUntilAsync(() => reopenedPreview.PageCount == 5 && reopenedPreview.VisiblePage == 1, "First build overrode outline navigation");

        Invoke(reopened, "CancelPendingNavigation");
        reopenedPreview.ShowLocation(point, highlight: false, viewportFraction: .5);
        await WaitForPreviewRenderedAsync(reopened);
        await CallAsync(reopened, "OpenAsync", other);
        File.Delete(pdf);
        await CallAsync(reopened, "OpenAsync", main);
        await CallAsync(reopened, "ForwardAsync", true);
        if (Field(reopened, "_pendingPreviewPosition") is not null || (bool)Field(reopened, "_followAwaitingSourceInteraction")!)
            throw new Exception("Explicit forward navigation did not cancel deferred PDF position");
        await CallAsync(reopened, "BuildAsync");
        await WaitUntilAsync(() => reopenedPreview.PageCount == 5 && reopenedPreview.VisiblePage == 1, "First build overrode explicit source navigation");
        await CloseAndObserveAsync(reopened, "reading history reopened window");
        Console.WriteLine("PASS: per-project layout and positions, restart, hidden preview, missing cache/source, and pending PDF cancellation after user source or outline navigation");
    }
}
