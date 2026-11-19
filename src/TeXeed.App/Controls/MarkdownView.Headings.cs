using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;
using System.Text;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private static readonly object HeadingNumberMarker = new();
    private readonly List<(Run Run, string Number)> _headingMarkers = [];
    private int[] _headingSeed = new int[6];
    private AiHeadingNumbers _headingNumbers = new();
    internal int[] HeadingSeed { set => _headingSeed = (int[])value.Clone(); }
    internal int[] HeadingCounts => _headingNumbers.Snapshot();
    private void ResetHeadingNumbers() => _headingNumbers = new AiHeadingNumbers(_headingSeed);
    private void AddHeadingNumber(Paragraph paragraph, int level)
    {
        paragraph.FontSize = FontSize;
        paragraph.FontFamily = FontFamily;
        paragraph.FontWeight = FontWeights.SemiBold;
        if (double.IsNaN(paragraph.LineHeight)) paragraph.LineHeight = ChatLineHeight;
        var number = _headingNumbers.Next(level);
        var marker = new Run($"[{TurnNumber}:{number}]\u2002")
        {
            Tag = HeadingNumberMarker, FontFamily = FontFamily, FontWeight = paragraph.FontWeight
        };
        marker.SetResourceReference(TextElement.ForegroundProperty, "ChatHeading");
        var runs = HeadingRuns(paragraph.Inlines);
        var sample = runs.Select(run => (Run: run, Word: FirstTitleWord(run.Text)))
            .FirstOrDefault(item => item.Word.Length > 0);
        var titleRun = sample.Run ?? runs.FirstOrDefault();
        if (titleRun is not null)
            OffsetInk(marker, InkAxis(sample.Word ?? titleRun.Text, FontFamily, titleRun.FontWeight, titleRun.FontStyle, FontSize)
                - InkAxis(marker.Text, FontFamily, marker.FontWeight, FontStyles.Normal, FontSize) + FontSize * .035);
        if (paragraph.Inlines.FirstInline is { } first) paragraph.Inlines.InsertBefore(first, marker);
        else paragraph.Inlines.Add(marker);
        _headingMarkers.Add((marker, number));
    }

    private static string FirstTitleWord(string text)
    {
        var start = -1; var end = 0; var offset = 0; bool? scriptIsHan = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var han = value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF;
            if ((han || Rune.IsLetter(rune)) && (scriptIsHan is null || scriptIsHan == han))
            {
                if (start < 0) { start = offset; scriptIsHan = han; }
                end = offset + rune.Utf16SequenceLength;
            }
            else if (start >= 0) break;
            offset += rune.Utf16SequenceLength;
        }
        return start < 0 ? "" : text[start..end];
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
        foreach (var (run, number) in _headingMarkers) run.Text = $"[{TurnNumber}:{number}]\u2002";
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
