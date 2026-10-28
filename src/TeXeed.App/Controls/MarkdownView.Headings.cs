using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private static readonly object HeadingNumberMarker = new();
    private readonly List<(Run Run, int Level)> _headingMarkers = [];
    private FontFamily? _headingFont;

    private void AddHeadingNumber(Paragraph paragraph, int level)
    {
        paragraph.FontSize = FontSize;
        var text = HeadingRuns(paragraph.Inlines).Select(run => (Run: run, Weight: run.FontWeight)).ToArray();
        _headingFont ??= new FontFamily { Baseline = FontFamily.Baseline, LineSpacing = FontFamily.LineSpacing,
            FamilyMaps = { new FontFamilyMap { Target = "Segoe UI, Microsoft YaHei UI, Source Han Sans SC" } } };
        paragraph.FontWeight = FontWeights.Light; paragraph.FontFamily = _headingFont;
        paragraph.LineHeight = 25; paragraph.Margin = new Thickness(0);
        foreach (var (run, oldWeight) in text)
        {
            run.FontWeight = FontWeights.Light;
            PreserveInkAxis(run, FontFamily, oldWeight, _headingFont, FontWeights.Light);
        }
        var marker = new Run($"[{TurnNumber}:h{level}]\u2002")
        {
            Tag = HeadingNumberMarker, FontFamily = _headingFont, FontWeight = FontWeights.Light
        };
        marker.SetResourceReference(TextElement.ForegroundProperty, "ChatHeading");
        var sample = text.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Run.Text));
        if (sample.Run is not null)
            OffsetInk(marker, InkAxis(sample.Run.Text, FontFamily, sample.Weight, sample.Run.FontStyle, FontSize)
                - InkAxis(marker.Text, _headingFont, FontWeights.Light, FontStyles.Normal, FontSize));
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
    private static void PreserveInkAxis(Run run, FontFamily previous, FontWeight oldWeight, FontFamily current, FontWeight weight) =>
        OffsetInk(run, InkAxis(run.Text, previous, oldWeight, run.FontStyle, run.FontSize) - InkAxis(run.Text, current, weight, run.FontStyle, run.FontSize));
    private static void OffsetInk(Run run, double y)
    {
        if (Math.Abs(y) < .001) return;
        var effect = new TextEffect { PositionStart = 0, PositionCount = int.MaxValue, Transform = new TranslateTransform(0, y) };
        effect.Freeze(); run.TextEffects = new TextEffectCollection { effect };
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
