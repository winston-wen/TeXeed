using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text;
using System.Text.RegularExpressions;
using Markdig.Syntax.Inlines;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class MarkdownView
{
    internal IReadOnlyList<AiEntry>? SnapshotEntries { get; set; }
    internal string? RevisionSourceOverride { get; set; }
    private sealed record SnapshotCopy(string Text);
    private sealed record SnapshotFormulaCopy(string Text);
    private sealed record ReadySnapshotFormula(Hyperlink Link, Run Fallback, ChatFormula Formula, string Original, int Version);
    private readonly List<ReadySnapshotFormula> _readySnapshotFormulas = [];
    private bool _snapshotFlushQueued;

    private sealed record SnapshotOrigin(AiEntry Entry, InlineCollection Inlines, Hyperlink Link)
    {
        internal Hyperlink? HistoryLink;
    }
    private List<SnapshotOrigin>? _snapshotOrigins;
    internal Func<AiEntry, Action?>? SnapshotHistory { get; set; }

    private void TrackSnapshotOrigin(AiEntry entry, InlineCollection inlines, Hyperlink link)
    {
        var origin = new SnapshotOrigin(entry, inlines, link);
        (_snapshotOrigins ??= []).Add(origin);
        UpdateSnapshotOrigin(origin);
    }

    internal void RefreshSnapshotOrigins(string? changedTurn = null)
    {
        if (_snapshotOrigins is null) return;
        foreach (var origin in _snapshotOrigins)
            if (changedTurn is null || origin.Entry.AnswerAnchor?.TurnId == changedTurn) UpdateSnapshotOrigin(origin);
    }

    private void UpdateSnapshotOrigin(SnapshotOrigin origin)
    {
        var show = SnapshotHistory?.Invoke(origin.Entry);
        if ((show is null) == (origin.HistoryLink is null)) return;
        if (IsLoaded) NotifyAsyncLayoutChanging();
        if (show is null)
        {
            origin.Inlines.Remove(origin.HistoryLink!);
            origin.HistoryLink = null;
            return;
        }
        var history = new Hyperlink(new Run("　原文已更新")) { FontSize = AiChatTypography.MetadataFontSize,
            TextDecorations = null, ToolTip = "查看批注时的版本", Tag = new SnapshotCopy("") };
        history.SetResourceReference(TextElement.ForegroundProperty, "Muted");
        history.Click += (_, _) => SnapshotHistory?.Invoke(origin.Entry)?.Invoke();
        origin.Inlines.InsertAfter(origin.Link, history);
        origin.HistoryLink = history;
    }

    private AiEntry? RenderSnapshotLink(Hyperlink link, LinkInline source, string key)
    {
        if (SnapshotEntries is not { } entries || !key.StartsWith("snapshot-", StringComparison.Ordinal) ||
            !int.TryParse(key.AsSpan("snapshot-".Length), out var number) || number <= 0 || number > entries.Count ||
            source.FirstChild is not LiteralInline literal || literal.NextSibling is not null ||
            literal.Content.ToString() != AiReferencePresentation.PlaceholderPrefix + number) return null;
        link.Name = "SnapshotReference";
        link.TextDecorations = null;
        var entry = entries[number - 1];
        var anchor = entry.AnswerAnchor is null ? null : AiReferencePresentation.NormalizePreview(entry.AnswerAnchor);
        var formula = anchor is { PreviewKind: "formula", PreviewSource.Length: > 0 };
        if (anchor is not null) link.Tag = new SnapshotCopy(anchor.Quote +
            (string.IsNullOrWhiteSpace(entry.Question) ? "" : " · " + entry.Question));
        if (anchor is { PreviewKind: not ("formula" or "image") } && AiReferencePresentation.MayContainMath(anchor) &&
            AiReferencePresentation.Excerpt(anchor) is { } parts && parts.Any(part => part.Formula))
        {
            AddSnapshotExcerpt(link, key, parts);
            if (!string.IsNullOrWhiteSpace(entry.Question)) link.Inlines.Add(new Run(" · " + AiTurnView.AnnotationSummary(entry)));
            return entry;
        }
        var summary = anchor is { PreviewKind: "formula" or "image" } ? AiPanel.ReferencePreviewFallback(anchor) : AiTurnView.AnnotationSummary(entry);
        if (formula && (string.IsNullOrWhiteSpace(summary) || summary == anchor!.Quote)) summary = "公式";
        var text = new Run(summary) { Tag = formula ? new SnapshotFormulaCopy(anchor!.Quote) : null }; link.Inlines.Add(text);
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
        return entry;
    }

    private void AddSnapshotExcerpt(Hyperlink link, string key, IReadOnlyList<AiReferencePresentation.ExcerptPart> parts)
    {
        var remaining = 120;
        var lastFormulaIndex = -1;
        for (var i = parts.Count - 1; i >= 0; i--) if (parts[i].Formula) { lastFormulaIndex = i; break; }
        var shownFormulas = 0; var omittedFormula = false;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Formula)
            {
                if (shownFormulas >= 3 && index != lastFormulaIndex)
                {
                    if (!omittedFormula) { link.Inlines.Add(new Run("…")); omittedFormula = true; }
                    continue;
                }
                shownFormulas++;
                var tex = part.CopyTex ?? part.Text;
                var original = part.Display ? "$$" + tex + "$$" : "$" + tex + "$";
                var fallback = new Run("公式") { Tag = new SnapshotFormulaCopy(original) }; link.Inlines.Add(fallback);
                var visual = new ChatFormula(part.Text, part.Display, asset: FormulaAsset?.Invoke(part.Text, part.Display));
                visual.SetValue(TextElement.FontSizeProperty, FontSize);
                visual.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                visual.Cursor = Cursors.Hand;
                visual.MouseLeftButtonUp += (_, e) => { LocalLink?.Invoke(key); e.Handled = true; };
                _ = CompleteSnapshotFormulaAsync(link, fallback, visual, original, _renderVersion);
                continue;
            }
            var text = Regex.Replace(part.Text, @"\s+", " ");
            var runes = text.EnumerateRunes().ToArray();
            if (runes.Length <= remaining) { link.Inlines.Add(new Run(text)); remaining -= runes.Length; continue; }
            if (remaining > 0) link.Inlines.Add(new Run(string.Concat(runes.Take(remaining))));
            link.Inlines.Add(new Run("…"));
            // Retain a final formula even when the preceding prose is long.
            var finalFormula = parts.Skip(index + 1).LastOrDefault(item => item.Formula);
            if (finalFormula is not null && shownFormulas < 4) AddSnapshotExcerpt(link, key, [finalFormula]);
            break;
        }
    }

    private async Task CompleteSnapshotFormulaAsync(Hyperlink link, Run fallback, ChatFormula formula, string original, int version)
    {
        await formula.RenderTask;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (version != _renderVersion || link.Parent is null || fallback.Parent != link || formula.Error is not null) return;
        _readySnapshotFormulas.Add(new(link, fallback, formula, original, version));
        if (_snapshotFlushQueued) return;
        _snapshotFlushQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlushSnapshotFormulas));
    }

    private void FlushSnapshotFormulas()
    {
        _snapshotFlushQueued = false;
        var ready = _readySnapshotFormulas.ToArray(); _readySnapshotFormulas.Clear();
        var changed = false;
        foreach (var item in ready)
        {
            var (link, fallback, formula, original, version) = item;
            if (version != _renderVersion || link.Parent is null || fallback.Parent != link || formula.Error is not null) continue;
            if (!changed) { NotifyAsyncLayoutChanging(); changed = true; }
            ReplaceSnapshotFormula(link, fallback, formula, original);
        }
        if (changed) LayoutVersion++;
    }

    private void ReplaceSnapshotFormula(Hyperlink link, Run fallback, ChatFormula formula, string original)
    {
        formula.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = formula.DesiredSize;
        var cropped = natural.Width > 280 || natural.Height > 64;
        FrameworkElement content = formula;
        if (cropped)
        {
            formula.Width = natural.Width; formula.Height = natural.Height;
            formula.HorizontalAlignment = HorizontalAlignment.Left; formula.VerticalAlignment = VerticalAlignment.Top;
            var clip = new Grid { Width = Math.Min(280, natural.Width), Height = Math.Min(64, natural.Height), ClipToBounds = true,
                ToolTip = formula.Source, Margin = new Thickness(0, 0, 3, 0) };
            var horizontalFade = natural.Width > clip.Width;
            var fadeLength = Math.Min(12, horizontalFade ? clip.Width : clip.Height);
            var opacity = new LinearGradientBrush { StartPoint = horizontalFade ? new Point(0, .5) : new Point(.5, 0),
                EndPoint = horizontalFade ? new Point(1, .5) : new Point(.5, 1) };
            opacity.GradientStops.Add(new GradientStop(Colors.White, 0));
            opacity.GradientStops.Add(new GradientStop(Colors.White, 1 - fadeLength / (horizontalFade ? clip.Width : clip.Height)));
            opacity.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            opacity.Freeze(); clip.OpacityMask = opacity;
            clip.Children.Add(formula);
            TextBlock.SetBaselineOffset(clip, Math.Min(clip.Height, TextBlock.GetBaselineOffset(formula)));
            content = clip;
        }
        var inline = new InlineUIContainer(content) { Tag = original, BaselineAlignment = BaselineAlignment.Baseline };
        link.Inlines.InsertBefore(fallback, inline);
        if (cropped) link.Inlines.InsertBefore(fallback, new Run("…"));
        link.Inlines.Remove(fallback);
        if (link.Parent is Paragraph paragraph) paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
    }
}
