using System.Diagnostics;
using System.IO;

namespace TeXeed.App.AI;

// Each view has its own active ID; all views share models and one ordered writer.
internal sealed class AiWorkspaceStore(string project, AiNotebook book)
{
    private readonly SemaphoreSlim _gate = new(1);
    internal string Project { get; set; } = project;
    internal AiNotebook Book { get; } = book;
    internal bool EmptyCleanupBackupFailed { get; private set; }
    internal event Action? Changed;
    internal async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try { var snapshot = Book.Snapshot(); await Task.Run(() => AiStorage.Save(Project, snapshot)); }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    internal AiNotebook View(string id) => new() { Conversations = Book.Conversations, ActiveConversationId = id };
    internal static async Task<AiWorkspaceStore> LoadAsync(string project, Func<bool>? otherInstanceRunning = null, Action<string, string>? copyBackup = null)
    {
        var book = await Task.Run(() => AiStorage.Load(project));
        var store = new AiWorkspaceStore(project, book);
        if (book.EmptyNewConversations().Count > 1 && !(otherInstanceRunning ?? OtherTeXeedInstanceRunning)())
        {
            if (TryBackupBeforeEmptyCleanup(project, copyBackup))
            {
                if (book.PruneEmptyNewConversations()) book.RequiresSave = true;
            }
            else store.EmptyCleanupBackupFailed = true;
        }
        if (book.RequiresSave || AiTurnCheckpoint.PathsFor(project).Any()) await store.SaveAsync();
        return store;
    }

    internal bool RemoveClosedDuplicateBlank(string id, Func<bool>? otherInstanceRunning = null, Action<string, string>? copyBackup = null)
    {
        EmptyCleanupBackupFailed = false;
        var closed = Book.Conversations.FirstOrDefault(c => c.Id == id);
        if (closed is null || !Book.IsEmptyNewConversation(closed)) return false;
        var other = Book.Conversations.FirstOrDefault(c => c.Id != id && Book.IsEmptyNewConversation(c));
        if (other is null || (otherInstanceRunning ?? OtherTeXeedInstanceRunning)()) return false;
        if (!TryBackupBeforeEmptyCleanup(Project, copyBackup)) { EmptyCleanupBackupFailed = true; return false; }
        Book.Conversations.Remove(closed);
        if (Book.ActiveConversationId == id) Book.ActiveConversationId = other.Id;
        return true;
    }

    private static bool TryBackupBeforeEmptyCleanup(string project, Action<string, string>? copyBackup)
    {
        var path = AiStorage.NotebookPath(project);
        try
        {
            if (File.Exists(path))
            {
                var backup = path + ".blank-cleanup-" + Guid.NewGuid().ToString("N") + ".bak";
                if (copyBackup is null) File.Copy(path, backup); else copyBackup(path, backup);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool OtherTeXeedInstanceRunning()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName("TeXeed");
            try { return processes.Any(process => process.Id != current.Id); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return true; }
    }
}
