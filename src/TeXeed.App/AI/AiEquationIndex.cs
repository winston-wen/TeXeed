using System.Globalization;
using System.Runtime.CompilerServices;
using TeXeed.App.Controls;

namespace TeXeed.App.AI;

internal sealed record AiEquationTarget(string TurnId, string SegmentId, ChatMath.Token Equation);

internal static class AiEquationIndex
{
    // Cache only immutable source, never a turn number or a mutable segment.
    private static readonly ConditionalWeakTable<string, SegmentIndex> Sources = new();

    private sealed class SegmentIndex
    {
        internal readonly ChatMath.Token[] Tokens;
        internal readonly Dictionary<string, ChatMath.Token?> Keys = [];
        internal SegmentIndex(string markdown)
        {
            Tokens = MarkdownView.RevisionMath(markdown, 0).Tokens.Values
                .Where(token => token.Display || token.Reference is not null).ToArray();
            foreach (var token in Tokens)
            {
                if (!token.Display || token.Number is not { } number) continue;
                void Add(string key)
                {
                    if (!Keys.TryAdd(key, token) && !ReferenceEquals(Keys[key], token)) Keys[key] = null;
                }
                Add(number); Add("eq-" + number);
                if (token.Label is { } label) Add(label);
            }
        }
    }

    private static SegmentIndex? Index(string markdown) => markdown.IndexOfAny(['$', '\\']) < 0
        ? null : Sources.GetValue(markdown, static text => new SegmentIndex(text));

    internal static IReadOnlyList<ChatMath.Token> Read(string markdown)
    {
        return Index(markdown)?.Tokens ?? [];
    }

    internal static bool TrySplit(string reference, out int turnNumber, out string localKey)
    {
        turnNumber = 0;
        localKey = reference;
        if (reference.Length == 0 || !char.IsAsciiDigit(reference[0])) return false;
        var colon = reference.IndexOf(':');
        if (colon < 1 || colon == reference.Length - 1 ||
            !int.TryParse(reference.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out turnNumber) ||
            turnNumber <= 0) return false;
        localKey = reference[(colon + 1)..];
        return true;
    }

    internal static AiTurn? TargetTurn(IReadOnlyList<AiTurn> turns, AiTurn? source, string reference)
        => TargetTurn(turns, source, reference, out _);

    internal static AiTurn? TargetTurn(IReadOnlyList<AiTurn> turns, AiTurn? source, string reference, out int number)
    {
        if (!TrySplit(reference, out number, out _)) return null;
        if (source?.EquationReferenceTurns?.TryGetValue(reference, out var id) == true)
        {
            for (var i = 0; i < turns.Count; i++)
                if (turns[i].Id == id) { number = i + 1; return turns[i]; }
            return null;
        }
        return number <= turns.Count ? turns[number - 1] : null;
    }

    internal static void BindBeforeRemoval(IReadOnlyList<AiTurn> turns, int removedIndex)
    {
        // A positional reference written before deletion must never start pointing at a different turn.
        foreach (var source in turns)
        {
            if (ReferenceEquals(source, turns[removedIndex])) continue;
            void Bind(string text)
            {
                if (!text.Contains(@"\eqref{", StringComparison.Ordinal) && !text.Contains(@"\ref{", StringComparison.Ordinal)) return;
                foreach (var token in Read(text))
                    if (token.Reference is { } key && TrySplit(key, out var n, out _) && n > removedIndex && n <= turns.Count)
                        (source.EquationReferenceTurns ??= []).TryAdd(key, turns[n - 1].Id);
            }
            Bind(source.Question);
            foreach (var segment in AiRevisionText.Segments(source)) Bind(segment.Text);
            if (source.UserRevisionDraft is { } draft)
                foreach (var row in draft.Rows) Bind(row.Text);
        }
    }

    internal static AiEquationTarget? Resolve(AiTurn turn, string localKey)
    {
        AiEquationTarget? found = null;
        var graded = AiFormGrading.IsApplied(turn);
        foreach (var segment in AiRevisionText.Segments(turn))
        {
            var source = graded ? AiFormGrading.DisplayText(segment.Text) : segment.Text;
            if (Index(source) is not { } index || !index.Keys.TryGetValue(localKey, out var token)) continue;
            if (token is null || found is not null) return null;
            found = new(turn.Id, segment.Id, token);
        }
        return found;
    }
}
