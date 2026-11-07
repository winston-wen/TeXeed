using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using TeXeed.App.AI;

internal static partial class Program
{
    private static async Task CheckAiDocumentResourceVersionsAsync(string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var project = Path.Combine(output, "resource-version-project", "main.tex");
        var conversation = "resource-" + Guid.NewGuid().ToString("N");
        var directory = AiDocumentRepository.GetDirectory(project, conversation);
        Directory.CreateDirectory(Path.Combine(directory, "图像"));
        var image = Path.Combine(directory, "图像", "图 1.png");
        var bib = Path.Combine(directory, "references.bib");
        await File.WriteAllBytesAsync(image, [1, 2, 3, 4]);
        await File.WriteAllTextAsync(bib, "@book{a,title={First}}", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(directory, ".gitignore"), "*.bib\n", new UTF8Encoding(false));
        var document = new AiSharedDocument(); Check(document.TryInitialize("\\documentclass{article}\n\\begin{document}\nA\\end{document}", "model"), "Could not initialize test document");
        var first = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "model", "first");
        var tracked = await RunGit(directory, "ls-files", "-z");
        Check(tracked.Contains("references.bib\0") && tracked.Contains("图像/图 1.png\0"), "Session resources were omitted, including ignored bib");
        await File.WriteAllBytesAsync(image, [5, 6, 7, 8]);
        await File.WriteAllTextAsync(bib, "@book{a,title={Second}}", new UTF8Encoding(false));
        var extra = Path.Combine(directory, "图像", "extra.png"); await File.WriteAllBytesAsync(extra, [9, 10]);
        var second = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "resource-only change");
        Check(first != second, "Resource-only changes did not create a version");
        var restore = await AiDocumentRepository.RestoreAssetsAsync(project, conversation, first, second);
        Check((await File.ReadAllBytesAsync(image)).SequenceEqual(new byte[] { 1, 2, 3, 4 }) && (await File.ReadAllTextAsync(bib)).Contains("First") && !File.Exists(extra), "Historical image, bibliography or absent resource were not restored");
        Check(restore.Rollback() && (await File.ReadAllBytesAsync(image)).SequenceEqual(new byte[] { 5, 6, 7, 8 }) && File.Exists(extra), "Rollback failed to preserve pre-restore resource state");
        var prepared = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "before restoring");
        var gitDirectory = Path.Combine(directory, ".git"); var index = Path.Combine(gitDirectory, "index"); var lockFile = index + ".lock";
        var orphanToken = Guid.NewGuid().ToString("N"); await File.WriteAllTextAsync(lockFile, "TeXeed-index-lock:" + orphanToken, new UTF8Encoding(false));
        _ = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "recover orphan lock");
        Check(!File.Exists(lockFile), "Recognized orphan lock was not recovered");
        await File.WriteAllTextAsync(lockFile, "OTHER GIT OPERATION", new UTF8Encoding(false));
        var otherLockBlocked = false; try { _ = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "protect foreign lock"); } catch (IOException) { otherLockBlocked = true; }
        Check(otherLockBlocked && (await File.ReadAllTextAsync(lockFile)) == "OTHER GIT OPERATION", "Unknown Git lock was removed"); File.Delete(lockFile);

        var token = Guid.NewGuid().ToString("N"); var indexName = "texeed-index-" + token; var journalFile = Path.Combine(gitDirectory, "texeed-save-journal.json");
        var sourcePath = Path.Combine(directory, "document.tex"); var reviewPath = Path.Combine(directory, "review.json");
        var beforeSource = await File.ReadAllBytesAsync(sourcePath); var beforeReview = await File.ReadAllBytesAsync(reviewPath);
        var interruptedSource = Encoding.UTF8.GetBytes("interrupted source"); var interruptedReview = Encoding.UTF8.GetBytes("interrupted review");
        var baselineHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(index)));
        var journal = new { Token = token, TemporaryIndex = indexName, BaseIndexHash = baselineHash, BaseHead = prepared, TargetTree = "", BeforeSource = beforeSource, BeforeReview = beforeReview, AfterSource = interruptedSource, AfterReview = interruptedReview };
        await File.WriteAllTextAsync(journalFile, JsonSerializer.Serialize(journal), new UTF8Encoding(false));
        await File.WriteAllTextAsync(lockFile, "TeXeed-index-lock:" + token, new UTF8Encoding(false));
        await File.WriteAllBytesAsync(sourcePath, interruptedSource); await File.WriteAllBytesAsync(reviewPath, interruptedReview);
        _ = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "recover interrupted save");
        Check(!File.Exists(journalFile) && !File.Exists(lockFile) && (await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(beforeSource) && (await File.ReadAllBytesAsync(reviewPath)).SequenceEqual(beforeReview), "Interrupted pre-commit save was not safely rolled back");

        var priorHead = (await RunGit(directory, "rev-parse", "HEAD")).Trim(); token = Guid.NewGuid().ToString("N"); indexName = "texeed-index-" + token;
        var alternate = Path.Combine(gitDirectory, indexName); File.Copy(index, alternate);
        baselineHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(index)));
        await File.WriteAllBytesAsync(image, [11, 12, 13]);
        await RunGitWithIndex(directory, alternate, "add", "-A", "-f", "--", ".");
        var targetTree = (await RunGitWithIndex(directory, alternate, "write-tree")).Trim();
        await RunGitWithIndex(directory, alternate, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "--no-verify", "--quiet", "-m", "simulated committed interruption");
        journal = new { Token = token, TemporaryIndex = indexName, BaseIndexHash = baselineHash, BaseHead = priorHead, TargetTree = targetTree, BeforeSource = beforeSource, BeforeReview = beforeReview, AfterSource = beforeSource, AfterReview = beforeReview };
        await File.WriteAllTextAsync(journalFile, JsonSerializer.Serialize(journal), new UTF8Encoding(false)); await File.WriteAllTextAsync(lockFile, "TeXeed-index-lock:" + token, new UTF8Encoding(false));
        _ = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "recover committed index");
        Check(!File.Exists(journalFile) && !File.Exists(lockFile) && (await RunGit(directory, "status", "--porcelain")).Length == 0 && (await File.ReadAllBytesAsync(image)).SequenceEqual(new byte[] { 11, 12, 13 }), "Interrupted committed index was not recovered");
        await File.WriteAllBytesAsync(image, [5, 6, 7, 8]);
        prepared = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "before external change");
        var interruptedAssets = await AiDocumentRepository.RestoreAssetsAsync(project, conversation, first, prepared);
        Check(AiDocumentRepository.HasInterruptedResourceRestore(project, conversation), "Restore did not create a durable marker");
        var markerBlocked = false; try { _ = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "blocked during restore"); } catch (IOException) { markerBlocked = true; }
        Check(markerBlocked && AiDocumentRepository.HasInterruptedResourceRestore(project, conversation), "Automatic Git versioning ignored interrupted resource restore");
        await File.WriteAllBytesAsync(image, [77, 88]); var conflict = false;
        try { _ = await AiDocumentRepository.RecoverInterruptedAssetsAsync(project, conversation, document); } catch (IOException) { conflict = true; }
        Check(conflict && (await File.ReadAllBytesAsync(image)).SequenceEqual(new byte[] { 77, 88 }) && AiDocumentRepository.HasInterruptedResourceRestore(project, conversation), "Recovery overwrote concurrent external asset changes");
        await File.WriteAllBytesAsync(image, [1, 2, 3, 4]);
        var relative = Path.GetRelativePath(directory, image).Replace('\\', '/').ToUpperInvariant();
        var backup = Path.Combine(gitDirectory, "texeed-asset-backup-" + interruptedAssets.Token + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))));
        File.Move(image, backup, false);
        var recovery = await AiDocumentRepository.RecoverInterruptedAssetsAsync(project, conversation, document);
        Check((await File.ReadAllBytesAsync(image)).SequenceEqual(new byte[] { 5, 6, 7, 8 }) && File.Exists(extra) && !File.Exists(backup), "Recovery lost an interrupted file move or did not restore the previous resources");
        Check(document.TryRestore(recovery.ExpectedDocument, recovery.PreviousDocument), "Could not restore previous document safely");
        prepared = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "restore interrupted resources", resourceRestoreToken: recovery.Resources.Token);
        recovery.Resources.Complete();
        Check(!AiDocumentRepository.HasInterruptedResourceRestore(project, conversation) && (await RunGit(directory, "status", "--porcelain")).Length == 0, "Completed recovery left a marker or dirty index");
        await File.WriteAllTextAsync(Path.Combine(directory, "concurrent.txt"), "NEW USER FILE", new UTF8Encoding(false));
        var rejected = false;
        try { _ = await AiDocumentRepository.RestoreAssetsAsync(project, conversation, first, prepared); }
        catch (IOException) { rejected = true; }
        Check(rejected && File.Exists(Path.Combine(directory, "concurrent.txt")) && (await File.ReadAllBytesAsync(image)).SequenceEqual(new byte[] { 5, 6, 7, 8 }), "Restore overwrote an external worktree change");
        File.Delete(Path.Combine(directory, "concurrent.txt"));
        var staged = Path.Combine(directory, "manual.txt"); await File.WriteAllTextAsync(staged, "USER STAGED", new UTF8Encoding(false));
        await RunGit(directory, "add", "--", "manual.txt"); rejected = false;
        try { _ = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "blocked staged change"); }
        catch (IOException) { rejected = true; }
        Check(rejected && File.Exists(staged) && (await RunGit(directory, "diff", "--cached", "--name-only")).Contains("manual.txt"), "Manually staged resource was consumed or overwritten");
        await RunGit(directory, "reset", "--", "manual.txt"); File.Delete(staged);
        await File.WriteAllTextAsync(Path.Combine(directory, ".gitattributes"), "*.png filter=untrusted", new UTF8Encoding(false)); rejected = false;
        try { _ = await AiDocumentRepository.SaveVersionAsync(project, conversation, document, "用户", "blocked attributes"); }
        catch (IOException) { rejected = true; }
        Check(rejected && File.Exists(Path.Combine(directory, ".gitattributes")), "Git filter control file was executed or removed");
        Console.WriteLine("PASS versioned images and bib, ignored resources, crash recovery, external edits and staged-index protection");
    }

    private static async Task<string> RunGitWithIndex(string directory, string index, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.Environment["GIT_INDEX_FILE"] = index; foreach (var item in arguments) start.ArgumentList.Add(item);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start git.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error); return await output;
    }

    private static async Task<string> RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var item in arguments) start.ArgumentList.Add(item);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start git.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error); return await output;
    }
}
