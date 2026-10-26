using System.IO;
using System.Windows;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckLayoutMetadataAsync(MainWindow window, string root, string output)
    {
        var engines = new List<(TexEngine Kind, string Path)> { (TexEngine.Tectonic, Path.Combine(root, ".tools", "tectonic", "tectonic.exe")) };
        var tools = CompileService.DetectTools();
        foreach (var (name, kind) in new[] { ("xelatex", TexEngine.XeLaTeX), ("pdflatex", TexEngine.PdfLaTeX) })
            if (tools.Engines.FirstOrDefault(e => e.Name == name) is { } tool) engines.Add((kind, tool.Path));
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var preview = (PdfPreview)window.FindName("Preview");
        foreach (var engine in engines)
        {
            var directory = Path.Combine(output, engine.Kind.ToString()); Directory.CreateDirectory(directory);
            var main = Path.Combine(directory, "main.tex");
            var source = """
                \documentclass[twoside]{article}
                \usepackage[paperwidth=600bp,paperheight=800bp,inner=80bp,outer=50bp,bindingoffset=10bp]{geometry}
                \begin{document}
                \begin{center}A centered short table.\end{center}
                \newpage
                \hspace*{40bp}Indented short line.
                \newgeometry{left=100bp,right=70bp,bindingoffset=0bp}
                Tiny.
                \end{document}
                """;
            File.WriteAllText(main, source);
            var project = await new ProjectService().LoadAsync(main);
            var result = await new CompileService(disableMiKTeXInstaller: engine.Path.Contains("miktex", StringComparison.OrdinalIgnoreCase))
                .CompileAsync(project, new AppSettings { Engine = engine.Kind, EnginePath = engine.Path });
            File.WriteAllText(Path.Combine(directory, "compile.log"), result.Log);
            if (!result.Success || result.PdfPath is null) throw new Exception(engine.Kind + " layout fixture failed: " + result.Log);
            var layouts = TexPageLayout.Read(result.PdfPath);
            if (layouts.Count != 3) throw new Exception(engine.Kind + " missing physical page layouts: " + layouts.Count);
            var expected = new[] { (Left: 90d, Width: 460d), (Left: 50d, Width: 460d), (Left: 100d, Width: 430d) };
            for (var i = 0; i < expected.Length; i++)
                if (Math.Abs(layouts[i].Left - expected[i].Left) > .1 || Math.Abs(layouts[i].Width - expected[i].Width) > .1)
                    throw new Exception($"{engine.Kind} page {i + 1}: expected {expected[i]}, got {layouts[i]}");
            preview.AutoCrop = true; await preview.LoadAsync(result.PdfPath); await WaitForPreviewRenderedAsync(window);
            var rendered = ((System.Collections.IEnumerable)Field(preview, "_layouts")!).Cast<object>().ToArray();
            for (var i = 0; i < expected.Length; i++)
            {
                var crop = rendered[i].GetType().GetProperty("Crop")!.GetValue(rendered[i])!;
                var left = (double)crop.GetType().GetProperty("Left")!.GetValue(crop)!;
                var width = (double)crop.GetType().GetProperty("Width")!.GetValue(crop)!;
                if (Math.Abs(left - (expected[i].Left - 6)) > .1 || Math.Abs(width - (expected[i].Width + 12)) > .1)
                    throw new Exception($"{engine.Kind} page {i + 1} crop does not follow its text area: {left}, {width}");
            }
            if (File.ReadAllText(main) != source) throw new Exception("Layout recording changed the user source");
            var sourceLine = Array.FindIndex(source.Split('\n'), l => l.Contains("Indented short line")) + 1;
            var sync = new SyncTexService();
            var settings = new AppSettings { SynctexPath = Path.Combine(directory, "missing-synctex.exe") };
            var position = await sync.ForwardAsync(project, result.PdfPath, new SourceLocation(main, sourceLine), settings);
            if (position?.Page != 2) throw new Exception(engine.Kind + " layout recording broke source mapping");
            Capture(window, Path.Combine(directory, "page-layout.png"));
            Console.WriteLine($"PASS: {engine.Kind} asymmetric odd/even margins, binding offset, centered/indented short pages and newgeometry");
        }
    }
}
