using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using TeXeed.App.Services;

namespace TeXeed.App.Controls;

public sealed partial class LatexEditor
{
    private void ThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        Background = Brush("EditorBackground"); Foreground = Brush("EditorForeground");
        LineNumbersForeground = Brush("Subtle");
        TextArea.SelectionBrush = Brush("Selection"); TextArea.SelectionForeground = Brush("EditorForeground");
        TextArea.TextView.CurrentLineBackground = Brush("CurrentLine");
        TextArea.TextView.CurrentLineBorder = new Pen(Brush("CurrentLine"), 0);
        _search.MarkerBrush = Brush("SearchMatch");
        if (_replacementStatus is not null) _replacementStatus.Foreground = Brush("Muted");
        foreach (var definition in new[] { LatexHighlighting.Value, BibHighlighting.Value })
            foreach (var color in definition.NamedHighlightingColors)
            {
                var key = color.Name switch { "Type" => "SyntaxCommand", "String" => "SyntaxMath", _ => "Syntax" + color.Name };
                color.Foreground = new SimpleHighlightingBrush(Brush(key).Color);
            }
        if (_completion is not null) { _completion.Background = Background; _completion.Foreground = Foreground; }
        TextArea.TextView.Redraw();
    }
}
