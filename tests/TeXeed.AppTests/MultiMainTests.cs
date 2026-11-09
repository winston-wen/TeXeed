using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckMultiMainAsync(MainWindow window, string root, string output)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var folder = Path.Combine(output, "multi-main-project"); Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "main.tex"); var b = Path.Combine(folder, "paper.tex");
        var shared = Path.Combine(folder, "shared.tex"); var ownA = Path.Combine(folder, "only-a.tex"); var ownB = Path.Combine(folder, "only-b.tex");
        File.WriteAllText(shared, "Shared words.\n"); File.WriteAllText(ownA, "Only A.\n"); File.WriteAllText(ownB, "Only B.\n");
        File.WriteAllText(a, "\\documentclass{article}\n\\begin{document}\nA: \\input{shared} \\input{only-a}\n\\end{document}\n");
        File.WriteAllText(b, "\\documentclass{article}\n\\begin{document}\nB: \\input{shared} \\input{only-b}\n\\newpage Second B page.\n\\end{document}\n");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var settings = new AppSettings { Engine = TexEngine.Tectonic, EnginePath = Path.Combine(root, ".tools", "tectonic", "tectonic.exe"), LivePreview = false, FollowMode = PreviewFollowMode.None };
        typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, settings);
        await CallAsync(window, "OpenAsync", a); await CallAsync(window, "BuildAsync");
        Console.WriteLine("multi-main: first entry built");
        var aPdf = (string?)Field(window, "_pdfPath");
        Check(aPdf is not null && File.Exists(aPdf), "Initial main file did not compile");
        Invoke(window, "OpenAiTab");
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        ((TextBox)ai.FindName("Composer")).Text = "待发送的 AI 草稿";
        Invoke(window, "ActivateFile", a);
        var editor = (LatexEditor)window.FindName("LeftEditor"); editor.Document.Insert(0, "% unsaved A\n");
        await CallAsync(window, "OpenProjectEntryAsync", b, false, false, true);
        Console.WriteLine("multi-main: second entry opened");
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        Check(tabs.Owner(a) is not null && tabs.Owner(b) is not null, "Both main source tabs must stay open");
        Check(tabs.Owner(TabWorkspace.PreviewKey) is not null && tabs.Owner(TabWorkspace.PreviewKeyFor(b)) is not null, "Each entry must have a PDF preview tab");
        Check(((Dictionary<string, string>)Field(window, "_changes")!).ContainsKey(a), "Opening another entry discarded the unsaved first source");
        editor.Document.Insert(0, "% unsaved B\n");
        Invoke(window, "ActivateFile", shared); editor.Document.Insert(0, "% unsaved shared\n");
        Invoke(window, "ActivateFile", ownA); editor.Document.Insert(0, "% unsaved A dependency\n");
        Invoke(window, "ActivateFile", ownB); editor.Document.Insert(0, "% unsaved B dependency\n");
        var changes = (Dictionary<string, string>)Field(window, "_changes")!;
        Check(new[] { a, b, shared, ownA, ownB }.All(changes.ContainsKey), "Opening both entries lost shared or independent unsaved buffers");
        Check(((TextBox)ai.FindName("Composer")).Text == "待发送的 AI 草稿", "Opening another entry lost the AI draft");
        Console.WriteLine("multi-main: unsaved edits verified");
        await CallAsync(window, "BuildAsync");
        Console.WriteLine("multi-main: second entry built");
        var bPdf = (string?)Field(window, "_pdfPath");
        Console.WriteLine($"multi-main: aPdf={aPdf}, bPdf={bPdf}, target={Field(window, "_lastCompileEntryPath")}, status={((TextBlock)window.FindName("StatusText")).Text}, changes={((Dictionary<string, string>)Field(window, "_changes")!).Count}");
        Check(bPdf is not null && File.Exists(bPdf) && !string.Equals(aPdf, bPdf, StringComparison.OrdinalIgnoreCase), "The second entry must compile to its own PDF");
        Check(((TexProject)Field(window, "_previewProject")!).MainPath == b, "The second preview was associated with the wrong source");
        Check(new[] { a, b, shared, ownA, ownB }.All(path => File.ReadAllText(path).Contains("% unsaved")), "Compilation did not save all project edits");
        Check(((PdfPreview)window.FindName("Preview")).PageCount == 2, "The second PDF should show its own two-page output");
        var preview = (PdfPreview)window.FindName("Preview");
        preview.SetZoom(1.35); preview.ShowLocation(new PdfLocation(2, 0, 0), highlight: false);
        await WaitForPreviewRenderedAsync(window);
        var bPage = preview.VisiblePage; var bZoom = preview.Zoom;
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKey);
        await WaitUntilAsync(() => string.Equals((string?)Field(window, "_activePreviewEntry"), a, StringComparison.OrdinalIgnoreCase) && !(bool)Field(window, "_switchingEntryPreview")!, "Could not switch back to the first PDF");
        Check(string.Equals((string?)Field(window, "_pdfPath"), aPdf, StringComparison.OrdinalIgnoreCase), "Switching to the first preview showed the wrong PDF");
        Check(((PdfPreview)window.FindName("Preview")).PageCount == 1, "First preview should still show its own one-page PDF");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("main.tex") && ((TextBlock)window.FindName("StatusText")).Text.Contains("过期"), "Preview status described a different entry or hid the stale state");
        preview.SetZoom(1.1); await WaitForPreviewRenderedAsync(window);
        Capture(window, Path.Combine(output, "multi-main-a.png"));
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKeyFor(b));
        await WaitUntilAsync(() => string.Equals((string?)Field(window, "_activePreviewEntry"), b, StringComparison.OrdinalIgnoreCase) && !(bool)Field(window, "_switchingEntryPreview")!, "Could not switch back to the second PDF");
        Check(string.Equals((string?)Field(window, "_pdfPath"), bPdf, StringComparison.OrdinalIgnoreCase), "Switching back showed the wrong PDF");
        await WaitUntilAsync(() => Field(window, "_pendingPreviewPosition") is null, "Second preview position did not restore");
        Check(preview.VisiblePage == bPage && Math.Abs(preview.Zoom - bZoom) < .01, "Second preview lost its own page or zoom");
        var firstBytes = File.ReadAllBytes(aPdf!);
        try
        {
            File.WriteAllText(aPdf!, "not a PDF");
            Invoke(window, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKey);
            await WaitUntilAsync(() => !(bool)Field(window, "_switchingEntryPreview")!, "Invalid-PDF switch did not finish");
            Check(string.Equals((string?)Field(window, "_activePreviewEntry"), b, StringComparison.OrdinalIgnoreCase)
                && string.Equals((string?)Field(window, "_pdfPath"), bPdf, StringComparison.OrdinalIgnoreCase)
                && preview.PageCount == 2, "An unreadable PDF did not roll back to the previously selected preview");
        }
        finally { File.WriteAllBytes(aPdf!, firstBytes); }
        Check(((TextBox)ai.FindName("Composer")).Text == "待发送的 AI 草稿", "Preview switching lost the AI draft");
        await WaitForPreviewRenderedAsync(window);
        Capture(window, Path.Combine(output, "multi-main.png"));
        Console.WriteLine("multi-main: preview switching verified");
        await CloseAndObserveAsync(window, "multi-main project close");
        Console.WriteLine("multi-main: original window closed");
        var reopened = new MainWindow(a) { Width = 1100, Height = 800, Left = 80, Top = 80, WindowStartupLocation = WindowStartupLocation.Manual };
        reopened.Show();
        Console.WriteLine("multi-main: reopening window");
        await WaitUntilAsync(() => Field(reopened, "_project") is TexProject && !(bool)Field(reopened, "_transitioning")!, "Project did not reopen");
        var restoredTabs = (TabWorkspace)Field(reopened, "_workspaceTabs")!;
        Check(restoredTabs.Owner(a) is not null && restoredTabs.Owner(b) is not null && restoredTabs.Owner(TabWorkspace.PreviewKeyFor(b)) is not null, "Restart did not restore both source and preview tabs");
        await WaitUntilAsync(() => !(bool)Field(reopened, "_switchingEntryPreview")!, "Restored preview switch did not finish");
        Invoke(reopened, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKeyFor(b));
        await WaitUntilAsync(() => string.Equals((string?)Field(reopened, "_activePreviewEntry"), b, StringComparison.OrdinalIgnoreCase) && !(bool)Field(reopened, "_switchingEntryPreview")!, "Restart did not restore second preview");
        Check(string.Equals((string?)Field(reopened, "_pdfPath"), bPdf, StringComparison.OrdinalIgnoreCase), "Restart restored the wrong second PDF cache");
        await WaitUntilAsync(() => Field(reopened, "_pendingPreviewPosition") is null, "Restart did not restore second PDF position");
        var reopenedPreview = (PdfPreview)reopened.FindName("Preview");
        Check(reopenedPreview.VisiblePage == bPage && Math.Abs(reopenedPreview.Zoom - bZoom) < .01, "Restart lost the second PDF page or zoom");
        Invoke(reopened, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKey);
        await WaitUntilAsync(() => string.Equals((string?)Field(reopened, "_activePreviewEntry"), a, StringComparison.OrdinalIgnoreCase) && !(bool)Field(reopened, "_switchingEntryPreview")!, "Restart did not restore first preview");
        Check(string.Equals((string?)Field(reopened, "_pdfPath"), aPdf, StringComparison.OrdinalIgnoreCase) && ((PdfPreview)reopened.FindName("Preview")).PageCount == 1, "Restart restored the wrong first PDF cache");
        await WaitUntilAsync(() => Field(reopened, "_pendingPreviewPosition") is null, "Restart did not restore first PDF position");
        Check(Math.Abs(reopenedPreview.Zoom - 1.1) < .01, "Restart lost the first PDF zoom");
        Invoke(reopened, "InvalidateCompileInputTracking");
        Invoke(reopened, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKeyFor(b));
        await WaitUntilAsync(() => string.Equals((string?)Field(reopened, "_activePreviewEntry"), b, StringComparison.OrdinalIgnoreCase) && !(bool)Field(reopened, "_switchingEntryPreview")!, "Could not switch to second preview after watcher overflow");
        Check((int)Field(reopened, "_pdfRevision")! < 0, "Watcher overflow left the inactive entry's PDF marked current");
        await CallAsync(reopened, "RenameExplorerFileAsync", b, "paper-renamed.tex");
        var renamed = Path.Combine(folder, "paper-renamed.tex");
        Check(restoredTabs.Owner(TabWorkspace.PreviewKeyFor(renamed)) is not null && restoredTabs.Owner(TabWorkspace.PreviewKeyFor(b)) is null
            && string.Equals((string?)Field(reopened, "_activePreviewEntry"), renamed, StringComparison.OrdinalIgnoreCase), "Renaming a main file did not retarget its preview tab");
        var restoredAi = (AiPanel)Field(reopened, "_aiPanel")!;
        Check(((TextBox)restoredAi.FindName("Composer")).Text == "待发送的 AI 草稿", "Restart lost the AI draft");
        await CloseAndObserveAsync(reopened, "reopened multi-main project");
        Console.WriteLine($"PASS: {checks} multi-entry checks for independent PDFs, shared and private unsaved edits, preview switching, AI draft and restart restoration.");
    }
}
