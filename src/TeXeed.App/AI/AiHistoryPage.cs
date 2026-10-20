namespace TeXeed.App.AI;

// Paging and bulk selection are independent of layout and of the opened conversation.
internal sealed class AiHistoryPage(AiNotebook book)
{
    internal HashSet<string> Selected { get; } = [];
    internal IReadOnlyList<AiConversation> Matches { get; private set; } = [];
    internal int First { get; private set; }
    internal int Capacity { get; private set; } = 1;
    internal int PageCount => Math.Max(1, (Matches.Count + Capacity - 1) / Capacity);
    internal int PageNumber => First / Capacity + 1;
    internal int MaximumFirst => Math.Max(First, (PageCount - 1) * Capacity);
    internal IEnumerable<AiConversation> Visible => Matches.Skip(First).Take(Capacity);
    internal void Refresh(string query, bool reset = false)
    {
        var anchor = reset ? null : Matches.ElementAtOrDefault(First)?.Id;
        Matches = Search(book, query.Trim());
        Selected.IntersectWith(book.Conversations.Select(c => c.Id));
        var found = anchor is null ? -1 : Matches.ToList().FindIndex(c => c.Id == anchor);
        First = reset ? 0 : Math.Clamp(found >= 0 ? found : First, 0, Math.Max(0, Matches.Count - 1));
    }
    internal void Resize(int capacity) => Capacity = Math.Max(1, capacity);
    internal void Scroll(int first) => First = Math.Clamp(first, 0, Math.Max(0, Matches.Count - 1));
    internal void GoTo(int page) => Scroll((Math.Clamp(page, 1, PageCount) - 1) * Capacity);
    internal static IReadOnlyList<AiConversation> Search(AiNotebook book, string query) => book.Conversations
        .Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || c.Turns.Any(t => t.Question.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Answer.Contains(query, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(c => c.UpdatedAt).ToArray();
}
