using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TeXeed.Core;

namespace TeXeed.App.Controls;

/// <summary>Inline title typography without invoking TeX or executing document macros.</summary>
public sealed class LatexTitleBlock : TextBlock
{
    public static readonly DependencyProperty LatexProperty = DependencyProperty.Register(
        nameof(Latex), typeof(string), typeof(LatexTitleBlock), new PropertyMetadata("", Rebuild));
    public string Latex { get => (string)GetValue(LatexProperty); set => SetValue(LatexProperty, value); }

    static LatexTitleBlock() => FontSizeProperty.OverrideMetadata(typeof(LatexTitleBlock),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, Rebuild));

    private static void Rebuild(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var block = (LatexTitleBlock)target;
        block.Inlines.Clear();
        Add(block.Inlines, LatexTitle.Parse(block.Latex), block.FontSize);
    }

    private static void Add(InlineCollection inlines, IReadOnlyList<TitlePart> parts, double size)
    {
        foreach (var part in parts)
        {
            if (part.Kind == "text") { inlines.Add(new Run(part.Text)); continue; }
            if (part.Kind == "math")
            {
                var formula = new OutlineFormula(part.Text, size);
                inlines.Add(new InlineUIContainer(formula) { BaselineAlignment = BaselineAlignment.Center });
                continue;
            }
            if (part.Kind is "fraction" or "binomial")
            {
                var grid = new Grid { Margin = new Thickness(2, 1, 2, 1), IsHitTestVisible = false };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var top = MakeBlock(part.Children!, Math.Max(8, size * .8));
                var bottom = MakeBlock(part.Second!, Math.Max(8, size * .8));
                top.Margin = new Thickness(2, 0, 2, 1); bottom.Margin = new Thickness(2, 1, 2, 0);
                var rule = new Border { Child = top, BorderThickness = new Thickness(0, 0, 0, part.Kind == "fraction" ? 1 : 0) };
                rule.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("Foreground") { Source = top });
                grid.Children.Add(rule); Grid.SetRow(bottom, 1); grid.Children.Add(bottom);
                if (part.Kind == "binomial") inlines.Add(new Run("("));
                inlines.Add(new InlineUIContainer(grid) { BaselineAlignment = BaselineAlignment.Center });
                if (part.Kind == "binomial") inlines.Add(new Run(")"));
                continue;
            }
            var span = new Span();
            switch (part.Kind)
            {
                case "bold": span.FontWeight = FontWeights.Bold; break;
                case "italic": span.FontStyle = FontStyles.Italic; break;
                case "math": span.FontFamily = new FontFamily("Cambria Math, Microsoft YaHei UI"); break;
                case "roman": span.FontStyle = FontStyles.Normal; break;
                case "mono": span.FontFamily = new FontFamily("Consolas"); break;
                case "sans": span.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); break;
                case "underline": span.TextDecorations = System.Windows.TextDecorations.Underline; break;
                case "overline": span.TextDecorations = System.Windows.TextDecorations.OverLine; break;
                case "sup": case "sub":
                    span.BaselineAlignment = part.Kind == "sup" ? BaselineAlignment.Superscript : BaselineAlignment.Subscript;
                    span.FontSize = Math.Max(8, size * .75); break;
                case "sqrt": span.Inlines.Add(new Run("√")); break;
                case "blackboard":
                    // Unicode provides the common number-set symbols without a font dependency.
                    foreach (var child in part.Children ?? [])
                        if (child.Kind == "text") span.Inlines.Add(new Run(string.Concat(child.Text.Select(c => c switch
                        { 'R' => "ℝ", 'N' => "ℕ", 'Z' => "ℤ", 'Q' => "ℚ", 'C' => "ℂ", 'P' => "ℙ", 'H' => "ℍ", _ => c.ToString() }))));
                        else Add(span.Inlines, [child], size);
                    inlines.Add(span); continue;
            }
            if (part.Kind == "sqrt")
            {
                var radicand = new Span { TextDecorations = System.Windows.TextDecorations.OverLine };
                Add(radicand.Inlines, part.Children ?? [], size); span.Inlines.Add(radicand);
            }
            else Add(span.Inlines, part.Children ?? [], part.Kind is "sup" or "sub" ? span.FontSize : size);
            inlines.Add(span);
        }
    }

    private static TextBlock MakeBlock(IReadOnlyList<TitlePart> parts, double size)
    {
        var block = new TextBlock { FontSize = size, FontFamily = new FontFamily("Cambria Math"), HorizontalAlignment = HorizontalAlignment.Center };
        Add(block.Inlines, parts, size);
        return block;
    }
}
