using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private static readonly object HeadingNumberMarker = new();
    private readonly List<(Run Run, int Level)> _headingMarkers = [];
    private void AddHeadingNumber(Paragraph paragraph, int level)
    {
        paragraph.FontSize = FontSize;
        var text = HeadingRuns(paragraph.Inlines).Select(run => (Run: run, Weight: run.FontWeight)).ToArray();
        paragraph.FontFamily = FontFamily;
        paragraph.LineHeight = ChatLineHeight;
        var marker = new Run($"[{TurnNumber}:h{level}]\u2002")
        {
            Tag = HeadingNumberMarker, FontFamily = FontFamily
        };
        marker.SetResourceReference(TextElement.ForegroundProperty, "ChatHeading");
        var sample = text.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Run.Text));
        if (sample.Run is not null)
            OffsetInk(marker, InkAxis(sample.Run.Text, FontFamily, sample.Weight, sample.Run.FontStyle, FontSize)
                - InkAxis(marker.Text, FontFamily, marker.FontWeight, FontStyles.Normal, FontSize));
        if (paragraph.Inlines.FirstInline is { } first) paragraph.Inlines.InsertBefore(first, marker);
        else paragraph.Inlines.Add(marker);
        _headingMarkers.Add((marker, level));
    }

    private static IEnumerable<Run> HeadingRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
            if (inline is Run run && !string.IsNullOrWhiteSpace(run.Text) && run.ReadLocalValue(TextElement.FontFamilyProperty) == DependencyProperty.UnsetValue) yield return run;
            else if (inline is Span span && !ReferenceEquals(span.Tag, CodeSpanMarker))
                foreach (var child in HeadingRuns(span.Inlines)) yield return child;
    }
    private static double InkAxis(string text, FontFamily family, FontWeight weight, FontStyle style, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, style, weight, FontStretches.Normal), size, Brushes.Black, 1);
        return formatted.Height + formatted.OverhangAfter - formatted.Extent / 2 - formatted.Baseline;
    }
    private static void OffsetInk(Run run, double y)
    {
        if (Math.Abs(y) < .001) return;
        var effect = new TextEffect { PositionStart = 0, PositionCount = int.MaxValue, Transform = new TranslateTransform(0, y) };
        effect.Freeze(); run.TextEffects = new TextEffectCollection { effect };
    }

    private void UpdateHeadingNumbers()
    {
        foreach (var (run, level) in _headingMarkers) run.Text = $"[{TurnNumber}:h{level}]\u2002";
        LayoutVersion++; _annotationPointers.Clear(); QueueWaveLayout();
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
