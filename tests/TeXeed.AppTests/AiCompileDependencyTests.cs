using System.IO;
using System.Reflection;
using TeXeed.App;
using TeXeed.Core;

internal static partial class Program
{
    // Invoke from the WPF test dispatcher, using a fresh --output directory.
    private static async Task CheckAiCompileDependenciesAsync(MainWindow window, string root, string output)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var folder = Path.Combine(output, "compile-dependency-project"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        var csv = Path.Combine(folder, "plot.csv"); var dat = Path.Combine(folder, "metrics.dat");
        var bibliography = Path.Combine(folder, "bibliography.bbl");
        File.WriteAllText(csv, "Initial plot data.\n"); File.WriteAllText(dat, "Initial metric data.\n");
        File.WriteAllText(bibliography, "Initial bibliography content.\n");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\n" +
            "\\newread\\dataset\n\\openin\\dataset=plot.csv\n\\read\\dataset to\\plotdata\n\\closein\\dataset\n" +
            "\\openin\\dataset=metrics.dat\n\\read\\dataset to\\metricdata\n\\closein\\dataset\n" +
            "\\plotdata\\par\\metricdata\\par\\input{bibliography.bbl}\n\\end{document}\n");
        var classify = typeof(MainWindow).GetMethod("IsPotentialCompileInput", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Watches(string relative) => (bool)classify.Invoke(null, [folder, Path.Combine(folder, relative)])!;
        foreach (var path in new[] { "plot.csv", "metrics.dat", "data/custom.input", "data/noextension", "chapters/main.tex" })
            Check(Watches(path), $"Potential dynamic dependency was excluded: {path}");
        foreach (var path in new[] { ".texeed/build/main.pdf", ".texeed/preview/main.texeed-cache.json", ".git/index", "main.aux", "main.log", "main.synctex.gz", "main.run.xml", "main.fls", "bibliography.bbl" })
            Check(!Watches(path), $"Generated compiler/cache output can invalidate every build: {path}");

        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
            new AppSettings { Engine = TexEngine.Tectonic, EnginePath = Path.Combine(root, ".tools", "tectonic", "tectonic.exe"), LivePreview = false, FollowMode = PreviewFollowMode.None });
        await CallAsync(window, "OpenAsync", main); Invoke(window, "OpenAiTab");
        var runner = window.CreateAiToolRunner();
        var project = (TexProject)Field(window, "_project")!;
        Check(!project.Files.Any(f => f.Path == csv || f.Path == dat), "Fixture no longer exercises dependencies absent from the static source graph");
        Check(project.Files.Any(f => f.Path == bibliography && f.IsEditable), "Fixture did not load the explicit .bbl source dependency");
        var result = await runner("compile_project", "{}", CancellationToken.None);
        Check(result.StartsWith("编译成功"), "Stable dynamic-data fixture did not compile: " + result);
        Check((int)Field(window, "_pdfRevision")! == (int)Field(window, "_revision")!, "Stable dynamic-data preview was marked stale");
        var compiler = (CompileService)Field(window, "_compiler")!;
        var gate = (SemaphoreSlim)typeof(CompileService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compiler)!;
        int SnapshotVersion()
        {
            var snapshot = Field(window, "_activeCompileInputs");
            return snapshot is null ? -1 : (int)snapshot.GetType().GetProperty("ChangeVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(snapshot)!;
        }
        async Task DrainExternalAsync()
        {
            await CallAsync(window, "ReloadExternalAsync");
            await WaitUntilAsync(() => !(bool)Field(window, "_externalChangePending")! && !(bool)Field(window, "_refreshing")!, "External dependency refresh did not finish");
        }
        async Task CheckStableAsync(string context)
        {
            await DrainExternalAsync();
            result = await runner("compile_project", "{}", CancellationToken.None);
            Check(result.StartsWith("编译成功"), "Unchanged retry remained stale after " + context + ": " + result);
            Check((int)Field(window, "_pdfRevision")! == (int)Field(window, "_revision")!, "Unchanged retry did not restore preview after " + context);
        }
        foreach (var data in new[] { csv, dat })
        {
            await DrainExternalAsync(); await gate.WaitAsync(); Task<string> pending;
            try
            {
                pending = runner("compile_project", "{}", CancellationToken.None);
                await WaitUntilAsync(() => Field(window, "_compileTask") is Task, "Dynamic-data compiler never started");
                var version = SnapshotVersion();
                File.AppendAllText(data, "Changed while compiling.\n");
                await WaitUntilAsync(() => SnapshotVersion() > version, "Watcher did not observe dynamic data: " + Path.GetFileName(data));
            }
            finally { gate.Release(); }
            result = await pending.WaitAsync(TimeSpan.FromSeconds(60));
            Check(!result.StartsWith("编译成功") && result.Contains("过期"), "Dynamic dependency changed during compilation but tool claimed current success: " + Path.GetFileName(data));
            Check(!(bool)Field(window, "_lastCompileDependenciesCurrent")!, "Dynamic dependency change retained a current compile snapshot");
            await CheckStableAsync(Path.GetFileName(data));
        }

        // Exercise the real event subscription without needing a nondeterministic
        // OS buffer flood. The identical Error callback handles actual overflow.
        await DrainExternalAsync(); await gate.WaitAsync(); Task<string> overflow;
        try
        {
            overflow = runner("compile_project", "{}", CancellationToken.None);
            await WaitUntilAsync(() => Field(window, "_compileTask") is Task, "Overflow regression compiler never started");
            var version = SnapshotVersion(); var watcher = (FileSystemWatcher)Field(window, "_watcher")!;
            typeof(FileSystemWatcher).GetMethod("OnError", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(watcher, [new ErrorEventArgs(new InternalBufferOverflowException("Synthetic lost dependency events"))]);
            await WaitUntilAsync(() => SnapshotVersion() > version, "Watcher error did not invalidate the active snapshot");
            Check((int)Field(window, "_pdfRevision")! < 0, "Watcher overflow left the displayed PDF eligible for synchronization");
        }
        finally { gate.Release(); }
        result = await overflow.WaitAsync(TimeSpan.FromSeconds(60));
        Check(!result.StartsWith("编译成功") && result.Contains("过期"), "Watcher overflow allowed compile success without verified input tracking");
        await CheckStableAsync("watcher overflow");

        // The data watcher must also retire an already-published PDF outside a build.
        File.AppendAllText(dat, "Changed after publication.\n");
        await WaitUntilAsync(() => (int)Field(window, "_pdfRevision")! < 0, "Published preview ignored a later dynamic-data change");
        Check(true, "Published dynamic-data preview retirement");
        await CheckStableAsync("post-publication data edit");

        // Known sources override suffix filtering, even when they look like
        // compiler output. This exercises the actual watcher after publication.
        File.AppendAllText(bibliography, "Updated bibliography content.\n");
        await WaitUntilAsync(() => (int)Field(window, "_pdfRevision")! < 0, "Published preview ignored a changed explicit .bbl dependency");
        Check(true, "Published .bbl source preview retirement");
        await CheckStableAsync("post-publication .bbl source edit");
        Console.WriteLine($"AI compile dependencies: {checks} checks passed for dynamic CSV/DAT inputs, explicit .bbl sources, generated-output filtering, watcher overflow, published preview retirement and stable retries.");
    }
}
