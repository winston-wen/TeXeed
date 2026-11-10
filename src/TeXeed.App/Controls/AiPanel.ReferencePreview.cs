using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    internal static string ReferencePreviewFallback(AiAnswerAnchor anchor)
    {
        static string Surrounding(AiAnswerAnchor item)
        {
            var before = item.PreviewBefore?.Trim(); var after = item.PreviewAfter?.Trim();
            return string.Join(" … ", new[] { before, after }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        var fallback = anchor.PreviewKind switch
        {
            "formula" when !string.IsNullOrWhiteSpace(anchor.PreviewNumber) => "公式 (" + anchor.PreviewNumber + ")",
            "image" when !string.IsNullOrWhiteSpace(anchor.PreviewSource) => anchor.PreviewSource,
            "formula" or "image" => Surrounding(anchor),
            _ => anchor.Quote
        };
        if (string.IsNullOrWhiteSpace(fallback)) fallback = anchor.PreviewKind == "image" ? "图片（无文字说明）" : anchor.Quote;
        return fallback;
    }
    private async void ReferencePreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentControl target || target.DataContext is not AiEntry { AnswerAnchor: { } anchor } entry) return;
        var fallback = ReferencePreviewFallback(anchor);
        var text = new TextBlock { Text = fallback, TextWrapping = TextWrapping.Wrap, MaxHeight = 140 };
        target.Content = text;
        if (anchor.PreviewKind != "formula" || string.IsNullOrWhiteSpace(anchor.PreviewSource)) return;
        var formula = new ChatFormula(anchor.PreviewSource, true);
        formula.SetValue(TextElement.FontSizeProperty, 15d);
        await formula.RenderTask;
        if (!target.IsLoaded || !ReferenceEquals(target.DataContext, entry) || formula.Error is not null) return;
        formula.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (formula.DesiredSize.Width <= 360 && formula.DesiredSize.Height <= 64) target.Content = formula;
    }
}
