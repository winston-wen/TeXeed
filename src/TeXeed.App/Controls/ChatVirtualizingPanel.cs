using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TeXeed.App.Controls;

/// <summary>Native pixel virtualization with an explicit navigation entry point.</summary>
public sealed class ChatVirtualizingPanel : VirtualizingStackPanel
{
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        var children = InternalChildren;
        var center = size.Height / 2;
        var nearest = double.PositiveInfinity;
        var anchor = -1;
        var previousBottom = double.NaN;
        var discontinuous = false;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is not FrameworkElement { Visibility: not Visibility.Collapsed } child) continue;
            var slot = LayoutInformation.GetLayoutSlot(child);
            if (slot.IsEmpty) continue;
            discontinuous |= Math.Abs(slot.Top - previousBottom) > .01;
            previousBottom = slot.Bottom;
            var distance = Math.Max(0, Math.Max(slot.Top - center, center - slot.Bottom));
            if (slot.Height > 0 && distance <= nearest) { nearest = distance; anchor = i; }
        }
        if (discontinuous && anchor >= 0 && ItemsControl.GetItemsOwner(this) is { } owner)
        {
            AlignFrom(anchor, -1, owner.ItemContainerGenerator);
            AlignFrom(anchor, 1, owner.ItemContainerGenerator);
        }
        return size;
    }

    // WPF locates retained containers outside its cache using estimated item sizes.
    // Once both neighbors exist, their arranged heights give their exact shared edge.
    private void AlignFrom(int anchor, int step, ItemContainerGenerator generator)
    {
        var children = InternalChildren;
        var neighbor = (FrameworkElement)children[anchor];
        var adjacent = LayoutInformation.GetLayoutSlot(neighbor);
        for (var i = anchor + step; i >= 0 && i < children.Count; i += step)
        {
            if (children[i] is not FrameworkElement { Visibility: not Visibility.Collapsed } child) continue;
            var slot = LayoutInformation.GetLayoutSlot(child);
            if (slot.IsEmpty) continue;
            var gap = step > 0 ? slot.Top - adjacent.Bottom : adjacent.Top - slot.Bottom;
            if (Math.Abs(gap) > .01)
            {
                var index = generator.IndexFromContainer(child);
                var neighborIndex = generator.IndexFromContainer(neighbor);
                if (index >= 0 && neighborIndex >= 0 && (gap < 0 || index == neighborIndex + step))
                {
                    slot.Y -= step * gap;
                    child.Arrange(slot);
                }
            }
            neighbor = child;
            adjacent = slot;
        }
    }

    internal void Reveal(int index)
    {
        if (ScrollOwner is ChatScrollViewer scroll) scroll.RevealItem(() => BringIndexIntoView(index));
        else BringIndexIntoView(index);
    }
}
