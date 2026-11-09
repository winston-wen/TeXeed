using System.IO;
using TeXeed.App.AI;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiDocumentCompilerAsync(string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var project = Path.Combine(output, "session-compile-project", "main.tex");
        var id = "compile-" + Guid.NewGuid().ToString("N");
        var root = AiDocumentRepository.GetDirectory(project, id);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "note.txt"), "test resource");
        var document = new AiSharedDocument();
        var source = "\\documentclass{article}\n\\begin{document}\nHello.\n\\end{document}\n";
        Check(document.TryInitialize(source, "model"), "Could not initialize source");
        var comment = new AiDocumentComment { Start = source.IndexOf("Hello.", StringComparison.Ordinal), Length = 6, Quote = "Hello.", Text = "Example" };
        document.Comments.Add(comment);
        var compiler = new AiDocumentCompiler();
        var settings = new AppSettings { Engine = TexEngine.PdfLaTeX };
        var builds = AiDocumentCompiler.BuildParent(project, id);
        var before = Directory.Exists(builds) ? Directory.GetDirectories(builds).Length : 0;
        var good = await compiler.CompileAsync(project, id, document, settings);
        Check(good.Preview is not null && !good.Outdated && File.Exists(good.Preview.PdfPath), "Real PDF compilation failed: " + good.Message);
        Check(good.Preview!.SourceRevision == document.Revision, "Preview revision did not match source");
        var location = await AiCommentLocator.LocateAsync(document, comment, good.Preview.SourceRevision, good.Preview.Project, good.Preview.PdfPath, settings, new SyncTexService());
        Check(location.Status == AiCommentLocationStatus.Approximate && location.Location is not null, "Real SyncTeX did not return an approximate comment location");
        Check(await AiDocumentCompiler.IsCurrentAsync(good.Preview, document), "New preview reported stale");
        await File.WriteAllTextAsync(Path.Combine(root, "note.txt"), "changed resource");
        Check(!await AiDocumentCompiler.IsCurrentAsync(good.Preview, document), "Changed session resource did not mark preview stale");
        await File.WriteAllTextAsync(Path.Combine(root, "note.txt"), "test resource");
        Check(!good.Preview.PdfPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Build output polluted the versioned resource directory");
        var first = good.Preview;
        await compiler.PublishAsync(project, id, first);
        var manifest = AiDocumentCompiler.ManifestPath(project, id);
        var originalManifest = await File.ReadAllBytesAsync(manifest);
        var reopened = new AiDocumentCompiler();
        var restored = await reopened.RestoreAsync(project, id);
        Check(restored is not null && restored.SourceRevision == first.SourceRevision && File.Exists(restored.PdfPath), "Last PDF did not survive reopening");
        await Task.WhenAll(compiler.PublishAsync(project, id, first), reopened.PublishAsync(project, id, restored!));
        Check(await new AiDocumentCompiler().RestoreAsync(project, id) is not null, "Concurrent preview publication left an invalid manifest");
        originalManifest = await File.ReadAllBytesAsync(manifest);
        var foreignId = "foreign-" + Guid.NewGuid().ToString("N");
        var foreignManifest = AiDocumentCompiler.ManifestPath(project, foreignId);
        await File.WriteAllBytesAsync(foreignManifest, originalManifest);
        Check(await new AiDocumentCompiler().RestoreAsync(project, foreignId) is null, "Preview manifest crossed conversations");
        var originalPdf = await File.ReadAllBytesAsync(first.PdfPath);
        await File.WriteAllBytesAsync(first.PdfPath, [1, 2, 3]);
        Check(await new AiDocumentCompiler().RestoreAsync(project, id) is null && File.Exists(first.PdfPath), "Corrupted PDF was accepted or removed");
        await File.WriteAllBytesAsync(first.PdfPath, originalPdf);
        await File.WriteAllTextAsync(manifest, "{ malformed");
        Check(await new AiDocumentCompiler().RestoreAsync(project, id) is null, "Malformed preview manifest was accepted");
        await File.WriteAllTextAsync(manifest, "{\"BuildId\":\"../../outside\"}");
        Check(await new AiDocumentCompiler().RestoreAsync(project, id) is null, "Unsafe preview manifest was accepted");
        await File.WriteAllBytesAsync(manifest, originalManifest);
        Check(document.TryApply(document.Revision, "\\documentclass{article}\n\\begin{document}\n\\brokencommand\n\\end{document}\n", "用户"), "Could not update source");
        Check(!await AiDocumentCompiler.IsCurrentAsync(first, document), "Changed source did not mark preview stale");
        var outdated = await AiCommentLocator.LocateAsync(document, comment, first.SourceRevision, first.Project, first.PdfPath, settings, new SyncTexService());
        Check(outdated.Status == AiCommentLocationStatus.PreviewOutdated && outdated.Location is null, "Outdated PDF retained a comment location");
        var broken = await compiler.CompileAsync(project, id, document, settings);
        Check(broken.Preview is null && compiler.LastSuccessful == first && File.Exists(first.PdfPath), "Failed build replaced the last successful PDF");
        Check((await File.ReadAllBytesAsync(manifest)).SequenceEqual(originalManifest), "Failed build replaced the persisted successful preview");
        Check(broken.Outdated && Directory.GetDirectories(builds).Length == before + 1, "Failed build did not mark the previous preview stale or clean its temporary directory");
        Check(!File.Exists(Path.Combine(root, "document.tex")) && !File.Exists(Path.Combine(root, "review.json")), "Compiler wrote session source or comments");
        Check((await File.ReadAllTextAsync(Path.Combine(root, "note.txt"))) == "test resource", "Compiler modified a resource");
        await File.WriteAllTextAsync(Path.Combine(root, "part.tex"), "Part");
        Check(document.TryApply(document.Revision, "\\documentclass{article}\n\\begin{document}\n\\input{part}\n\\end{document}\n", "用户"), "Could not prepare inclusion test");
        var included = await compiler.CompileAsync(project, id, document, settings);
        Check(included.Preview is null && included.Message.Contains("不能包含") && compiler.LastSuccessful == first, "Compiler allowed a second TeX source or replaced last preview");
        var second = new AiDocumentCompiler();
        Check(second.LastSuccessful is null, "Separate sessions shared a preview");
        Check(!compiler.Release(first) && File.Exists(first.PdfPath), "Current preview was released");
        Check(document.TryApply(document.Revision, source, "用户"), "Could not restore test source");
        var concurrent = compiler.CompileAsync(project, id, document, settings);
        Check(document.TryApply(document.Revision, source + "\n% changed during compilation", "用户"), "Could not prepare concurrent source edit");
        var changed = await concurrent;
        Check(changed.Preview is null && changed.Outdated && compiler.LastSuccessful == first, "A build against an older source replaced the current preview");
        Check(document.TryApply(document.Revision, source, "用户"), "Could not restore source after concurrent build");
        var priorBuilds = Directory.GetDirectories(builds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changingResource = compiler.CompileAsync(project, id, document, settings);
        string? active = null;
        for (var i = 0; i < 300 && !changingResource.IsCompleted; i++)
        {
            active = Directory.GetDirectories(builds).FirstOrDefault(p => !priorBuilds.Contains(p) && File.Exists(Path.Combine(p, "document.tex")));
            if (active is not null) break;
            await Task.Delay(10);
        }
        Check(active is not null, "Could not observe a build after capturing resources");
        var newResource = Path.Combine(root, "concurrent-resource.txt");
        await File.WriteAllTextAsync(newResource, "new resource during compilation");
        var changedResource = await changingResource;
        Check(changedResource.Preview is null && changedResource.Outdated && compiler.LastSuccessful == first, "A build against older resources replaced the current preview");
        File.Delete(newResource);
        var next = await compiler.CompileAsync(project, id, document, settings);
        Check(next.Preview is not null && !compiler.Release(first) && File.Exists(first.PdfPath), "A persisted preview was removed while another process may still use it");
        Console.WriteLine("Session PDF snapshots, failed build preservation and single-file check verified.");
    }
}
