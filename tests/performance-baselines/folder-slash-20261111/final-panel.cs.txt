using System.Windows;
using System.Windows.Controls;

namespace TeXeed.App.Controls;

// Keep the last directory and separator beside the prefix while allowing both to trim.
public sealed class AttachmentPathPanel : Panel
{
    public static readonly DependencyProperty IsLeafClippedProperty = DependencyProperty.Register(
        nameof(IsLeafClipped), typeof(bool), typeof(AttachmentPathPanel), new PropertyMetadata(false));
    public bool IsLeafClipped { get => (bool)GetValue(IsLeafClippedProperty); private set => SetValue(IsLeafClippedProperty, value); }
    private double _prefixNatural, _middleNatural, _leafNatural, _separatorNatural;

    private (double Prefix, double Middle, double Leaf, double Separator) Widths(double available)
    {
        var separator = Math.Min(_separatorNatural, available);
        var middle = Math.Min(_middleNatural, Math.Max(0, available - separator));
        var remaining = Math.Max(0, available - separator - middle);
        if (_prefixNatural + _leafNatural <= remaining) return (_prefixNatural, middle, _leafNatural, separator);
        var prefixMinimum = Math.Min(_prefixNatural, Math.Min(34, remaining));
        var leaf = Math.Min(_leafNatural, Math.Max(0, remaining - prefixMinimum));
        return (Math.Min(_prefixNatural, remaining - leaf), middle, leaf, separator);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 4) return new Size();
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        _prefixNatural = InternalChildren[0].DesiredSize.Width;
        _middleNatural = InternalChildren[1].DesiredSize.Width;
        _leafNatural = InternalChildren[2].DesiredSize.Width;
        _separatorNatural = InternalChildren[3].DesiredSize.Width;
        var width = double.IsInfinity(availableSize.Width) ? _prefixNatural + _middleNatural + _leafNatural + _separatorNatural : Math.Max(0, availableSize.Width);
        var sizes = Widths(width);
        InternalChildren[0].Measure(new Size(sizes.Prefix, availableSize.Height));
        InternalChildren[1].Measure(new Size(sizes.Middle, availableSize.Height));
        InternalChildren[2].Measure(new Size(sizes.Leaf, availableSize.Height));
        InternalChildren[3].Measure(new Size(sizes.Separator, availableSize.Height));
        return new Size(sizes.Prefix + sizes.Middle + sizes.Leaf + sizes.Separator, InternalChildren.Cast<UIElement>().Max(child => child.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 4) return finalSize;
        var sizes = Widths(Math.Max(0, finalSize.Width));
        IsLeafClipped = _leafNatural > sizes.Leaf + 0.1;
        InternalChildren[0].Arrange(new Rect(0, 0, sizes.Prefix, finalSize.Height));
        InternalChildren[1].Arrange(new Rect(sizes.Prefix, 0, sizes.Middle, finalSize.Height));
        InternalChildren[2].Arrange(new Rect(sizes.Prefix + sizes.Middle, 0, sizes.Leaf, finalSize.Height));
        InternalChildren[3].Arrange(new Rect(sizes.Prefix + sizes.Middle + sizes.Leaf, 0, sizes.Separator, finalSize.Height));
        return finalSize;
    }
}
