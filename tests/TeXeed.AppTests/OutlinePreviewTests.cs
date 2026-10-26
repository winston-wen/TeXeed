using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckOutlinePreviewAsync(MainWindow window, string output)
    {
        var checks = 0; void Check(bool ok, string error) { checks++; if (!ok) throw new Exception(error); }
        void Set(string name, object value) => window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        var settings = (AppSettings)Field(window, "_settings")!;
        const string engine = "C:/texlive/2026/bin/windows/xelatex.exe";
        Check(File.Exists(engine), "Outline preview test requires installed XeLaTeX");
        Set("_settings", settings with { Engine = TexEngine.XeLaTeX, EnginePath = engine, LivePreview = false, FollowMode = PreviewFollowMode.None,
            SynctexPath = Path.Combine(output, "missing-synctex.exe") });
        var folder = Path.Combine(output, "outline-preview-project"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex"); var child = Path.Combine(folder, "middle.tex");
        const string middle = "\\section{Middle}\n\\noindent Target in the included source.\\par\n";
        File.WriteAllText(child, middle);
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\n\\section{Start}\nFirst page.\\par\n\\newpage\n\\input{middle}\n\\newpage\n\\section{Last}\nLast page.\\par\n\\end{document}\n");
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync");
        var preview = (PdfPreview)window.FindName("Preview"); await WaitForPreviewRenderedAsync(window);
        Check(preview.PageCount == 3 && (int)Field(window, "_pdfRevision")! == (int)Field(window, "_revision")!, "Fixture did not compile a current three-page preview");
        var project = (TexProject)Field(window, "_project")!; var pdf = (string)Field(window, "_pdfPath")!;
        var location = new SourceLocation(child, 1);
        var expected = await new SyncTexService().ForwardAsync(project, pdf, location, (AppSettings)Field(window, "_settings")!);
        Check(expected?.Page == 2, "Included heading has no page-two SyncTeX mapping");
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var compileTask = Field(window, "_compileTask"); var pdfTime = File.GetLastWriteTimeUtc(pdf);
        void CloseSources()
        {
            foreach (var path in tabs.Sources.ToArray()) Invoke(window, "CloseTabs", path, TabCloseScope.This);
            window.UpdateLayout();
        }
        Button TargetHeading()
        {
            var tree = (TreeView)((DocumentOutline)window.FindName("OutlineTree")).FindName("Tree"); window.UpdateLayout();
            var item = Descendants<TreeViewItem>(tree).Single(v => v.Header is OutlineNode node && node.Entry.Path == child);
            return (Button)item.Template.FindName("HeadingButton", item);
        }
        CloseSources(); await WaitForPreviewRenderedAsync(window);
        Check(!tabs.Sources.Any() && preview.IsVisible, "Fixture is not in preview-only layout");
        foreach (var mode in new[] { PreviewFollowMode.None, PreviewFollowMode.Cursor, PreviewFollowMode.Viewport })
        {
            Invoke(window, "ChangeFollowMode", mode);
            preview.ShowLocation(new PdfLocation(1, 0, 0), highlight: false); window.UpdateLayout();
            DoubleClickHeading(window, TargetHeading());
            await WaitUntilAsync(() => Field(preview, "_highlight") is PdfLocation point && point.Page == 2, "Outline double-click did not locate PDF in " + mode);
            Check(!tabs.Sources.Any() && !((LatexEditor)window.FindName("LeftEditor")).IsVisible && !((LatexEditor)window.FindName("RightEditor")).IsVisible,
                "Outline navigation reopened an editor in " + mode);
            Check(ReferenceEquals(compileTask, Field(window, "_compileTask")) && File.GetLastWriteTimeUtc(pdf) == pdfTime, "Direct outline navigation recompiled the project");
        }
        // Manual preview movement and a second double-click must re-run navigation.
        preview.ShowLocation(new PdfLocation(3, 0, 0), highlight: false);
        DoubleClickHeading(window, TargetHeading());
        await WaitUntilAsync(() => Field(preview, "_highlight") is PdfLocation point && point.Page == 2, "Repeated outline double-click did not resynchronize the preview");
        Check(!tabs.Sources.Any(), "Repeated preview navigation changed the layout");
        Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, TabSide.Left, 0, false);
        Check(await (Task<bool>)Invoke(window, "TryNavigateOutlinePreviewAsync", location)!, "Preview on the left cannot navigate directly");
        Capture(window, Path.Combine(output, "outline-direct-preview.png"));
        // Stale or failed previews retain the established source-navigation path.
        Set("_pdfRevision", -1); Invoke(window, "Outline_NavigateRequested", window, location); window.UpdateLayout();
        Check(tabs.Sources.Contains(child), "Stale preview bypassed source navigation");
        CloseSources(); Set("_pdfRevision", Field(window, "_revision")!); Set("_previewWarning", true);
        Invoke(window, "Outline_NavigateRequested", window, location); window.UpdateLayout();
        Check(tabs.Sources.Contains(child), "A failed-build warning was treated as a valid preview");
        CloseSources(); Set("_previewWarning", false);
        // Verify content equality even when a filesystem event has not arrived.
        var watcher = (FileSystemWatcher)Field(window, "_watcher")!; watcher.EnableRaisingEvents = false;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(child); File.WriteAllText(child, middle.Replace("Target", "Change")); File.SetLastWriteTimeUtc(child, stamp);
            Check(!await (Task<bool>)Invoke(window, "TryNavigateOutlinePreviewAsync", location)!, "Changed source with a preserved timestamp passed the input-snapshot check");
            Check(!tabs.Sources.Any(), "Eligibility checking itself opened a source tab");
            File.WriteAllText(child, middle); File.SetLastWriteTimeUtc(child, stamp);
        }
        finally { watcher.EnableRaisingEvents = true; }
        await CallAsync(window, "OpenAsync", main); CloseSources(); await WaitForPreviewRenderedAsync(window);
        Check(await (Task<bool>)Invoke(window, "TryNavigateOutlinePreviewAsync", location)!, "A restored current build cache cannot navigate directly");
        // When an editor is visible, preserve normal source navigation.
        Invoke(window, "ActivateFile", main); window.UpdateLayout();
        Check(!await (Task<bool>)Invoke(window, "TryNavigateOutlinePreviewAsync", location)!, "Direct-preview routing changed the editor-visible workflow");
        Invoke(window, "Outline_NavigateRequested", window, location); window.UpdateLayout();
        Check(((LatexEditor)window.FindName("Editor")).CurrentFilePath == child, "Editor-visible outline navigation no longer selects its source");
        Console.WriteLine($"Outline direct preview: {checks} checks passed (double-click, all follow modes, included source, current cache, stale/failed inputs and layout preservation).");
    }
}
