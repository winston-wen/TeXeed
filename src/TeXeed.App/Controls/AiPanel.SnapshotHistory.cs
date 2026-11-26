using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private Action? SnapshotHistoryAction(AiEntry entry, AiTurn? citingTurn)
    {
        if (entry.AnswerAnchor is not { } anchor ||
            !AiSessionExcerptReferences.TryResolve(_book, entry, out var conversation, out _, out _) || conversation is null) return null;
        var number = conversation.Turns.FindIndex(item => item.Id == anchor.TurnId) + 1;
        var turn = conversation.Turns[number - 1];
        if (turn.RevisionHistory is not { Count: > 1 } history) return null;
        var latest = history[^1];
        if (latest.Id == anchor.CapturedRevisionId || !latest.Sources.TryGetValue(anchor.SegmentId, out var current)) return null;
        var prior = history.Where(v => anchor.CapturedRevisionId is not null ? v.Id == anchor.CapturedRevisionId :
            v.Sources.TryGetValue(anchor.SegmentId, out var text) && AiStorage.Hash(text) == anchor.Revision).ToArray();
        if (prior.Length == 0 || !prior[0].Sources.TryGetValue(anchor.SegmentId, out var captured) || captured == current) return null;

        var index = citingTurn?.SentAnnotations.FindIndex(e => e.Id == entry.Id) ?? -1;
        var linkId = conversation.Id == _book.Active.Id && index >= 0 ? $"turn:{citingTurn!.Id}:{index}" :
            AiSessionExcerptReferences.IsBranchOrigin(_book.Active, entry) ? _book.Active.Id : null;
        if (!ChangedExcerpt(anchor, prior[0], latest, linkId, out var rowId)) return null;
        if (prior.Length == 1) return () => ShowRevisionHistory(turn, prior[0].Id, number, rowId);
        // Older anchors identify the selected segment, not the version of the rest of the turn.
        return () =>
        {
            var view = LiteralBranchOrigin(anchor);
            view.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var window = new Window { Title = $"第 {number} 轮 · 批注时的原文", Owner = Window.GetWindow(this),
                Content = view, Width = 680, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.SetResourceReference(BackgroundProperty, "EditorBackground");
            window.Show();
        };
    }

    private static bool ChangedExcerpt(AiAnswerAnchor anchor, AiTurnRevision captured, AiTurnRevision latest, string? linkId, out string? rowId)
    {
        rowId = null;
        var owner = linkId is null ? null : captured.Rows.FirstOrDefault(row => row.Links.Any(link => link.Id == linkId));
        if (owner is null)
        {
            var candidates = captured.Rows.Where(row => row.SegmentId == anchor.SegmentId).ToArray();
            if (candidates.Length == 0) return false;
            var reference = new SourceReference("", "", anchor, anchor.PreviewBefore, anchor.PreviewAfter);
            (owner, _) = LocateSourceReference(candidates, reference, captured.Id != "original");
        }
        rowId = owner.Id;
        var currentOwner = linkId is null ? null : latest.Rows.FirstOrDefault(row => row.Links.Any(link => link.Id == linkId));
        currentOwner ??= latest.Rows.FirstOrDefault(row => row.Id == owner.Id);
        var normalized = AiReferencePresentation.NormalizePreview(anchor);
        if (normalized is { PreviewKind: "formula", PreviewSource: { Length: > 0 } formula })
        {
            if (currentOwner?.Text == owner.Text) return false;
            if (currentOwner is null) return true;
            if (AiRevisionText.IsDisplayFormula(currentOwner.Text))
                return !AiEquationIndex.Read(currentOwner.Text).Any(t => t.Display && t.Source == formula);
            return !MarkdownView.RevisionMath(currentOwner.Text, 0).Tokens.Values.Any(t => t.Reference is null && t.Source == formula);
        }
        var quote = anchor.Quote.Trim().ReplaceLineEndings("\n");
        static string Visible(AiRevisionRow row) => AiRevisionText.SelectionText(row.Text).Text.ReplaceLineEndings("\n");
        if (quote.Length == 0) return false;
        if (Visible(owner).Contains(quote, StringComparison.Ordinal))
        {
            if (currentOwner is null) return true;
            if (currentOwner.Text == owner.Text) return false;
            if (currentOwner.Links.FirstOrDefault(link => link.Id == linkId) is { } end)
            {
                var visible = AiRevisionText.SelectionText(currentOwner.Text);
                var boundary = visible.Ends.FindLastIndex(sourceEnd => sourceEnd <= end.Offset) + 1;
                return !visible.Text[..boundary].ReplaceLineEndings("\n").TrimEnd().EndsWith(quote, StringComparison.Ordinal);
            }
            return !Visible(currentOwner).Contains(quote, StringComparison.Ordinal);
        }
        // A selection can start several paragraphs before the row holding its end.
        string Joined(AiTurnRevision version) => string.Join("\n", version.Rows.Where(row => row.SegmentId == anchor.SegmentId).Select(Visible));
        return Joined(captured).Contains(quote, StringComparison.Ordinal) && !Joined(latest).Contains(quote, StringComparison.Ordinal);
    }
}
