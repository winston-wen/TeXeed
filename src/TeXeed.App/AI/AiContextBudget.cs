namespace TeXeed.App.AI;

/// <summary>Local token estimate with output headroom; actual API usage overrides it when available.</summary>
internal sealed class AiContextBudget(long? window, int? characterLimit = null)
{
    internal int Limit { get; } = characterLimit ?? (int)Math.Clamp((window ?? 128000) * .75, 1024, int.MaxValue);
    internal int Size(string text)
    {
        // Explicit character budgets remain available for deterministic migration tests.
        if (characterLimit is not null) return text.Length;
        long ascii = 0, unicode = 0;
        foreach (var rune in text.EnumerateRunes()) { if (rune.IsAscii) ascii++; else unicode++; }
        // Reserve space for message framing and estimate token use by character class.
        return (int)Math.Min(int.MaxValue, (ascii + 2) / 3 + unicode * 2 + 512);
    }
}
