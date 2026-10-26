using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiCompileEntriesAsync(MainWindow window, string root, string output)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var folder = Path.Combine(output, "compile-entry-project"); Directory.CreateDirectory(folder);
        var fragment = Path.Combine(folder, "fragment.tex");
        File.WriteAllText(fragment, "\\section{Fragment}\r\nDraft body.\r\n");
        File.WriteAllText(Path.Combine(folder, "preamble.tex"), "\\newcommand{\\projectword}{Project}\n");
        var wrapper = Path.Combine(folder, "main.tex");
        File.WriteAllText(wrapper, "\\documentclass{article}\n\\input{preamble}\n\\begin{document}\nWrapper-only heading.\\par\n\\projectword\\input{fragment}\n\\end{document}\n");
        var broken = Path.Combine(folder, "broken.tex");
        File.WriteAllText(broken, "\\documentclass{article}\n\\begin{document}\\undefinedCommandHere\\end{document}");
        var second = Path.Combine(folder, "second.tex");
        File.WriteAllText(second, "\\documentclass{article}\n\\begin{document}Second preview.\\end{document}");
        Directory.CreateDirectory(Path.Combine(folder, "validation"));
        var nested = Path.Combine(folder, "validation", "nested.tex");
        File.WriteAllText(nested, "\\documentclass{article}\n\\begin{document}Nested preview.\\end{document}");
        var sameName = Path.Combine(folder, "validation", "main.tex");
        var nestedPreamble = Path.Combine(folder, "validation", "preamble.tex");
        File.WriteAllText(nestedPreamble, "\\newcommand{\\projectword}{Nested template}\n\\typeout{NESTED-TEMPLATE-SELECTED}\n");
        File.WriteAllText(sameName, "\\documentclass{article}\n\\input{preamble}\n\\begin{document}\n\\projectword\\par\n\\input{../fragment}\n\\end{document}");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window,
            new AppSettings { Engine = TexEngine.Tectonic, EnginePath = Path.Combine(root, ".tools", "tectonic", "tectonic.exe"), LivePreview = false, FollowMode = PreviewFollowMode.None });
        await CallAsync(window, "OpenAsync", fragment); Invoke(window, "OpenAiTab");
        var runner = window.CreateAiToolRunner();
        var read = await runner("read_source", "{\"file\":\"fragment.tex\",\"start_line\":1,\"line_count\":30}", CancellationToken.None);
        var hash = Regex.Match(read, "SHA256=([A-F0-9]+)").Groups[1].Value;
        await runner("replace_source", JsonSerializer.Serialize(new { file = "fragment.tex", expected_sha256 = hash, old_text = "Draft body.", new_text = "Updated body." }), CancellationToken.None);
        var result = await runner("compile_project", "{\"entry_file\":\"main.tex\"}", CancellationToken.None);
        Check(result.StartsWith("编译成功") && result.Contains("入口：main.tex"), "Alternate wrapper did not compile successfully");
        Check(result.Contains("当前默认入口仍为 fragment.tex") && result.Contains("不能证明默认入口"), "Alternate compile success claimed to repair the default entry");
        Check(File.ReadAllText(fragment).Contains("Updated body."), "Alternate compile did not first save the current buffer");
        Check(((TexProject)Field(window, "_project")!).MainPath == fragment, "Alternate compilation changed the workspace's default main file");
        Check(((TabWorkspace)Field(window, "_workspaceTabs")!).FocusedTab == TabWorkspace.AiKey, "Alternate compilation changed the selected chat tab");
        Check(((TexProject)Field(window, "_previewProject")!).MainPath == wrapper, "PDF source mapping still points at the default fragment");
        var wrapperPdf = (string)Field(window, "_pdfPath")!;
        Check(File.Exists(wrapperPdf) && result.Contains(Path.GetRelativePath(folder, wrapperPdf)), "Compile result omitted the actual published PDF path");
        var previewProject = (TexProject)Field(window, "_previewProject")!;
        var settings = (AppSettings)Field(window, "_settings")!;
        await CallAsync(window, "ReloadExternalAsync");
        await WaitUntilAsync(() => !(bool)Field(window, "_externalChangePending")! && !(bool)Field(window, "_refreshing")!, "Pending source-save notifications blocked inverse navigation");
        var point = await ((SyncTexService)Field(window, "_sync")!).ForwardAsync(previewProject, wrapperPdf, new SourceLocation(wrapper, 4), settings);
        Check(point is not null, "Alternate-only wrapper text has no SyncTeX mapping");
        await CallAsync(window, "InverseAsync", point!);
        Check(((TexProject)Field(window, "_project")!).Files.Any(f => f.Path == wrapper) && ((LatexEditor)window.FindName("LeftEditor")).CurrentFilePath == wrapper, "Inverse navigation could not open a verified alternate-only source");
        Check(((TexProject)Field(window, "_project")!).MainPath == fragment, "Inverse navigation changed the default compile entry");
        Check(!await (Task<bool>)Invoke(window, "ImportPreviewSourceAsync", new SourceLocation(second, 1))!, "Inverse navigation imported a source outside the displayed PDF dependency graph");
        Invoke(window, "OpenAiTab");
        result = await runner("compile_project", "{}", CancellationToken.None);
        Check(result.StartsWith("编译失败") && result.Contains("入口：fragment.tex"), "Missing entry_file did not retain the default entry");
        Check((string)Field(window, "_pdfPath")! == wrapperPdf && ((TexProject)Field(window, "_previewProject")!).MainPath == wrapper, "Failed default build discarded the last alternate preview mapping");
        result = await runner("compile_project", "{\"entry_file\":\"broken.tex\"}", CancellationToken.None);
        Check(result.StartsWith("编译失败") && result.Contains("入口：broken.tex") && result.Contains("对应入口：main.tex"), "Alternate failure did not distinguish attempted target from retained PDF");
        Check(((TexProject)Field(window, "_project")!).MainPath == fragment, "Failed alternate compilation changed the default entry");
        var rejected = false;
        try { await runner("compile_project", "{\"entry_file\":\"../outside.tex\"}", CancellationToken.None); } catch (UnauthorizedAccessException) { rejected = true; }
        Check(rejected, "Alternate compile accepted an entry outside the project root");
        rejected = false;
        try { await runner("compile_project", "{\"entry_file\":\"README.md\"}", CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Alternate compile accepted a non-TeX entry");
        rejected = false;
        try { await runner("compile_project", "{\"entry_file\":\"missing.tex\"}", CancellationToken.None); } catch (FileNotFoundException) { rejected = true; }
        Check(rejected, "Alternate compile fabricated a missing entry");
        var rootPdfBytes = File.ReadAllBytes(wrapperPdf);
        var rootManifest = Path.Combine(folder, ".texeed", "preview", "main.texeed-cache.json");
        var rootManifestBytes = File.ReadAllBytes(rootManifest);
        result = await runner("compile_project", "{\"entry_file\":\"validation/main.tex\"}", CancellationToken.None);
        var sameNamePdf = (string)Field(window, "_pdfPath")!;
        Check(result.StartsWith("编译成功") && sameNamePdf != wrapperPdf && ((TexProject)Field(window, "_previewProject")!).MainPath == sameName, "Same-name nested entry reused the root output path");
        Check(((CompileResult)Field(window, "_lastCompileResult")!).Log.Contains("NESTED-TEMPLATE-SELECTED"), "Nested entry loaded the root preamble instead of the entry's template");
        var nestedPreviewProject = (TexProject)Field(window, "_previewProject")!;
        Check(nestedPreviewProject.Files.Any(f => f.Path == nestedPreamble) && nestedPreviewProject.Files.Any(f => f.Path == fragment), "Nested preview dependency graph omitted the local template or parent fragment");
        point = await ((SyncTexService)Field(window, "_sync")!).ForwardAsync(nestedPreviewProject, sameNamePdf, new SourceLocation(fragment, 2), settings);
        Check(point is not null, "Nested preview cannot synchronize its parent fragment");
        Check((await ((SyncTexService)Field(window, "_sync")!).InverseAsync(nestedPreviewProject, sameNamePdf, point!, settings))?.Path == fragment, "Nested preview inverse lookup resolved its parent fragment outside the actual entry directory");
        Check(rootPdfBytes.SequenceEqual(File.ReadAllBytes(wrapperPdf)) && rootManifestBytes.SequenceEqual(File.ReadAllBytes(rootManifest)), "Nested compilation overwrote the root PDF or cache manifest");
        var cacheService = new CompileService();
        Check((await cacheService.GetCachedPreviewAsync(previewProject, settings)) is { IsCurrent: true } rootCache && rootCache.PdfPath == wrapperPdf, "Same-name nested build invalidated/replaced root cache");
        result = await runner("compile_project", "{\"entry_file\":\"validation/nested.tex\"}", CancellationToken.None);
        Check(result.StartsWith("编译成功") && ((TexProject)Field(window, "_previewProject")!).MainPath == nested, "Nested alternate entry was not compiled/mapped");
        Check(((TexProject)Field(window, "_previewProject")!).Root == folder && ((TexProject)Field(window, "_project")!).MainPath == fragment, "Nested entry changed the authorized root or default main file");
        var nestedPdf = (string)Field(window, "_pdfPath")!;

        var compiler = (CompileService)Field(window, "_compiler")!;
        var gate = (SemaphoreSlim)typeof(CompileService).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(compiler)!;
        await gate.WaitAsync();
        using (var cancel = new CancellationTokenSource())
        {
            try
            {
                var pending = runner("compile_project", "{\"entry_file\":\"second.tex\"}", cancel.Token);
                await WaitUntilAsync(() => Field(window, "_compileTask") is Task, "Alternate compiler never started");
                cancel.Cancel(); rejected = false;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected && !(bool)Field(window, "_building")!, "Cancelled alternate compiler remained active");
                Check((string)Field(window, "_pdfPath")! == nestedPdf, "Cancelled alternate compile replaced the preview");
            }
            finally { gate.Release(); }
        }
        await gate.WaitAsync();
        Task<string> stale;
        try
        {
            stale = runner("compile_project", "{\"entry_file\":\"second.tex\"}", CancellationToken.None);
            await WaitUntilAsync(() => Field(window, "_compileTask") is Task, "Stale alternate compiler never started");
            Invoke(window, "ActivateFile", fragment);
            ((LatexEditor)window.FindName("LeftEditor")).Document.Insert(0, "% typed during validation\r\n");
        }
        finally { gate.Release(); }
        result = await stale.WaitAsync(TimeSpan.FromSeconds(60));
        Check(!result.StartsWith("编译成功") && result.Contains("过期"), "Compile success was claimed after the user changed a buffer during validation");
        Check((string)Field(window, "_pdfPath")! == nestedPdf && ((TexProject)Field(window, "_previewProject")!).MainPath == nested, "Stale alternate result replaced the displayed preview");
        await runner("save_project", "{}", CancellationToken.None);
        Check(((TexProject)Field(window, "_project")!).MainPath == fragment, "Temporary verification target persisted as the user's project entry");
        result = await runner("compile_project", "{\"entry_file\":\"main.tex\"}", CancellationToken.None);
        Check(result.StartsWith("编译成功"), "Could not refresh wrapper before external-dependency regression");
        var preamble = Path.Combine(folder, "preamble.tex"); var preambleText = File.ReadAllText(preamble);
        File.AppendAllText(preamble, "% external edit to alternate-only dependency\n");
        await WaitUntilAsync(() => (int)Field(window, "_pdfRevision")! < 0, "An alternate-only dependency change did not invalidate PDF synchronization");
        Check(((TexProject)Field(window, "_project")!).MainPath == fragment, "External alternate dependency change changed the default entry");
        File.WriteAllText(preamble, preambleText);
        await gate.WaitAsync();
        Task<string> externalStale;
        try
        {
            externalStale = runner("compile_project", "{\"entry_file\":\"main.tex\"}", CancellationToken.None);
            await WaitUntilAsync(() => Field(window, "_compileTask") is Task, "External-dependency stale compiler never started");
            File.AppendAllText(preamble, "% changed while validating alternate entry\n");
        }
        finally { gate.Release(); }
        result = await externalStale.WaitAsync(TimeSpan.FromSeconds(60));
        Check(!result.StartsWith("编译成功") && result.Contains("过期"), "Compile success was claimed after an alternate-only dependency changed during validation");
        File.WriteAllText(preamble, preambleText);
        Console.WriteLine($"AI compile entries: {checks} checks passed for real wrapper/nested compilation, default-entry preservation, saved edits, actual preview association, failure/cancellation/staleness and path bounds.");
    }
}
