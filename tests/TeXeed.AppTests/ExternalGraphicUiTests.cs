using System.Diagnostics;
using System.IO;
using System.Reflection;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task<(string Root, string Source, string PdfSource, string Main)> PrepareGraphicFixtureAsync(string output)
    {
        var root = Path.Combine(output, "external-graphic-project"); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "fig.tex"); var pdfSource = Path.Combine(root, "multipage.tex"); var main = Path.Combine(root, "main.tex");
        File.WriteAllText(source, "\\documentclass{article}\n\\pagestyle{empty}\n\\begin{document}\n\\begin{picture}(100,200)\n\\put(0,180){Top}\n\\put(0,100){Middle}\n\\put(0,20){Bottom}\n\\end{picture}\n\\end{document}\n");
        File.WriteAllText(pdfSource, "\\documentclass{article}\n\\begin{document}\nFirst page only.\n\\newpage\nSecond page line one.\\par\nSecond page line two.\\par\nSecond page line three.\\par\n\\end{document}\n");
        File.WriteAllText(main, "\\documentclass{article}\n\\usepackage{graphicx}\n\\begin{document}\nAbove text.\n\\includegraphics[width=3cm]{fig.eps}\n\\newpage\n\\includegraphics[page=2,width=8cm]{multipage.pdf}\n\\end{document}\n");
        async Task RunAsync(string exe, params string[] arguments)
        {
            using var process = new Process { StartInfo = new(exe) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception($"{exe} fixture generation failed: {await stdout} {await stderr}");
        }
        await RunAsync("latex", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "fig.tex");
        await RunAsync("dvips", "-E", "-o", "fig.eps", "fig.dvi");
        await RunAsync("xelatex", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "multipage.tex");
        return (root, source, pdfSource, main);
    }

    private static async Task CheckExternalGraphicUiAsync(MainWindow window, string output)
    {
        var (root, source, pdfSource, main) = await PrepareGraphicFixtureAsync(output);
        var settings = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        settings.SetValue(window, new AppSettings { LivePreview = false, FollowMode = PreviewFollowMode.None, Engine = TexEngine.XeLaTeX, SynctexPath = Path.Combine(root, "missing.exe") });
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync");
        var project = (TexProject)Field(window, "_project")!; var pdf = (string?)Field(window, "_pdfPath") ?? throw new Exception("Main PDF did not compile");
        if (!project.Files.Any(f => f.IsEditable && f.Path.Equals(source, StringComparison.OrdinalIgnoreCase))) throw new Exception("Verified graphic source was not added to editable project");
        var sync = (SyncTexService)Field(window, "_sync")!; var appSettings = (AppSettings)settings.GetValue(window)!;
        var first = await sync.ForwardViewportAsync(project, pdf, new(source, 5), appSettings);
        var last = await sync.ForwardViewportAsync(project, pdf, new(source, 7), appSettings);
        if (first is null || last is null || last.Y <= first.Y + 30) throw new Exception("External EPS source rows did not map to separate locations");
        if (!project.Files.Any(f => f.IsEditable && f.Path.Equals(pdfSource, StringComparison.OrdinalIgnoreCase))) throw new Exception("Verified PDF graphic source was not added to editable project");
        var pdfFirst = await sync.ForwardViewportAsync(project, pdf, new(pdfSource, 5), appSettings);
        var pdfLast = await sync.ForwardViewportAsync(project, pdf, new(pdfSource, 7), appSettings);
        if (pdfFirst is null || pdfLast is null || pdfFirst.Page != 2 || pdfLast.Page != 2 || pdfLast.Y <= pdfFirst.Y + 3)
            throw new Exception($"Second page of an embedded PDF did not map its source rows to separate positions: {pdfFirst} / {pdfLast}; page size={sync.PdfPageSize?.Invoke(Path.Combine(root, "multipage.pdf"), 2)}");
        await CallAsync(window, "InverseAsync", pdfFirst);
        var editor = (LatexEditor)window.FindName("Editor");
        if (!string.Equals(editor.CurrentFilePath, pdfSource, StringComparison.OrdinalIgnoreCase) || Math.Abs(editor.CurrentLocation!.Line - 5) > 1)
            throw new Exception("Inverse PDF graphic click did not open its corresponding source row");
        await CallAsync(window, "InverseAsync", first);
        if (!string.Equals(editor.CurrentFilePath, source, StringComparison.OrdinalIgnoreCase) || Math.Abs(editor.CurrentLocation!.Line - 5) > 1)
            throw new Exception("Inverse EPS click did not open the corresponding editable source row");
        editor.Text += "\n% Changed drawing source\n";
        if ((int)Field(window, "_revision")! == (int)Field(window, "_pdfRevision")!) throw new Exception("Editing drawing source did not invalidate preview");
        await CallAsync(window, "BuildAsync");
        project = (TexProject)Field(window, "_project")!; pdf = (string)Field(window, "_pdfPath")!;
        var inverse = await sync.InverseAsync(project, pdf, first, appSettings);
        if (inverse is not null && inverse.Path.Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Compiling the main document silently reused stale EPS mapping after graphic source changed");
        Console.WriteLine("PASS: EPS and page 2 of PDF graphics open their sources; EPS edits retire its mapping even after main document builds");
    }
}
