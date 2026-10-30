using TeXeed.Core;

namespace TeXeed.Tests;

public static class LongBlockTests
{
    public static async Task RunAsync(TexProject parent)
    {
        var root = Path.Combine(parent.Root, "long-blocks"); Directory.CreateDirectory(root);
        var main = Path.Combine(root, "main.tex");
        var content = "\\begin{document}\n\\begin{lstlisting}\none\ntwo\nthree\nfour\nfive\n\\end{lstlisting}\n\\begin{tikzpicture}\na\nb\nc\nd\ne\n\\end{tikzpicture}\n\\end{document}\n";
        await File.WriteAllTextAsync(main, content);
        var project = new TexProject(root, main, [new(main, "main.tex", ProjectFileKind.Tex, content)], []);
        var pdf = Path.Combine(root, "main.pdf"); await File.WriteAllTextAsync(pdf, "%PDF-1.4\n");
        var syncPath = Path.ChangeExtension(pdf, ".synctex");
        const int unit = 65782;
        var records = $"(1,3:{100*unit},{100*unit}:{80*unit},{10*unit},0\nx1,3:{100*unit},{100*unit}\n)\n" +
            $"(1,7:{100*unit},{180*unit}:{80*unit},{10*unit},0\nx1,7:{100*unit},{180*unit}\n)\n" +
            $"(1,15:{100*unit},{400*unit}:{80*unit},{200*unit},0\n" +
            string.Join('\n', Enumerable.Range(10, 5).Select(line => $"(1,{line}:{100*unit},{200*unit}:0,0,0\nx1,{line}:{100*unit},{200*unit}\n)")) + "\n)\n";
        var data = $"SyncTeX Version:1\nInput:1:{main}\nOutput:pdf\nMagnification:1000\nUnit:1\nX Offset:0\nY Offset:0\nContent:\n{{1\n{records}}}1\nPostamble:\nCount:30\n";
        await File.WriteAllTextAsync(syncPath, data);
        var settings = new AppSettings { SynctexPath = Path.Combine(root, "missing.exe") };
        var sync = new SyncTexService();
        var middle = await sync.ForwardViewportAsync(project, pdf, new(main, 5), settings);
        Check.True(middle is not null && Math.Abs(middle.Y - 130) < 1, "Missing lines inside listing should interpolate between same-page anchors");
        double previous = double.NegativeInfinity;
        for (var line = 10; line <= 14; line++)
        {
            var position = await sync.ForwardViewportAsync(project, pdf, new(main, line), settings);
            Check.True(position is not null && position.Y >= 199 && position.Y <= 401 && position.Y > previous, "Collapsed graphic mapping must progress inside its box");
            var reverse = await sync.InverseAsync(project, pdf, new(1, position!.X + 1, position.Y), settings);
            Check.True(reverse?.Line == line, "Graphic approximation must round-trip its source row");
            previous = position.Y;
        }
        Console.WriteLine("PASS: sparse block interpolation, collapsed graphic extent and inverse approximation");
        if (Environment.GetEnvironmentVariable("TEXEED_TEST_REAL_BLOCKS") is { Length: > 0 } engine)
            await RunRealAsync(engine, root);
    }

    private static async Task RunRealAsync(string engine, string root)
    {
        var fixtures = Path.GetFullPath("tests/fixtures/long-blocks");
        foreach (var name in new[] { "main", "algorithm2e", "tikz", "verbatim" })
        {
            var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            var main = Path.Combine(directory, name + ".tex"); File.Copy(Path.Combine(fixtures, name + ".tex"), main, true);
            using var process = new System.Diagnostics.Process { StartInfo = new(engine) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var argument in new[] { "-synctex=1", "-interaction=nonstopmode", "-halt-on-error", main }) process.StartInfo.ArgumentList.Add(argument);
            process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Check.True(process.ExitCode == 0, $"Real {name} fixture failed: {await output} {await errors}");
            var project = await new ProjectService().LoadAsync(main);
            var pdf = Path.ChangeExtension(main, ".pdf"); var sync = new SyncTexService();
            var settings = new AppSettings { SynctexPath = Path.Combine(directory, "absent.exe") };
            var (first, last) = name switch { "main" => (9, 15), "algorithm2e" => (8, 15), "verbatim" => (4, 93), _ => (6, 15) };
            double previous = double.NegativeInfinity;
            var previousPage = 1;
            for (var line = first; line <= last; line++)
            {
                var location = await sync.ForwardViewportAsync(project, pdf, new(main, line), settings);
                Check.True(location is not null && (location.Page > previousPage || location.Page == previousPage && (line == first || location.Y > previous + 1)), $"Real {name} line {line} did not advance: {previousPage}:{previous:F1} -> {location?.Page}:{location?.Y:F1}");
                if (name is "tikz" or "verbatim")
                {
                    var inverse = await sync.InverseAsync(project, pdf, new(location!.Page, location.X + 1, location.Y), settings);
                    Check.True(inverse?.Line == line, $"Real {name} inverse mismatch for {line}: {inverse?.Line}");
                }
                previous = location!.Y;
                previousPage = location.Page;
            }
            if (name == "verbatim") Check.True(previousPage > 1, "Verbatim fixture must cross a page boundary");
            if (name == "main")
            {
                var firstRow = await sync.ForwardViewportAsync(project, pdf, new(main, 26), settings);
                var lastRow = await sync.ForwardViewportAsync(project, pdf, new(main, 28), settings);
                Check.True(firstRow is not null && lastRow is not null && lastRow.Y > firstRow.Y + 10, "Real longtable rows must advance");
            }
        }
        Console.WriteLine("PASS: real XeLaTeX listing, algorithm, longtable, multi-page verbatim and TikZ forward/inverse positioning");
    }
}
