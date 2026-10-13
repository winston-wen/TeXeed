using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiDiscoveryAsync(MainWindow window, string root, string output)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var folder = Path.Combine(output, "discovery-project"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}Body remains.\\end{document}\n");
        Directory.CreateDirectory(Path.Combine(folder, "template", "nested"));
        var templatePath = Path.Combine(folder, "template", "nested", "ruledprotocol.sty");
        var original = "% 独立协议模板\r\n\\newcommand{\\protocolname}{OriginalProtocol}\r\n";
        File.WriteAllText(templatePath, original, new UnicodeEncoding(false, true, true));
        File.WriteAllText(Path.Combine(folder, "README.md"), "Use template/nested/ruledprotocol.sty. ReadmeUniqueMarker.");
        File.WriteAllText(Path.Combine(folder, "Makefile"), "main.pdf: main.tex\n\txelatex main.tex\n");
        File.WriteAllText(Path.Combine(folder, "build.ps1"), "xelatex main.tex\n");
        var metadataPaths = new[] { ".latexmkrc", ".vscode/settings.json", ".vscode/tasks.json", ".vscode/build-note.sh", ".vscode/build.ps1", ".vscode/build-all.cmd", ".vscode/build.bat", ".vscode/build_notes.py" };
        foreach (var relative in metadataPaths)
        {
            var path = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "BuildMetadataMarker " + relative);
        }
        File.SetAttributes(Path.Combine(folder, ".vscode"), File.GetAttributes(Path.Combine(folder, ".vscode")) | FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(folder, ".latexmkrc"), FileAttributes.Hidden);
        var excludedMetadata = new[] { ".vscode/extensions.json", ".vscode/launch.json", ".vscode/private.tex", ".vscode/.env", ".vscode/build-note.lua", ".vscode/build-note.sh.bak", ".vscode/nested/settings.json", "nested/.vscode/settings.json", "nested/.latexmkrc" };
        foreach (var relative in excludedMetadata)
        {
            var path = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "ExcludedMetadataMarker");
        }
        Directory.CreateDirectory(Path.Combine(folder, ".vscode/build-directory.py"));
        File.WriteAllText(Path.Combine(folder, ".vscode/build-directory.py/hidden.tex"), "ExcludedMetadataMarker");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllBytes(Path.Combine(folder, "assets", "image.png"), [0x89, 0x50, 0x4e, 0x47, 0]);
        foreach (var ignored in new[] { ".git", ".texeed", "build", "node_modules" })
        {
            Directory.CreateDirectory(Path.Combine(folder, ignored));
            File.WriteAllText(Path.Combine(folder, ignored, "ignored.tex"), "DoNotDiscoverMarker");
        }
        File.WriteAllText(Path.Combine(folder, "main.aux"), "DoNotDiscoverMarker");
        File.WriteAllBytes(Path.Combine(folder, "invalid.tex"), [0xff, 0xff, 0x61]);
        var catalog = await Task.Run(() => AiWorkspaceFiles.Discover(folder, ".", 8, CancellationToken.None));
        Check(catalog.Entries.Any(e => e.RelativePath.EndsWith("ruledprotocol.sty") && e.IsSource), "Recursive catalog missed an unreferenced nested template");
        Check(catalog.Entries.Any(e => e.RelativePath == "README.md" && e.IsText && !e.IsSource), "README is not readable project context");
        Check(catalog.Entries.Any(e => e.RelativePath == "Makefile" && e.IsText) && catalog.Entries.Any(e => e.RelativePath == "build.ps1" && e.IsText), "Build configuration scripts cannot be read");
        Check(catalog.Entries.Any(e => e.RelativePath.EndsWith("image.png") && !e.IsText), "Resource was not listed as a resource");
        Check(catalog.Entries.All(e => !e.RelativePath.Contains("ignored.tex") && !e.RelativePath.EndsWith("main.aux")), "Catalog crawled hidden/cache/generated files");
        Check(metadataPaths.All(path => catalog.Entries.Any(e => e.RelativePath.Replace('\\', '/') == path && e.IsText && !e.IsSource)), "Read-only hidden build metadata was not discovered");
        Check(catalog.Entries.All(e => !excludedMetadata.Contains(e.RelativePath.Replace('\\', '/')) && !e.RelativePath.Contains("build-directory.py")), "Metadata allowlist exposed unrelated files or recursed into a script-named directory");
        var editorCatalog = AiWorkspaceFiles.Discover(folder, ".vscode", 8, CancellationToken.None);
        Check(editorCatalog.Entries.Count == metadataPaths.Length - 1 && editorCatalog.Entries.All(e => AiWorkspaceFiles.IsBuildMetadataFile(e.RelativePath)), "Listing .vscode exposed files beyond its build allowlist");
        var formatted = AiWorkspaceFiles.Format(catalog, []);
        Check(!formatted.Contains("OriginalProtocol") && formatted.Contains("未引用源码"), "Directory listing loaded or exposed source bodies");
        Check((await AiWorkspaceFiles.ReadAsync(folder, "template/nested/ruledprotocol.sty", CancellationToken.None)).Content == original, "UTF-16 BOM source did not decode exactly");
        var rejected = false;
        try { await AiWorkspaceFiles.ReadAsync(folder, "invalid.tex", CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid text encoding was silently replaced");
        using (var oversized = File.Create(Path.Combine(folder, "oversized.tex"))) oversized.SetLength(AiWorkspaceFiles.MaximumTextBytes + 1);
        rejected = false;
        try { await AiWorkspaceFiles.ReadAsync(folder, "oversized.tex", CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Oversized text was loaded without a byte limit");
        var search = await AiWorkspaceFiles.SearchAsync(folder, "OriginalProtocol", [], CancellationToken.None);
        Check(search.Contains("ruledprotocol.sty:2:"), "Search missed an unreferenced template");
        search = await AiWorkspaceFiles.SearchAsync(folder, "ReadmeUniqueMarker", [], CancellationToken.None);
        Check(search.Contains("README.md:1:"), "Search missed project documentation");
        var overlay = new ProjectFile(templatePath, Path.GetRelativePath(folder, templatePath), ProjectFileKind.Style, "UnsavedBufferMarker");
        search = await AiWorkspaceFiles.SearchAsync(folder, "UnsavedBufferMarker", [overlay], CancellationToken.None);
        Check(search.Contains("ruledprotocol.sty:1:"), "Search ignored unsaved content");
        search = await AiWorkspaceFiles.SearchAsync(folder, "OriginalProtocol", [overlay], CancellationToken.None);
        Check(!search.Contains("ruledprotocol.sty:2:"), "Search mixed stale disk and current buffer versions");
        foreach (var path in metadataPaths)
        {
            var metadata = await AiWorkspaceFiles.ReadAsync(folder, path, CancellationToken.None);
            Check(metadata.Content == "BuildMetadataMarker " + path, "Allowed build configuration was not readable: " + path);
            rejected = false;
            try { MainWindow.ValidateAiPath(folder, path); } catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Build metadata became eligible for a mutation path: " + path);
        }
        search = await AiWorkspaceFiles.SearchAsync(folder, "BuildMetadataMarker", [], CancellationToken.None);
        Check(metadataPaths.All(path => search.Replace('\\', '/').Contains(path + ":1:")), "Source search omitted whitelisted build metadata");
        var hiddenBuffer = new ProjectFile(Path.Combine(folder, ".vscode/private.tex"), ".vscode/private.tex", ProjectFileKind.Tex, "ExcludedMetadataMarker");
        search = await AiWorkspaceFiles.SearchAsync(folder, "ExcludedMetadataMarker", [hiddenBuffer], CancellationToken.None);
        Check(!search.Contains(".vscode") && !search.Contains("nested/.latexmkrc"), "Hidden files bypassed the search boundary through a loaded buffer");
        Check(!AiWorkspaceFiles.Format(catalog, [hiddenBuffer], compact: true).Contains("private.tex"), "Hidden buffer leaked through the compact directory listing");
        foreach (var path in excludedMetadata)
        {
            rejected = false;
            try { await AiWorkspaceFiles.ReadAsync(folder, path, CancellationToken.None); } catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Nonallowlisted hidden metadata was readable: " + path);
        }
        rejected = false;
        try { AiWorkspaceFiles.Discover(folder, ".vscode/build-directory.py", 8, CancellationToken.None); } catch (UnauthorizedAccessException) { rejected = true; }
        Check(rejected, "The file allowlist enabled arbitrary hidden directory traversal");
        Check((await AiWorkspaceFiles.ReadAsync(folder, ".VSCODE/SETTINGS.JSON", CancellationToken.None)).Content.Contains("BuildMetadataMarker"), "Windows build metadata paths were treated as case-sensitive");
        var linkTarget = Path.Combine(output, "metadata-link-target"); Directory.CreateDirectory(linkTarget);
        File.WriteAllText(Path.Combine(linkTarget, "settings.json"), "OutsideMetadataMarker");
        var linkRoot = Path.Combine(output, "metadata-linked-project"); Directory.CreateDirectory(linkRoot);
        var fileLinkCreated = false; var directoryLinkCreated = false;
        try { File.CreateSymbolicLink(Path.Combine(folder, ".vscode/build-link.sh"), Path.Combine(linkTarget, "settings.json")); fileLinkCreated = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("Metadata symlink fixture unavailable: " + ex.Message); }
        try { Directory.CreateSymbolicLink(Path.Combine(linkRoot, ".vscode"), linkTarget); directoryLinkCreated = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("Metadata directory-link fixture unavailable: " + ex.Message); }
        if (fileLinkCreated)
        {
            rejected = false;
            try { await AiWorkspaceFiles.ReadAsync(folder, ".vscode/build-link.sh", CancellationToken.None); } catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Whitelisted build script path followed a symbolic link");
            Check(AiWorkspaceFiles.Discover(folder, ".vscode", 1, CancellationToken.None).Entries.All(e => !e.RelativePath.EndsWith("build-link.sh")), "Directory listing exposed a linked build script");
        }
        if (directoryLinkCreated)
        {
            rejected = false;
            try { await AiWorkspaceFiles.ReadAsync(linkRoot, ".vscode/settings.json", CancellationToken.None); } catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Build metadata read followed a linked .vscode directory");
            rejected = false;
            try { AiWorkspaceFiles.Discover(linkRoot, ".vscode", 1, CancellationToken.None); } catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Explicit metadata listing followed a linked directory");
        }

        var paged = Path.Combine(folder, "pages"); Directory.CreateDirectory(paged);
        for (var i = 0; i < 510; i++) File.WriteAllText(Path.Combine(paged, $"page-{i:000}.md"), "small");
        var pageCatalog = await Task.Run(() => AiWorkspaceFiles.Discover(folder, "pages", 1, CancellationToken.None));
        var page1 = AiWorkspaceFiles.Format(pageCatalog, [], "pages", 0);
        var page2 = AiWorkspaceFiles.Format(pageCatalog, [], "pages", 500);
        Check(page1.Contains("offset=500") && !page1.Contains("page-500.md") && page2.Contains("page-500.md") && page2.Contains("page-509.md"), "Directory pagination skips or duplicates entries");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); rejected = false;
            try { await Task.Run(() => AiWorkspaceFiles.Discover(folder, ".", 8, cancelled.Token)); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "Cancelled directory scan continued");
        }
        typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window,
            new AppSettings { LivePreview = false, FollowMode = PreviewFollowMode.None });
        await CallAsync(window, "OpenAsync", main); Invoke(window, "OpenAiTab");
        var runner = window.CreateAiToolRunner();
        var listing = await runner("list_files", "{}", CancellationToken.None);
        Check(listing.Contains("项目根目录：") && listing.Contains("主文件：main.tex") && listing.Contains("编译引擎：") && listing.Contains("ruledprotocol.sty"), "Agent workspace summary lacks root/main/template context");
        Check(listing.Contains("README.md") && listing.Contains("Makefile"), "Agent directory listing omits supporting context");
        Check(listing.Contains(".latexmkrc") && listing.Replace('\\', '/').Contains(".vscode/build-note.sh") && listing.Contains("只读构建配置"), "Agent inventory omitted the read-only editor recipe");
        var detail = await runner("list_directory", JsonSerializer.Serialize(new { path = "template", depth = 2, offset = 0 }), CancellationToken.None);
        Check(detail.Contains("ruledprotocol.sty"), "Agent directory tool cannot descend into templates");
        detail = await runner("list_directory", JsonSerializer.Serialize(new { path = ".vscode", depth = 8, offset = 0 }), CancellationToken.None);
        Check(detail.Contains("build-note.sh") && !detail.Contains("private.tex") && !detail.Contains("extensions.json"), "Agent directory tool ignored the editor configuration allowlist");
        foreach (var path in metadataPaths)
        {
            var metadataRead = await runner("read_source", JsonSerializer.Serialize(new { file = path, start_line = 1, line_count = 20 }), CancellationToken.None);
            var metadataHash = Regex.Match(metadataRead, "SHA256=([A-F0-9]+)").Groups[1].Value;
            Check(metadataRead.Contains("BuildMetadataMarker") && metadataHash.Length == 64, "Agent could not inspect a whitelisted build recipe: " + path);
            rejected = false;
            try { await runner("replace_source", JsonSerializer.Serialize(new { file = path, expected_sha256 = metadataHash, old_text = "BuildMetadataMarker", new_text = "Changed" }), CancellationToken.None); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(folder, path)) == "BuildMetadataMarker " + path, "Read access granted write access to build metadata: " + path);
        }
        foreach (var path in new[] { ".vscode/build-new.py", ".vscode/new.tex", ".latexmkrc" })
        {
            rejected = false;
            try { await runner("create_source", JsonSerializer.Serialize(new { file = path, content = "NeverCreated" }), CancellationToken.None); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Create tool wrote a hidden build metadata path: " + path);
        }
        Check(!File.Exists(Path.Combine(folder, ".vscode/build-new.py")) && !File.Exists(Path.Combine(folder, ".vscode/new.tex")), "Rejected metadata creation left a file behind");
        var read = await runner("read_source", JsonSerializer.Serialize(new { file = "template/nested/ruledprotocol.sty", start_line = 1, line_count = 100 }), CancellationToken.None);
        var hash = Regex.Match(read, "SHA256=([A-F0-9]+)").Groups[1].Value;
        Check(read.Contains("OriginalProtocol") && hash.Length == 64, "Agent cannot read an unreferenced Unicode template");
        Check(!((TexProject)Field(window, "_project")!).Files.Any(f => f.Path == templatePath), "Read-only discovery polluted the loaded source graph");
        await runner("replace_source", JsonSerializer.Serialize(new { file = "template/nested/ruledprotocol.sty", expected_sha256 = hash, old_text = "OriginalProtocol", new_text = "RevisedProtocol" }), CancellationToken.None);
        Check(((TexProject)Field(window, "_project")!).Files.Any(f => f.Path == templatePath && f.IsEditable), "Unreferenced edit was not registered for safe saving");
        Check(((TabWorkspace)Field(window, "_workspaceTabs")!).FocusedTab == TabWorkspace.AiKey, "Importing an unreferenced source stole chat focus");
        await runner("save_project", "{}", CancellationToken.None);
        var bytes = File.ReadAllBytes(templatePath);
        Check(bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) && File.ReadAllText(templatePath) == original.Replace("OriginalProtocol", "RevisedProtocol"), "Imported source save changed BOM, newline convention or unrelated text");
        Check(((TexProject)Field(window, "_project")!).Files.Any(f => f.Path == templatePath), "Save dropped the imported detached source");
        Check(Directory.EnumerateFiles(Path.Combine(AiStorage.Home, "backups"), "*-ruledprotocol.sty", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == original), "Imported source original was not backed up");
        Invoke(window, "ActivateFile", templatePath);
        var editor = (TeXeed.App.Controls.LatexEditor)window.FindName("LeftEditor");
        editor.Undo(); Check(editor.Text == original, "Imported source edit cannot be undone after saving");
        editor.Redo(); await runner("save_project", "{}", CancellationToken.None);
        read = await runner("read_source", JsonSerializer.Serialize(new { file = "README.md", start_line = 1, line_count = 20 }), CancellationToken.None);
        hash = Regex.Match(read, "SHA256=([A-F0-9]+)").Groups[1].Value;
        rejected = false;
        try { await runner("replace_source", JsonSerializer.Serialize(new { file = "README.md", expected_sha256 = hash, old_text = "ReadmeUniqueMarker", new_text = "Changed" }), CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && File.ReadAllText(Path.Combine(folder, "README.md")).Contains("ReadmeUniqueMarker"), "Read-only project documentation became editable through source tool");
        foreach (var path in new[] { "../outside.tex", ".git/ignored.tex", "C:/outside.tex" })
        {
            rejected = false;
            try { await runner("read_source", JsonSerializer.Serialize(new { file = path, start_line = 1, line_count = 20 }), CancellationToken.None); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Unsafe discovered read accepted: " + path);
        }
        rejected = false;
        try { await runner("read_source", JsonSerializer.Serialize(new { file = "assets/image.png", start_line = 1, line_count = 20 }), CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Binary resource was interpreted as source text");
        var independent = Path.Combine(folder, "external.sty");
        File.WriteAllText(independent, "BeforeExternalChange");
        read = await runner("read_source", JsonSerializer.Serialize(new { file = "external.sty", start_line = 1, line_count = 20 }), CancellationToken.None);
        hash = Regex.Match(read, "SHA256=([A-F0-9]+)").Groups[1].Value;
        File.WriteAllText(independent, "AfterExternalChange"); rejected = false;
        try { await runner("replace_source", JsonSerializer.Serialize(new { file = "external.sty", expected_sha256 = hash, old_text = "BeforeExternalChange", new_text = "Overwritten" }), CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && File.ReadAllText(independent) == "AfterExternalChange", "Agent overwrote an external edit to an unreferenced source");
        read = await runner("read_source", JsonSerializer.Serialize(new { file = "external.sty", start_line = 1, line_count = 20 }), CancellationToken.None);
        Check(read.Contains("AfterExternalChange"), "Unreferenced source read was stale after disk change");
        var saveGate = (SemaphoreSlim)Field(window, "_saveGate")!;
        await saveGate.WaitAsync();
        Task<string> creating;
        try
        {
            creating = runner("create_source", JsonSerializer.Serialize(new { file = "after-save.tex", content = "Retained source." }), CancellationToken.None);
            await Task.Delay(50);
            Check(!creating.IsCompleted && !File.Exists(Path.Combine(folder, "after-save.tex")), "Source creation raced an in-flight project save");
        }
        finally { saveGate.Release(); }
        await creating.WaitAsync(TimeSpan.FromSeconds(5));
        await runner("save_project", "{}", CancellationToken.None);
        Check(((TexProject)Field(window, "_project")!).Files.Any(f => f.Name == "after-save.tex"), "Source created after an in-flight save was discarded");
        Console.WriteLine($"AI discovery: {checks} checks passed for recursive template/docs/assets discovery, paging, buffer precedence, bounds, encoding, safe imports, save/undo/backups, external conflicts and path confinement.");
    }
}
