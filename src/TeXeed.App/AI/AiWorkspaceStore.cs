namespace TeXeed.App.AI;

// Each view has its own active ID; all views share models and one ordered writer.
internal sealed class AiWorkspaceStore(string project, AiNotebook book)
{
    private readonly SemaphoreSlim _gate = new(1);
    internal string Project { get; } = project;
    internal AiNotebook Book { get; } = book;
    internal event Action? Changed;
    internal async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try { var snapshot = Book.Snapshot(); await Task.Run(() => AiStorage.Save(Project, snapshot)); }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    internal AiNotebook View(string id) => new() { Conversations = Book.Conversations, ActiveConversationId = id };
    internal static async Task<AiWorkspaceStore> LoadAsync(string project)
    {
        var book = await Task.Run(() => AiStorage.Load(project));
        var store = new AiWorkspaceStore(project, book);
        if (book.RequiresSave || AiTurnCheckpoint.PathsFor(project).Any()) await store.SaveAsync();
        return store;
    }
}
