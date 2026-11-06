using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TeXeed.App.Services;

namespace TeXeed.App.AI;

internal sealed record AiDocumentVersion(string Commit, string Author, DateTimeOffset CreatedAt, string Description);

/// <summary>Stores explicit, per-conversation versions outside the user's TeX project.</summary>
internal static class AiDocumentRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static string GetDirectory(string project, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return Path.Combine(AiStorage.Home, "documents", AiStorage.Hash(Path.GetFullPath(project).ToUpperInvariant())[..32], AiStorage.Hash(conversationId)[..32]);
    }

    internal static async Task<string> SaveVersionAsync(string project, string conversationId, AiSharedDocument document,
        string author, string description, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        var directory = GetDirectory(project, conversationId);
        var snapshot = document.Snapshot();
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(directory) && !Directory.Exists(Path.Combine(directory, ".git")) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new IOException("会话版本目录含有未识别的文件，未覆盖任何内容。" );
            Directory.CreateDirectory(directory);
            if (!Directory.Exists(Path.Combine(directory, ".git"))) await Git(directory, cancellationToken, "init", "--quiet");
            var root = (await Git(directory, cancellationToken, "rev-parse", "--show-toplevel")).Trim();
            if (!string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new IOException("会话版本仓库的位置不匹配，未写入版本。" );
            if (!string.IsNullOrWhiteSpace(await Git(directory, cancellationToken, "status", "--porcelain", "--untracked-files=all")))
                throw new IOException("会话版本仓库包含未提交的文件，未覆盖任何内容。" );

            WriteAtomic(Path.Combine(directory, "document.tex"), snapshot.Source);
            WriteAtomic(Path.Combine(directory, "review.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            await Git(directory, cancellationToken, "add", "-f", "--", "document.tex", "review.json");
            var status = await Git(directory, cancellationToken, "status", "--porcelain");
            if (string.IsNullOrWhiteSpace(status)) return (await Git(directory, cancellationToken, "rev-parse", "HEAD")).Trim();
            var name = new string(author.Where(c => !char.IsControl(c) && c is not ('<' or '>')).Take(200).ToArray()).Trim();
            if (name.Length == 0) name = "用户";
            const string email = "author@texeed.local";
            var message = string.IsNullOrWhiteSpace(description) ? "保存文稿" : description.Trim();
            await Git(directory, cancellationToken, "-c", "user.name=TeXeed", "-c", "user.email=local@texeed.local", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(directory, ".texeed-no-hooks"), "commit", "--no-verify", "--quiet", "--author=" + name + " <" + email + ">", "-m", message);
            return (await Git(directory, cancellationToken, "rev-parse", "HEAD")).Trim();
        }
        finally { gate.Release(); }
    }

    internal static async Task<IReadOnlyList<AiDocumentVersion>> ListVersionsAsync(string project, string conversationId, CancellationToken cancellationToken = default)
    {
        var directory = GetDirectory(project, conversationId);
        if (!Directory.Exists(Path.Combine(directory, ".git"))) return [];
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var log = await Git(directory, cancellationToken, "log", "--format=%H%x00%an%x00%aI%x00%s%x00");
            var versions = new List<AiDocumentVersion>();
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimEnd('\r').Split('\0');
                if (fields.Length >= 4 && DateTimeOffset.TryParse(fields[2], out var date)) versions.Add(new(fields[0], fields[1], date, fields[3]));
            }
            return versions;
        }
        finally { gate.Release(); }
    }

    internal static async Task<AiSharedDocument> ReadVersionAsync(string project, string conversationId, string commit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid version identifier.", nameof(commit));
        var directory = GetDirectory(project, conversationId);
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var json = await Git(directory, cancellationToken, "show", commit + ":review.json");
            return JsonSerializer.Deserialize<AiSharedDocument>(json) ?? throw new InvalidDataException("无法读取会话版本。" );
        }
        finally { gate.Release(); }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, Utf8, leaveOpen: true))
            { writer.Write(content); writer.Flush(); file.Flush(flushToDisk: true); }
            AtomicFile.Replace(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string> Git(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase) || k is "GIT_DIR" or "GIT_WORK_TREE" or "GIT_INDEX_FILE" or "GIT_OBJECT_DIRECTORY" or "GIT_ALTERNATE_OBJECT_DIRECTORIES" or "GIT_COMMON_DIR").ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".texeed-no-config");
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.autocrlf=false");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.longpaths=true");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.attributesFile=" + Path.Combine(directory, ".texeed-no-attributes"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Git。" );
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new IOException($"Git 操作失败（{process.ExitCode}）：{error.Trim()}" );
        return output;
    }
}
