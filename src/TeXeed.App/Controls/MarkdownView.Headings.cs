using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private static readonly object HeadingNumberMarker = new();
    private readonly List<(Run Run, int Level)> _headingMarkers = [];

    private void AddHeadingNumber(Paragraph paragraph, int level)
    {
        paragraph.FontSize = FontSize; paragraph.FontWeight = FontWeights.Normal;
        paragraph.LineHeight = 25; paragraph.Margin = new Thickness(0);
        var marker = new Run($"[{TurnNumber}:h{level}]\u2002")
        {
            Tag = HeadingNumberMarker, FontFamily = new FontFamily("Source Han Sans SC, Microsoft YaHei"),
            FontWeight = FontWeights.Normal
        };
        marker.SetResourceReference(TextElement.ForegroundProperty, "ChatHeading");
        if (paragraph.Inlines.FirstInline is { } first) paragraph.Inlines.InsertBefore(first, marker);
        else paragraph.Inlines.Add(marker);
        _headingMarkers.Add((marker, level));
    }

    private void UpdateHeadingNumbers()
    {
        foreach (var (run, level) in _headingMarkers) run.Text = $"[{TurnNumber}:h{level}]\u2002";
        LayoutVersion++; _annotationPointers.Clear();
    }

    private static bool IsDecoration(TextPointer pointer, out TextPointer end)
    {
        if (IsCodePadding(pointer, out end)) return true;
        var run = pointer.Parent as Run ?? (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart
            ? pointer.GetAdjacentElement(LogicalDirection.Forward) as Run : null);
        if (run is not null && ReferenceEquals(run.Tag, HeadingNumberMarker)) { end = run.ElementEnd; return true; }
        end = pointer; return false;
    }
}
