using TeXeed.Core;
namespace TeXeed.Tests;
public static class ExternalGraphicTests
{
    public static async Task GenerateAndRunAsync(string root, string xeLatex)
    {
        Directory.CreateDirectory(root);
        var fixtures = Path.GetFullPath("tests/fixtures/external-graphics");
        foreach (var name in new[] { "fig.tex", "main.tex", "multipage.tex", "mainpdf.tex", "flow.tex", "mainflow.tex", "table.tex", "maintable.tex" }) File.Copy(Path.Combine(fixtures, name), Path.Combine(root, name), true);
        var tools = Path.GetDirectoryName(xeLatex)!;
        async Task RunAt(string working, string exe, params string[] args)
        {
            using var process = new System.Diagnostics.Process { StartInfo = new(Path.Combine(tools, exe)) { WorkingDirectory = working, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Check.True(process.ExitCode == 0, $"External graphic fixture build failed: {await stdout} {await stderr}");
        }
        Task Run(string exe, params string[] args) => RunAt(root, exe, args);
        await Run("latex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "fig.tex");
        await Run("dvips.exe", "-E", "-o", "fig.eps", "fig.dvi");
        await Run("xelatex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "main.tex");
        await Run("xelatex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "multipage.tex");
        await Run("xelatex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "mainpdf.tex");
        await Run("latex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "flow.tex");
        await Run("dvips.exe", "-E", "-o", "flow.eps", "flow.dvi");
        var flowEps = Path.Combine(root, "flow.eps");
        var epsText = File.ReadAllText(flowEps);
        // dvips -E does not include TikZ PostScript specials in its guessed box.
        epsText = System.Text.RegularExpressions.Regex.Replace(epsText, @"(?m)^%%BoundingBox:.*$", "%%BoundingBox: 148 423 210 670");
        File.WriteAllText(flowEps, epsText);
        await Run("xelatex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "mainflow.tex");
        var flowSource = Path.Combine(root, "flow.tex"); var flowMain = Path.Combine(root, "mainflow.tex");
        var flowProject = new TexProject(root, flowMain, [new(flowMain,"mainflow.tex",ProjectFileKind.Tex,File.ReadAllText(flowMain)), new(flowSource,"flow.tex",ProjectFileKind.Tex,File.ReadAllText(flowSource)), new(Path.Combine(root,"flow.eps"),"flow.eps",ProjectFileKind.Asset, "")], []);
        var flowSync = new SyncTexService(); var flowSettings = new AppSettings { SynctexPath = Path.Combine(root, "missing.exe") };
        var flowFirst = await flowSync.ForwardViewportAsync(flowProject, Path.Combine(root, "mainflow.pdf"), new(flowSource, 6), flowSettings);
        var flowLast = await flowSync.ForwardViewportAsync(flowProject, Path.Combine(root, "mainflow.pdf"), new(flowSource, 10), flowSettings);
        Check.True(flowFirst is not null && flowLast is not null && flowLast.Y > flowFirst.Y + 10, $"EPS flowchart positions did not progress: {flowFirst} / {flowLast}");
        var flowBack = await flowSync.InverseAsync(flowProject, Path.Combine(root, "mainflow.pdf"), flowLast!, flowSettings);
        Check.True(flowBack is not null && flowBack.Path == flowSource && Math.Abs(flowBack.Line - 10) <= 1, $"EPS flowchart inverse mismatch: {flowBack}");
        await Run("xelatex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "table.tex");
        await Run("xelatex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "maintable.tex");
        var tableSource = Path.Combine(root, "table.tex"); var tableMain = Path.Combine(root, "maintable.tex");
        var tableProject = new TexProject(root, tableMain, [new(tableMain,"maintable.tex",ProjectFileKind.Tex,File.ReadAllText(tableMain)), new(tableSource,"table.tex",ProjectFileKind.Tex,File.ReadAllText(tableSource)), new(Path.Combine(root,"table.pdf"),"table.pdf",ProjectFileKind.Asset, "")], []);
        var tableSync = new SyncTexService { PdfPageSize = (_, _) => (612, 792) };
        var tableFirst = await tableSync.ForwardViewportAsync(tableProject, Path.Combine(root, "maintable.pdf"), new(tableSource, 60), flowSettings);
        var tableLast = await tableSync.ForwardViewportAsync(tableProject, Path.Combine(root, "maintable.pdf"), new(tableSource, 70), flowSettings);
        Check.True(tableFirst is not null && tableLast is not null && tableLast.Y > tableFirst.Y + 5, $"Embedded longtable page 2 did not advance: {tableFirst} / {tableLast}");
        var tableBack = await tableSync.InverseAsync(tableProject, Path.Combine(root, "maintable.pdf"), tableFirst!, flowSettings);
        Check.True(tableBack is not null && tableBack.Path == tableSource && Math.Abs(tableBack.Line - 60) <= 1, $"Embedded longtable inverse mismatch: {tableBack}");
        await RunAsync(root);
        var sub = Path.Combine(root, "sub"); Directory.CreateDirectory(sub);
        File.Copy(Path.Combine(root, "fig.tex"), Path.Combine(sub, "fig.tex"), true);
        await RunAt(sub, "latex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "fig.tex");
        await RunAt(sub, "dvips.exe", "-E", "-o", "fig.eps", "fig.dvi");
        var subMain = Path.Combine(root, "submain.tex");
        File.WriteAllText(subMain, File.ReadAllText(Path.Combine(root, "main.tex")).Replace("\\begin{document}", "\\graphicspath{{sub/}}\n\\begin{document}"));
        await Run("xelatex.exe", "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", "submain.tex");
        var subSource = Path.Combine(sub, "fig.tex");
        var project = new TexProject(root, subMain, [new(subMain,"submain.tex",ProjectFileKind.Tex,File.ReadAllText(subMain)), new(subSource,"sub/fig.tex",ProjectFileKind.Tex,File.ReadAllText(subSource)), new(Path.Combine(sub,"fig.eps"),"sub/fig.eps",ProjectFileKind.Asset, "")], []);
        var mapping = new SyncTexService(); var settings = new AppSettings { SynctexPath = Path.Combine(root, "missing.exe") };
        var point = await mapping.ForwardViewportAsync(project, Path.Combine(root, "submain.pdf"), new(subSource, 6), settings);
        Check.True(point is not null, "Subdirectory source SyncTeX must resolve relative to its own build directory");
        var back = await mapping.InverseAsync(project, Path.Combine(root, "submain.pdf"), point!, settings);
        Check.True(back is not null && back.Path == subSource, "Subdirectory graphic inverse must resolve source in the graphic directory");
    }
    public static async Task RunAsync(string directory)
    {
        var root = Path.GetFullPath(directory); var main = Path.Combine(root, "main.tex"); var source = Path.Combine(root, "fig.tex");
        var asset = Path.Combine(root, "fig.eps"); var pdf = Path.Combine(root, "main.pdf");
        var p = new TexProject(root, main, [new(main,"main.tex",ProjectFileKind.Tex,File.ReadAllText(main)), new(source,"fig.tex",ProjectFileKind.Tex,File.ReadAllText(source)), new(asset,"fig.eps",ProjectFileKind.Asset, "")], []);
        Check.True(EpsSyncMap.FindSource(root,asset) == source, "dvips source not recognized");
        var sync = new SyncTexService(); var settings = new AppSettings { SynctexPath = Path.Combine(root,"missing.exe") };
        var a = await sync.ForwardViewportAsync(p,pdf,new(source,5),settings);
        var b = await sync.ForwardViewportAsync(p,pdf,new(source,7),settings);
        Check.True(a is not null && b is not null && b.Y > a.Y + 100, $"EPS forward does not cover figure: {a} / {b}");
        var inverse = await sync.InverseAsync(p,pdf,new(a!.Page,a.X,a.Y,0,0),settings);
        Check.True(inverse is not null && inverse.Path == source && Math.Abs(inverse.Line-5) <= 1, $"EPS inverse mismatch: {inverse}");
        Console.WriteLine("PASS: dvips EPS source forward and inverse positioning");
        var pdfMain = Path.Combine(root, "mainpdf.tex"); var pdfSource = Path.Combine(root, "multipage.tex"); var pdfAsset = Path.Combine(root, "multipage.pdf");
        var q = new TexProject(root, pdfMain, [new(pdfMain,"mainpdf.tex",ProjectFileKind.Tex,File.ReadAllText(pdfMain)), new(pdfSource,"multipage.tex",ProjectFileKind.Tex,File.ReadAllText(pdfSource)), new(pdfAsset,"multipage.pdf",ProjectFileKind.Asset, "")], []);
        var mapping = new SyncTexService { PdfPageSize = (_, _) => (612, 792) };
        var location = await mapping.ForwardViewportAsync(q, Path.Combine(root,"mainpdf.pdf"), new(pdfSource,6), settings);
        Check.True(location is not null, "Embedded second PDF page forward mapping failed");
        var back = await mapping.InverseAsync(q, Path.Combine(root,"mainpdf.pdf"), location!, settings);
        Check.True(back is not null && back.Path == pdfSource && Math.Abs(back.Line-6) <= 1, $"PDF inverse mismatch: {back}");
        var changedSource = q with { Files = q.Files.Select(f => f.Path == pdfSource ? f with { Content = f.Content + "\nchanged" } : f).ToArray() };
        Check.True(await mapping.ForwardViewportAsync(changedSource, Path.Combine(root,"mainpdf.pdf"), new(pdfSource,6), settings) is null, "Unsaved source must not use stale graphic mapping");
        var rotated = q with { Files = q.Files.Select(f => f.Path == pdfMain ? f with { Content = f.Content.Replace("page=2,", "angle=30,page=2,") } : f).ToArray() };
        Check.True(await mapping.ForwardViewportAsync(rotated, Path.Combine(root,"mainpdf.pdf"), new(pdfSource,6), settings) is null, "Rotated inclusion must not use unrotated coordinates");
        var duplicate = q with { Files = q.Files.Select(f => f.Path == pdfMain ? f with { Content = f.Content.Replace("{multipage.pdf}", "{multipage.pdf}\\includegraphics{fig.eps}") } : f).ToArray() };
        Check.True(await mapping.ForwardViewportAsync(duplicate, Path.Combine(root,"mainpdf.pdf"), new(pdfSource,6), settings) is null, "Same-line graphics must not be confused");
        var originalTime = File.GetLastWriteTimeUtc(pdfSource);
        try
        {
            File.SetLastWriteTimeUtc(pdfSource, DateTime.UtcNow.AddMinutes(1));
            Check.True(EpsSyncMap.FindSource(root, pdfAsset) is null, "Source newer than output must invalidate mapping");
        }
        finally { File.SetLastWriteTimeUtc(pdfSource, originalTime); }
        var originalPdfTime = File.GetLastWriteTimeUtc(pdfAsset);
        try
        {
            File.SetLastWriteTimeUtc(pdfAsset, DateTime.UtcNow.AddMinutes(1));
            Check.True(EpsSyncMap.FindSource(root, pdfAsset) is null, "Output newer than SyncTeX must invalidate mapping");
        }
        finally { File.SetLastWriteTimeUtc(pdfAsset, originalPdfTime); }
        Console.WriteLine("PASS: embedded PDF page 2 forward and inverse positioning");
    }
}
