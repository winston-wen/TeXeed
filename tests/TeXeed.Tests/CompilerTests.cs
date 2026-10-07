using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using TeXeed.Core;

namespace TeXeed.Tests;

public static class CompilerTests
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetACP")]
    private static extern uint SyncTestCodePage();
    public static async Task<int> RunRealEngineAsync(string executable, string mainPath)
    {
        var project = await new ProjectService().LoadAsync(Path.GetFullPath(mainPath));
        Check.True(project.Files.Any(f => f.Kind == ProjectFileKind.Bibliography), "Real fixture must discover its bibliography");
        Check.True(project.Files.Any(f => f.Kind == ProjectFileKind.Style), "Real fixture must discover its local style");
        var settings = new AppSettings { Engine = TexEngine.Tectonic, EnginePath = Path.GetFullPath(executable), SynctexPath = Path.Combine(project.Root, "missing-synctex.exe") };
        var result = await new CompileService().CompileAsync(project, settings);
        var logPath = Path.Combine(project.Root, ".texeed", "integration.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, result.Log);
        Console.WriteLine(result.Log);
        Check.True(result.Success, "Real Tectonic compilation failed. Log: " + logPath);
        Check.True(result.SynctexAvailable && result.PdfPath is not null, "Real compilation must produce PDF and SyncTeX");
        var bibliography = await File.ReadAllTextAsync(Path.Combine(project.Root, ".texeed", "build", Path.GetFileNameWithoutExtension(project.MainPath) + ".bbl"));
        Check.True(bibliography.Contains("knuth1984texbook", StringComparison.Ordinal) && bibliography.Contains("lamport1994latex", StringComparison.Ordinal), "Real bibliography must include both cited entries");
        var auxiliary = await File.ReadAllTextAsync(Path.Combine(project.Root, ".texeed", "build", Path.GetFileNameWithoutExtension(project.MainPath) + ".aux"));
        Check.True(auxiliary.Contains(@"\newlabel{eq:energy}{{1}{1}}", StringComparison.Ordinal) && auxiliary.Contains(@"\newlabel{sec:method}{{2}{2}}", StringComparison.Ordinal), "Equation and section references must resolve across included files");
        Check.True(!result.Diagnostics.Any(d => d.Message.Contains("undefined", StringComparison.OrdinalIgnoreCase)), "The final pass must have no undefined references or citations");
        var intro = project.Files.Single(f => f.RelativePath.Replace('\\', '/') == "sections/introduction.tex");
        var line = Array.FindIndex(intro.Content.Split('\n'), text => text.Contains("E = mc", StringComparison.Ordinal)) + 1;
        var sync = new SyncTexService();
        var point = await sync.ForwardAsync(project, result.PdfPath!, new(intro.Path, line), settings);
        Check.True(point is not null && point.Page == 1, "Native SyncTeX must locate an included equation on page 1");
        var inverse = await sync.InverseAsync(project, result.PdfPath!, point!, settings);
        Check.True(inverse is not null && inverse.Path == intro.Path && Math.Abs(inverse.Line - line) <= 3, "Native SyncTeX must return the included source near the equation");
        var method = project.Files.Single(f => f.RelativePath.Replace('\\', '/') == "sections/method.tex");
        var second = await sync.ForwardAsync(project, result.PdfPath!, new(method.Path, 4), settings);
        Check.True(second is not null && second.Page == 2, "Native SyncTeX must locate the second included file on page 2");
        Console.WriteLine($"PASS: real Tectonic bibliography, references, multi-file compilation and two-way SyncTeX. PDF: {result.PdfPath}");
        return 0;
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeXeed 编译测试 & spaces", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var main = Path.Combine(root, "main document.tex");
        var content = string.Join('\n', Enumerable.Range(1, 60).Select(i => $"Source line {i} with text"));
        await File.WriteAllTextAsync(main, content);
        var project = new TexProject(root, main, [new(main, Path.GetFileName(main), ProjectFileKind.Tex, content)], []);
        var previousMode = Environment.GetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE");
        try
        {
            DiagnosticParsing(project);
            CliCoordinateParsing(project);
            await NativeSyncTexAsync(project);
            var service = new CompileService();
            var absent = await service.CompileAsync(project, new() { Engine = TexEngine.PdfLaTeX, EnginePath = Path.Combine(root, "absent.exe") });
            Check.True(!absent.Success && absent.Diagnostics.Count > 0, "Missing compiler must produce an actionable diagnostic");

            var executable = Environment.ProcessPath ?? throw new Exception("Test executable path unavailable");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Run compiler integration tests via the test apphost (dotnet run --project tests/TeXeed.Tests).");
            var settings = new AppSettings { Engine = TexEngine.PdfLaTeX, EnginePath = executable };
            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "success");
            var success = await service.CompileAsync(project, settings);
            Check.True(success.Success, success.Log);
            Check.True(success.PdfPath is not null && File.Exists(success.PdfPath), "Successful output must be published");
            Check.True(success.SynctexAvailable, "SyncTeX must accompany a successful PDF");
            var publishedContents = await File.ReadAllTextAsync(success.PdfPath!);
            var build = Path.Combine(root, ".texeed", "build");
            var runCount = int.Parse(await File.ReadAllTextAsync(Path.Combine(build, "fake-run-count")), CultureInfo.InvariantCulture);
            Check.Equal(2, runCount);
            var arguments = await File.ReadAllLinesAsync(Path.Combine(build, "fake-arguments"));
            Check.True(arguments.Contains("-no-shell-escape"), "Engine shell escape must be disabled");
            var driver = arguments.Last();
            Check.True(!Path.IsPathRooted(driver), "Engine input must be relative to the project working directory");
            var outputPath = arguments.Single(a => a.StartsWith("-output-directory=", StringComparison.Ordinal))["-output-directory=".Length..];
            Check.True(!Path.IsPathRooted(outputPath) && outputPath.All(c => c < 128), "Output argument must stay ASCII even under a Unicode project root");
            driver = Path.GetFullPath(driver, root);
            Check.Equal(Path.GetFileName(main), Path.GetFileName(driver));
            Check.True(File.Exists(driver) && (await File.ReadAllTextAsync(driver)).Contains(main.Replace('\\', '/')), "Layout driver must retain the original source path with spaces and shell metacharacters");
            Check.True(arguments.Contains("-recorder"), "Direct engines must record active generated files");
            await File.WriteAllTextAsync(Path.Combine(build, "fake-run-count"), "0");
            var warm = await new CompileService().CompileAsync(project, settings);
            Check.True(warm.Success, warm.Log);
            Check.Equal(1, int.Parse(await File.ReadAllTextAsync(Path.Combine(build, "fake-run-count")), CultureInfo.InvariantCulture));

            await ConvergencePassesAsync(project.Root, executable);
            await BibliographyPassesAsync(project, executable);

            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "failure");
            var failure = await service.CompileAsync(project, settings);
            Check.True(!failure.Success && failure.PdfPath is null, "Failed builds cannot be published");
            Check.Equal(publishedContents, await File.ReadAllTextAsync(success.PdfPath!));
            Check.True(failure.Diagnostics.Any(d => d.Line == 7 && !d.IsWarning), "Engine errors must point to the reported line");

            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "stale");
            var stale = await service.CompileAsync(project, settings);
            Check.True(!stale.Success, "Zero exit without a fresh PDF must be rejected");
            Check.Equal(publishedContents, await File.ReadAllTextAsync(success.PdfPath!));

            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "sleep");
            var sleeping = service.CompileAsync(project, settings);
            var childMarker = Path.Combine(build, "fake-child-pid");
            await WaitUntilAsync(() => File.Exists(childMarker), TimeSpan.FromSeconds(10));
            var childPid = int.Parse(await File.ReadAllTextAsync(childMarker), CultureInfo.InvariantCulture);
            var timer = Stopwatch.StartNew();
            service.Cancel();
            var cancelled = await sleeping.WaitAsync(TimeSpan.FromSeconds(10));
            Check.True(!cancelled.Success && timer.Elapsed < TimeSpan.FromSeconds(10), "Cancel must promptly finish the active compile");
            await WaitUntilAsync(() => !ProcessAlive(childPid), TimeSpan.FromSeconds(5));
            Check.True(!ProcessAlive(childPid), "Cancel must terminate compiler descendants");
            Check.Equal(publishedContents, await File.ReadAllTextAsync(success.PdfPath!));

            // SyncTeX must return protocol text even when external-viewer env vars exist.
            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "synctex");
            var priorViewer = Environment.GetEnvironmentVariable("SYNCTEX_VIEWER");
            var priorEditor = Environment.GetEnvironmentVariable("SYNCTEX_EDITOR");
            try
            {
                Environment.SetEnvironmentVariable("SYNCTEX_VIEWER", "must not be inherited");
                Environment.SetEnvironmentVariable("SYNCTEX_EDITOR", "must not be inherited");
                var sync = new SyncTexService();
                var point = await sync.ForwardAsync(project, success.PdfPath!, new(main, 12), settings with { SynctexPath = executable });
                Check.True(point is { Page: 1, X: 20, Y: 30, Width: 40, Height: 10 }, "CLI boxes must use their top edge and ignore viewer commands");
                var location = await sync.InverseAsync(project, success.PdfPath!, new(1, 20.5, 30.75), settings with { SynctexPath = executable });
                Check.True(location is { Line: 12, Column: 3 }, $"Inverse CLI must return the trusted source path; actual: {location}");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SYNCTEX_VIEWER", priorViewer);
                Environment.SetEnvironmentVariable("SYNCTEX_EDITOR", priorEditor);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", previousMode);
            foreach (var marker in Directory.EnumerateFiles(root, "fake-*-pid", SearchOption.AllDirectories))
            {
                if (int.TryParse(await File.ReadAllTextAsync(marker), out var pid))
                {
                    try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(true); }
                    catch (ArgumentException) { }
                }
            }
            Directory.Delete(root, true);
        }
    }

    private static void DiagnosticParsing(TexProject project)
    {
        var errors = CompileService.ParseDiagnostics($"{project.MainPath}:7: Undefined control sequence.\n! Missing $ inserted.\nl.19 Oops\nLaTeX Warning: Reference `eq:missing' on page 1 undefined on input line 24.\n", project);
        Check.True(errors.Any(d => d.Path == project.MainPath && d.Line == 7 && !d.IsWarning));
        Check.True(errors.Any(d => d.Line == 19 && d.Message == "Missing $ inserted."));
        Check.True(errors.Any(d => d.Line == 24 && d.IsWarning));
        var hostile = CompileService.ParseDiagnostics("../../outside.tex:4: Error\n", project);
        Check.True(hostile.All(d => d.Path == project.MainPath), "Diagnostic navigation cannot escape the project");
    }

    private static void CliCoordinateParsing(TexProject project)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var point = SyncTexService.ParseForwardOutput("SyncTeX result begin\nOutput:C:\\my doc.pdf\nPage:2\nx:120.25\ny:500.5\nh:100.25\nv:512.75\nW:200.5\nH:16.5\nSyncTeX result end\n");
            Check.True(point is not null);
            Check.Equal(2, point!.Page);
            Check.Equal(100.25, point.X);
            Check.Equal(496.25, point.Y);
            Check.Equal(200.5, point.Width);
            Check.Equal(16.5, point.Height);
            Check.True(SyncTexService.ParseForwardOutput("Output:file.pdf\nPage:1\nx:NaN\ny:Infinity\n") is null);
            var inverse = SyncTexService.ParseInverseOutput($"Output:main.pdf\nInput:{project.MainPath}\nLine:12\nColumn:-1\n", project);
            Check.True(inverse is { Line: 12, Column: 1 });
            var escaped = SyncTexService.ParseInverseOutput("Output:main.pdf\nInput:../../private.tex\nLine:2\nColumn:1\n", project);
            Check.True(escaped is null, "Inverse results must identify a loaded project source");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    private static async Task NativeSyncTexAsync(TexProject project)
    {
        var directory = Path.Combine(project.Root, "native-sync");
        Directory.CreateDirectory(directory);
        var pdf = Path.Combine(directory, "document.pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-1.4\n");
        var raw = $$"""
            SyncTeX Version:1
            Input:1:{{project.MainPath}}
            Input:2:../../private.tex
            Output:pdf
            Magnification:1000
            Unit:1
            X Offset:0
            Y Offset:0
            Content:
            {1
            (1,10:6578176,13156352:13156352,657818,131564
            x1,12,4:6578176,=
            x1,13,2:13156352,=
            )
            (2,12:0,0:10000000,1000000,0
            )
            }1
            Postamble:
            Count:8
            Post Scriptum:
            """;
        await using (var output = File.Create(Path.ChangeExtension(pdf, ".synctex.gz")))
        await using (var gzip = new GZipStream(output, CompressionMode.Compress))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(raw));
        var settings = new AppSettings { SynctexPath = Path.Combine(project.Root, "missing-synctex.exe") };
        var sync = new SyncTexService();
        var first = await sync.ForwardAsync(project, pdf, new(project.MainPath, 12, 4), settings);
        Check.True(first is not null);
        Check.True(Math.Abs(first!.X - 100) < .01 && Math.Abs(first.Y - 190) < .01, "Native units must convert scaled points to PDF points");
        var second = await sync.ForwardAsync(project, pdf, new(project.MainPath, 13, 2), settings);
        Check.True(second is not null && Math.Abs(second.X - 200) < .01 && Math.Abs(second.Y - 190) < .01, "Compressed Y coordinates must reuse the previous full coordinate");
        var source = await sync.InverseAsync(project, pdf, new(1, 101, 192), settings);
        Check.True(source is { Line: 12, Column: 4 }, "Native inverse must identify the nearest source node");
        Check.True(await sync.InverseAsync(project, pdf, new(4, 10, 20), settings) is null, "Missing pages must not map to arbitrary source lines");

        if (OperatingSystem.IsWindows())
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding((int)SyncTestCodePage());
            var encoded = encoding.GetBytes(raw);
            if (encoding.GetString(encoded) == raw)
            {
                await using (var output = File.Create(Path.ChangeExtension(pdf, ".synctex.gz")))
                await using (var gzip = new GZipStream(output, CompressionMode.Compress)) await gzip.WriteAsync(encoded);
                var ansiSync = new SyncTexService();
                var ansiPoint = await ansiSync.ForwardViewportAsync(project, pdf, new(project.MainPath, 12, 4), settings);
                Check.True(ansiPoint is { Page: 1 }, "ANSI-encoded SyncTeX must retain Chinese source paths for native viewport following");
                var ansiSource = await ansiSync.InverseAsync(project, pdf, new(1, 101, 192), settings);
                Check.True(ansiSource?.Path == project.MainPath && ansiSource.Line == 12, "ANSI SyncTeX inverse lost the trusted source path");
            }
        }

        // Output routines may stamp the previous page's footer with the source line
        // currently being read on the next page. Those kerns are not source text.
        var boundary = $$"""
            SyncTeX Version:1
            Input:1:{{project.MainPath}}
            Output:pdf
            Magnification:1000
            Unit:1
            X Offset:0
            Y Offset:0
            Content:
            {1
            (1,30:6578176,46047232:13156352,657818,0
            k1,30:6578176,46047232:6000000
            )
            }1
            {2
            (1,29:6578176,6578176:13156352,657818,0
            x1,29:6578176,6578176
            )
            (1,31:6578176,7893811:13156352,657818,0
            g1,30:6578176,7893811
            x1,31:7000000,7893811
            )
            }2
            Postamble:
            Count:12
            Post Scriptum:
            """;
        await File.WriteAllTextAsync(Path.ChangeExtension(pdf, ".synctex"), boundary);
        foreach (var line in new[] { 29, 30, 31, 32, 31, 30, 29 })
        {
            var point = await sync.ForwardViewportAsync(project, pdf, new(project.MainPath, line), settings);
            Check.True(point?.Page == 2, $"Viewport source {line} must not jump to the previous page's shipout kern");
        }
        var fallback = await sync.ForwardAsync(project, pdf, new(project.MainPath, 30), settings);
        Check.True(fallback?.Page == 2, "Native forward fallback must prefer source content over same-line positioning kerns");
    }

    private static async Task BibliographyPassesAsync(TexProject project, string testExecutable)
    {
        var toolsDirectory = Path.Combine(project.Root, "fake-tools");
        Directory.CreateDirectory(toolsDirectory);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(testExecutable)!))
            File.Copy(file, Path.Combine(toolsDirectory, Path.GetFileName(file)), true);
        foreach (var name in new[] { "pdflatex", "bibtex", "biber" })
            File.Copy(testExecutable, Path.Combine(toolsDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : "")), true);
        var settings = new AppSettings { Engine = TexEngine.PdfLaTeX, EnginePath = Path.Combine(toolsDirectory, OperatingSystem.IsWindows() ? "pdflatex.exe" : "pdflatex") };
        var service = new CompileService();
        var references = Path.Combine(project.Root, "references.bib");
        var style = Path.Combine(project.Root, "custom.bst");
        await File.WriteAllTextAsync(references, "@book{alpha,title={First title}}");
        await File.WriteAllTextAsync(style, "ENTRY { title } {} {}\n");
        project = project with { Files = project.Files.Concat(new[]
        {
            new ProjectFile(references, "references.bib", ProjectFileKind.Bibliography, "stale in-memory content"),
            new ProjectFile(style, "custom.bst", ProjectFileKind.Style, "stale in-memory content")
        }).ToArray() };
        var build = Path.Combine(project.Root, ".texeed", "build");
        var countPath = Path.Combine(build, "fake-bibliography-count");
        var runCount = Path.Combine(build, "fake-run-count");
        async Task<int> Count() => File.Exists(countPath) ? int.Parse(await File.ReadAllTextAsync(countPath), CultureInfo.InvariantCulture) : 0;
        async Task<CompileResult> Compile(string mode)
        {
            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", mode);
            await File.WriteAllTextAsync(runCount, "0");
            return await new CompileService().CompileAsync(project, settings);
        }
        foreach (var mode in new[] { "bibtex", "biber" })
        {
            var before = await Count();
            var compiled = await Compile(mode);
            Check.True(compiled.Success, compiled.Log);
            Check.Equal(before + 1, await Count());
            var marker = Path.Combine(build, "fake-bibliography");
            Check.Equal(mode, await File.ReadAllTextAsync(marker));
            Check.True(compiled.Log.Contains($"──── {mode}", StringComparison.Ordinal), $"The direct engine must invoke {mode}");
            var hot = await Compile(mode);
            Check.True(hot.Success, hot.Log);
            Check.Equal(before + 1, await Count());
            Check.Equal(1, int.Parse(await File.ReadAllTextAsync(runCount), CultureInfo.InvariantCulture));
            if (mode == "biber") Check.Equal("True", await File.ReadAllTextAsync(Path.Combine(build, "fake-bcf-was-present")));

            // Read the actual database bytes, not stale ProjectFile.Content or only
            // file timestamps (editors and synchronization tools may preserve them).
            var timestamp = File.GetLastWriteTimeUtc(references);
            await File.AppendAllTextAsync(references, "\n% changed " + mode);
            File.SetLastWriteTimeUtc(references, timestamp);
            var databaseChanged = await Compile(mode);
            Check.True(databaseChanged.Success, databaseChanged.Log);
            Check.Equal(before + 2, await Count());
            await File.AppendAllTextAsync(style, "% style changed " + mode);
            Check.True((await Compile(mode)).Success);
            Check.Equal(before + 3, await Count());

            await File.WriteAllTextAsync(Path.Combine(build, "fake-citation-key"), "second-" + mode);
            Check.True((await Compile(mode)).Success);
            Check.Equal(before + 4, await Count());
            await File.WriteAllTextAsync(Path.Combine(build, Path.GetFileNameWithoutExtension(project.MainPath) + ".bbl"), "externally overwritten bibliography");
            Check.True((await Compile(mode)).Success);
            Check.Equal(before + 5, await Count());

            if (mode == "bibtex")
            {
                // The source may select its style through a macro, so it need not
                // occur in Project.Files. The engine's actual aux is authoritative.
                var dynamicStyle = Path.Combine(project.Root, "dynamic.bst");
                await File.WriteAllTextAsync(dynamicStyle, "ENTRY { title } {} {}\n");
                await File.WriteAllTextAsync(Path.Combine(build, "fake-style-name"), "dynamic");
                Check.True((await Compile(mode)).Success);
                var dynamicCount = await Count();
                await File.AppendAllTextAsync(dynamicStyle, "% a style change hidden behind a macro\n");
                Check.True((await Compile(mode)).Success);
                Check.Equal(dynamicCount + 1, await Count());
                await File.WriteAllTextAsync(Path.Combine(build, "fake-style-name"), "plain");
                Check.True((await Compile(mode)).Success);
                var plainCount = await Count();
                Check.True((await Compile(mode)).Success);
                Check.Equal(plainCount, await Count());
                await File.WriteAllTextAsync(Path.Combine(build, "fake-citation-order"), "before");
                Check.True((await Compile(mode)).Success);
                var orderCount = await Count();
                await File.WriteAllTextAsync(Path.Combine(build, "fake-citation-order"), "after");
                Check.True((await Compile(mode)).Success);
                Check.Equal(orderCount + 1, await Count());
                File.Delete(Path.Combine(build, "fake-citation-order"));
            }

            // A bibliography tool can succeed before a later TeX pass fails. That
            // incomplete build must not commit a reusable cache or replace preview.
            var priorPreview = await File.ReadAllBytesAsync(compiled.PdfPath!);
            await File.AppendAllTextAsync(references, "\n% change before failed build " + mode);
            var failed = await Compile(mode + "-late-failure");
            Check.True(!failed.Success, "A later failed pass cannot publish an intermediate bibliography build");
            var afterFailedPreview = await File.ReadAllBytesAsync(compiled.PdfPath!);
            Check.True(priorPreview.SequenceEqual(afterFailedPreview));
            var afterFailure = await Count();
            Check.True((await Compile(mode)).Success);
            Check.Equal(afterFailure + 1, await Count());
        }
        var finalCount = await Count();
        var noBibliography = await Compile("success");
        Check.True(noBibliography.Success, noBibliography.Log);
        Check.Equal(finalCount, await Count());
        Check.True(!noBibliography.Log.Contains("──── biber", StringComparison.Ordinal), "A stale .bcf cannot activate Biber after biblatex is removed");
        await ExternalBibliographyCacheAsync(project.Root, settings);
        Console.WriteLine("PASS: bibliography cache survives service restart and invalidates for control, database, style, output and failed builds.");
    }

    private static async Task ExternalBibliographyCacheAsync(string parent, AppSettings settings)
    {
        var root = Path.Combine(parent, "external-database-project");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var main = Path.Combine(root, "external.tex");
        var unrelatedLocalDatabase = Path.Combine(local, "references.bib");
        await File.WriteAllTextAsync(main, "test");
        await File.WriteAllTextAsync(unrelatedLocalDatabase, "@book{unrelated,title={Local}}");
        var project = new TexProject(root, main,
            [new(main, "external.tex", ProjectFileKind.Tex, "test"),
             new(unrelatedLocalDatabase, "local/references.bib", ProjectFileKind.Bibliography, "")], []);
        var build = Path.Combine(root, ".texeed", "build");
        Directory.CreateDirectory(build);
        await File.WriteAllTextAsync(Path.Combine(build, "fake-bib-name"), "../references");
        Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "bibtex");
        Check.True((await new CompileService().CompileAsync(project, settings)).Success);
        var countFile = Path.Combine(build, "fake-bibliography-count");
        var first = int.Parse(await File.ReadAllTextAsync(countFile), CultureInfo.InvariantCulture);
        Check.True((await new CompileService().CompileAsync(project, settings)).Success);
        Check.Equal(first + 1, int.Parse(await File.ReadAllTextAsync(countFile), CultureInfo.InvariantCulture));
    }

    private static async Task ConvergencePassesAsync(string parent, string executable)
    {
        foreach (var mode in new[] { "toc", "rerun", "later-stale" })
        {
            var root = Path.Combine(parent, "convergence-" + mode);
            Directory.CreateDirectory(root);
            var main = Path.Combine(root, "main.tex");
            await File.WriteAllTextAsync(main, "test");
            var project = new TexProject(root, main, [new(main, "main.tex", ProjectFileKind.Tex, "test")], []);
            var settings = new AppSettings { Engine = TexEngine.PdfLaTeX, EnginePath = executable };
            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", mode);
            var result = await new CompileService().CompileAsync(project, settings);
            var build = Path.Combine(root, ".texeed", "build");
            var count = int.Parse(await File.ReadAllTextAsync(Path.Combine(build, "fake-run-count")), CultureInfo.InvariantCulture);
            Check.Equal(mode == "toc" ? 3 : mode == "rerun" ? 4 : 2, count);
            Check.Equal(mode != "later-stale", result.Success);
            if (mode != "toc") continue;
            await File.WriteAllTextAsync(Path.Combine(build, "fake-run-count"), "0");
            Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "undefined");
            // Seed the plain, stable aux state: unresolved warnings alone cannot
            // turn every edit into four futile passes.
            await File.WriteAllTextAsync(Path.Combine(build, "main.aux"), "\\relax\n");
            var unresolved = await new CompileService().CompileAsync(project, settings);
            Check.True(unresolved.Success && unresolved.Diagnostics.Any(d => d.IsWarning), unresolved.Log);
            Check.Equal(1, int.Parse(await File.ReadAllTextAsync(Path.Combine(build, "fake-run-count")), CultureInfo.InvariantCulture));
        }
        Console.WriteLine("PASS: cold/hot and TOC convergence, explicit rerun cap, persistent undefined references and stale last-pass PDF.");
    }

    public static async Task<int> RunFakeEngineAsync(string[] args, string mode)
    {
        var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (executableName is "bibtex" or "biber")
        {
            var bibliographyDirectory = executableName == "biber" ? args[Array.IndexOf(args, "--output-directory") + 1] : Environment.CurrentDirectory;
            var counter = Path.Combine(bibliographyDirectory, "fake-bibliography-count");
            var count = File.Exists(counter) ? int.Parse(await File.ReadAllTextAsync(counter), CultureInfo.InvariantCulture) : 0;
            await File.WriteAllTextAsync(counter, (count + 1).ToString(CultureInfo.InvariantCulture));
            await File.WriteAllTextAsync(Path.Combine(bibliographyDirectory, "fake-bibliography"), executableName);
            await File.WriteAllTextAsync(Path.Combine(bibliographyDirectory, args.Last() + ".bbl"), "Fake bibliography output");
            return 0;
        }
        if (mode == "synctex")
        {
            if (Environment.GetEnvironmentVariable("SYNCTEX_VIEWER") is not null || Environment.GetEnvironmentVariable("SYNCTEX_EDITOR") is not null) return 9;
            if (args.Contains("view"))
                Console.WriteLine("SyncTeX result begin\nOutput:main.pdf\nPage:1\nx:20\ny:40\nh:20\nv:40\nW:40\nH:10\nSyncTeX result end");
            else
            {
                var input = Directory.EnumerateFiles(Environment.CurrentDirectory, "*.tex").Single();
                Console.WriteLine($"SyncTeX result begin\nOutput:main.pdf\nInput:{input}\nLine:12\nColumn:3\nSyncTeX result end");
            }
            return 0;
        }
        var outputArgument = args.FirstOrDefault(a => a.StartsWith("-output-directory=", StringComparison.Ordinal))
            ?? throw new Exception("Compiler did not provide its output directory");
        var outputDirectory = outputArgument["-output-directory=".Length..];
        Directory.CreateDirectory(outputDirectory);
        if (args.Contains("--fake-child"))
        {
            var marker = Path.Combine(outputDirectory, "fake-child-pid");
            await File.WriteAllTextAsync(marker + ".new", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            File.Move(marker + ".new", marker, true);
            await Task.Delay(TimeSpan.FromMinutes(3));
            return 0;
        }
        var main = args.Last();
        var job = Path.GetFileNameWithoutExtension(main);
        await File.WriteAllLinesAsync(Path.Combine(outputDirectory, "fake-arguments"), args);
        var countPath = Path.Combine(outputDirectory, "fake-run-count");
        var runs = File.Exists(countPath) ? int.Parse(await File.ReadAllTextAsync(countPath), CultureInfo.InvariantCulture) : 0;
        await File.WriteAllTextAsync(countPath, (runs + 1).ToString(CultureInfo.InvariantCulture));
        if (mode == "sleep")
        {
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "fake-parent-pid"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--fake-child");
            using var child = Process.Start(start);
            await Task.Delay(TimeSpan.FromMinutes(3));
            return 0;
        }
        if (mode == "stale") return 0;
        if (mode == "later-stale" && runs > 0) return 0;
        if (mode == "failure" || mode.EndsWith("-late-failure", StringComparison.Ordinal) && runs > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, job + ".pdf"), "%PDF-1.4\nbroken output");
            Console.Error.WriteLine($"{main}:7: Undefined control sequence.");
            return 1;
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, job + ".pdf"), "%PDF-1.4\nsuccessful test PDF");
        var log = mode == "rerun" ? "LaTeX Warning: Label(s) may have changed. Rerun to get cross-references right."
            : mode == "undefined" ? "LaTeX Warning: There were undefined references." : "Output written successfully.";
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, job + ".log"), log);
        var keyPath = Path.Combine(outputDirectory, "fake-citation-key");
        var key = File.Exists(keyPath) ? await File.ReadAllTextAsync(keyPath) : "alpha";
        var stylePath = Path.Combine(outputDirectory, "fake-style-name");
        var styleName = File.Exists(stylePath) ? await File.ReadAllTextAsync(stylePath) : "custom";
        var bibPath = Path.Combine(outputDirectory, "fake-bib-name");
        var bibName = File.Exists(bibPath) ? await File.ReadAllTextAsync(bibPath) : "references";
        var hasBibliography = File.Exists(Path.Combine(outputDirectory, job + ".bbl"));
        var citation = $"\\citation{{{key}}}\n";
        var orderPath = Path.Combine(outputDirectory, "fake-citation-order");
        if (mode.StartsWith("bibtex", StringComparison.Ordinal) && File.Exists(orderPath))
        {
            var order = await File.ReadAllTextAsync(orderPath);
            citation = order == "before" ? "\\citation{a}\n\\@input{chapter.aux}\n\\citation{b}\n"
                : "\\citation{a}\n\\citation{b}\n\\@input{chapter.aux}\n";
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "chapter.aux"), "\\citation{c}\n");
        }
        var auxText = mode.StartsWith("bibtex", StringComparison.Ordinal) ? $"\\relax\n{citation}\\bibstyle{{{styleName}}}\n\\bibdata{{{bibName}}}\n" + (hasBibliography ? "\\bibcite{alpha}{1}\n" : "")
            : mode.StartsWith("biber", StringComparison.Ordinal) ? "\\relax\n\\abx@aux@refcontext{nty/global//global/global}\n" + (hasBibliography ? "\\newlabel{bibliography}{{1}{2}}\n" : "")
            : mode == "toc" ? $"\\relax\n\\newlabel{{chapter}}{{{{1}}{{{Math.Min(2, runs + 1)}}}}}\n" : "\\relax\n";
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, job + ".aux"), auxText);
        if (mode == "toc") await File.WriteAllTextAsync(Path.Combine(outputDirectory, job + ".toc"), "Chapter on page " + Math.Min(2, runs + 1));
        if (mode.StartsWith("biber", StringComparison.Ordinal))
        {
            var bcf = Path.Combine(outputDirectory, job + ".bcf");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "fake-bcf-was-present"), File.Exists(bcf).ToString());
            await File.WriteAllTextAsync(bcf, $"<bcf:controlfile xmlns:bcf='test'><bcf:datasource>references.bib</bcf:datasource><bcf:citekey>{key}</bcf:citekey></bcf:controlfile>");
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, job + ".synctex"), $"SyncTeX Version:1\nInput:1:{main}\nPostamble:\n");
        Console.WriteLine("Output written successfully.");
        return 0;
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout) await Task.Delay(25);
        Check.True(condition(), "Timed out waiting for compiler process state");
    }
}
