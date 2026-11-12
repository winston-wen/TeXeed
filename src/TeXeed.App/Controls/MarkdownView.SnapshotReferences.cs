using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Markdig.Syntax.Inlines;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class MarkdownView
{
    internal IReadOnlyList<AiEntry>? SnapshotEntries { get; set; }
    internal string? RevisionSourceOverride { get; set; }

    private bool TryRenderSnapshotLink(Hyperlink link, LinkInline source, string key)
    {
        if (SnapshotEntries is not { } entries || !key.StartsWith("snapshot-", StringComparison.Ordinal) ||
            !int.TryParse(key.AsSpan("snapshot-".Length), out var number) || number <= 0 || number > entries.Count ||
            source.FirstChild is not LiteralInline literal || literal.NextSibling is not null ||
            literal.Content.ToString() != AiReferencePresentation.PlaceholderPrefix + number) return false;
        var entry = entries[number - 1];
        var anchor = entry.AnswerAnchor is null ? null : AiReferencePresentation.NormalizePreview(entry.AnswerAnchor);
        var formula = anchor is { PreviewKind: "formula", PreviewSource.Length: > 0 };
        var summary = anchor is { PreviewKind: "formula" or "image" } ? AiPanel.ReferencePreviewFallback(anchor) : AiTurnView.AnnotationSummary(entry);
        if (formula && (string.IsNullOrWhiteSpace(summary) || summary == anchor!.Quote)) summary = "公式";
        var text = new Run(summary); link.Inlines.Add(text);
        if (anchor is { PreviewKind: "formula" or "image" } && !string.IsNullOrWhiteSpace(entry.Question))
            link.Inlines.Add(new Run(" · " + AiTurnView.AnnotationSummary(entry)));
        if (formula)
        {
            var visual = new ChatFormula(anchor!.PreviewSource!, true, asset: FormulaAsset?.Invoke(anchor.PreviewSource!, true));
            visual.SetValue(TextElement.FontSizeProperty, FontSize);
            visual.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            visual.Cursor = Cursors.Hand;
            visual.MouseLeftButtonUp += (_, e) => { LocalLink?.Invoke(key); e.Handled = true; };
            _ = CompleteSnapshotFormulaAsync(link, text, visual, anchor.Quote, _renderVersion);
        }
        return true;
    }

    private async Task CompleteSnapshotFormulaAsync(Hyperlink link, Run fallback, ChatFormula formula, string original, int version)
    {
        await formula.RenderTask;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (version != _renderVersion || link.Parent is null || fallback.Parent != link || formula.Error is not null) return;
        formula.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (formula.DesiredSize.Width > 360 || formula.DesiredSize.Height > 64) return;
        var inline = new InlineUIContainer(formula) { Tag = original, BaselineAlignment = BaselineAlignment.Baseline };
        link.Inlines.InsertBefore(fallback, inline);
        link.Inlines.Remove(fallback);
        if (link.Parent is Paragraph paragraph) paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        LayoutVersion++;
    }
}
