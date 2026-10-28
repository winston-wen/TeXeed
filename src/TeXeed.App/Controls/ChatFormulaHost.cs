using System.Windows;
using System.Windows.Controls;

namespace TeXeed.App.Controls;

/// <summary>Short equations need no nested scroll infrastructure. Create it only on overflow.</summary>
internal sealed class ChatFormulaHost : Decorator
{
    private readonly ChatFormula _formula;
    private ScrollViewer? _scroll;
    private readonly bool _leftAligned;
    internal ChatFormulaHost(ChatFormula formula, bool leftAligned = false) { _formula = formula; _leftAligned = leftAligned; Child = formula; ClipToBounds = true; }
    protected override Size MeasureOverride(Size constraint)
    {
        _formula.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var overflow = double.IsFinite(constraint.Width) && _formula.DesiredSize.Width + (_leftAligned ? 0 : 12) > constraint.Width;
        if (overflow && _scroll is null)
        {
            Child = null;
            _scroll = new ScrollViewer { Content = _formula, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = _leftAligned ? new Thickness(0, 6, 0, 6) : new Thickness(6), HorizontalContentAlignment = _leftAligned ? HorizontalAlignment.Left : HorizontalAlignment.Center };
            Child = _scroll;
        }
        else if (!overflow && _scroll is not null)
        { _scroll.Content = null; _scroll = null; Child = _formula; }
        if (_scroll is not null) { _scroll.Measure(constraint); return _scroll.DesiredSize; }
        return new Size(_formula.DesiredSize.Width + 12, _formula.DesiredSize.Height + 12);
    }
    protected override Size ArrangeOverride(Size size)
    {
        if (_scroll is not null) _scroll.Arrange(new Rect(size));
        else _formula.Arrange(new Rect(new Point(_leftAligned ? 0 : Math.Max(6, (size.Width - _formula.DesiredSize.Width) / 2), 6), _formula.DesiredSize));
        return size;
    }
}
