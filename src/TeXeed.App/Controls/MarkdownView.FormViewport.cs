using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private int _formBringVersion;

    private bool OwnsFormField(Control field) => _formControls.Contains(field) ||
        _tableViews.Any(table => table.View.OwnsFormField(field));

    private void FormFieldPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || (Keyboard.Modifiers & ~(ModifierKeys.Shift)) != ModifierKeys.None || sender is not Control field) return;
        var owner = _tableOwner ?? this;
        var fields = new List<Control>();
        void Collect(MarkdownView view)
        {
            fields.AddRange(view._formControls.Where(control => control.IsLoaded && control.IsVisible && control.IsEnabled && control.Focusable));
            foreach (var table in view._tableViews) Collect(table.View);
        }
        Collect(owner);
        var ordered = fields.Select(control =>
        {
            try { return (Control: control, Point: control.TranslatePoint(new Point(), owner)); }
            catch (InvalidOperationException) { return (Control: control, Point: new Point(double.PositiveInfinity, double.PositiveInfinity)); }
        }).OrderBy(item => item.Point.Y).ThenBy(item => item.Point.X).Select(item => item.Control).ToArray();
        var index = Array.IndexOf(ordered, field);
        var next = index + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        if (index >= 0 && next >= 0 && next < ordered.Length && ordered[next].Focus()) e.Handled = true;
    }

    private void FormRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_tableOwner is not null || e.OriginalSource is not ScrollViewer source || !ReferenceEquals(e.TargetObject, source) ||
            !ReferenceEquals(source, Template?.FindName("PART_ContentHost", this))) return;

        if (_forms is null || Keyboard.FocusedElement is not Control field || !OwnsFormField(field))
        {
            var focusedTable = _tableViews.FirstOrDefault(item => item.View.IsKeyboardFocusWithin);
            if (focusedTable is not null) TableRequestBringIntoView(e, focusedTable);
            return;
        }

        e.Handled = true;
        ScrollViewer? conversation = null;
        for (DependencyObject? current = VisualTreeHelper.GetParent(this); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ScrollViewer candidate && candidate.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && candidate.ScrollableHeight > 0)
            { conversation = candidate; break; }
        var table = _tableViews.FirstOrDefault(item => item.View.OwnsFormField(field));
        if (conversation is null && table is null) return;
        var version = ++_formBringVersion;
        var offset = conversation?.VerticalOffset;
        var horizontalOffset = table?.Scroll.HorizontalOffset;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != _formBringVersion || !field.IsKeyboardFocusWithin || !field.IsLoaded ||
                conversation is not null && (!conversation.IsLoaded || Math.Abs(conversation.VerticalOffset - offset!.Value) > .5)) return;
            try
            {
                if (table is not null && horizontalOffset is { } saved && Math.Abs(table.Scroll.HorizontalOffset - saved) <= .5)
                {
                    var x = field.TranslatePoint(new Point(), table.Scroll).X;
                    var right = x + field.ActualWidth;
                    var horizontal = x < 0 ? x : right > table.Scroll.ViewportWidth ? Math.Min(x, right - table.Scroll.ViewportWidth) : 0;
                    if (Math.Abs(horizontal) > .5)
                        table.Scroll.ScrollToHorizontalOffset(Math.Clamp(saved + horizontal, 0, table.Scroll.ScrollableWidth));
                }
                if (conversation is null || offset is null) return;
                var y = field.TranslatePoint(new Point(), conversation).Y;
                var bottom = y + field.ActualHeight;
                if (field is TextBox { AcceptsReturn: true } box && field.ActualHeight > conversation.ViewportHeight)
                {
                    var caret = box.GetRectFromCharacterIndex(box.CaretIndex);
                    if (!caret.IsEmpty)
                    {
                        y = box.TranslatePoint(caret.TopLeft, conversation).Y;
                        bottom = y + caret.Height;
                    }
                }
                var change = y < 0 ? y : bottom > conversation.ViewportHeight ? bottom - conversation.ViewportHeight : 0;
                if (Math.Abs(change) > .5)
                {
                    conversation.ScrollToVerticalOffset(Math.Clamp(offset.Value + change, 0, conversation.ScrollableHeight));
                    conversation.UpdateLayout();
                    if (field.IsKeyboardFocusWithin && field.IsLoaded && conversation.IsLoaded)
                    {
                        var correctedY = field.TranslatePoint(new Point(), conversation).Y;
                        var correctedBottom = correctedY + field.ActualHeight;
                        if (field is TextBox { AcceptsReturn: true } current && field.ActualHeight > conversation.ViewportHeight)
                        {
                            var caret = current.GetRectFromCharacterIndex(current.CaretIndex);
                            if (!caret.IsEmpty)
                            {
                                correctedY = current.TranslatePoint(caret.TopLeft, conversation).Y;
                                correctedBottom = correctedY + caret.Height;
                            }
                        }
                        var correction = correctedY < 0 ? correctedY : correctedBottom > conversation.ViewportHeight ? correctedBottom - conversation.ViewportHeight : 0;
                        if (Math.Abs(correction) > .5)
                            conversation.ScrollToVerticalOffset(Math.Clamp(conversation.VerticalOffset + correction, 0, conversation.ScrollableHeight));
                    }
                }
            }
            catch (InvalidOperationException) { }
        }));
    }

    private void TableRequestBringIntoView(RequestBringIntoViewEventArgs e, TableViewport table)
    {
        e.Handled = true;
        ScrollViewer? conversation = null;
        for (DependencyObject? current = VisualTreeHelper.GetParent(this); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ScrollViewer candidate && candidate.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && candidate.ScrollableHeight > 0)
            { conversation = candidate; break; }
        if (conversation is null) return;
        var version = ++_formBringVersion;
        var offset = conversation.VerticalOffset;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != _formBringVersion || !table.View.IsKeyboardFocusWithin || !table.View.IsLoaded || !conversation.IsLoaded ||
                Math.Abs(conversation.VerticalOffset - offset) > .5) return;
            try
            {
                bool GetCaret(out double top, out double bottom)
                {
                    var pointer = table.View.CaretPosition;
                    var rect = pointer.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
                    if (rect.IsEmpty) rect = pointer.GetCharacterRect(System.Windows.Documents.LogicalDirection.Backward);
                    top = bottom = 0;
                    if (rect.IsEmpty) return false;
                    top = table.View.TranslatePoint(rect.TopLeft, conversation).Y; bottom = top + rect.Height;
                    return true;
                }
                if (!GetCaret(out var top, out var bottom)) return;
                var change = top < 0 ? top : bottom > conversation.ViewportHeight ? bottom - conversation.ViewportHeight : 0;
                if (Math.Abs(change) <= .5) return;
                conversation.ScrollToVerticalOffset(Math.Clamp(offset + change, 0, conversation.ScrollableHeight));
                conversation.UpdateLayout();
                if (!table.View.IsKeyboardFocusWithin || !table.View.IsLoaded || !conversation.IsLoaded || !GetCaret(out top, out bottom)) return;
                var correction = top < 0 ? top : bottom > conversation.ViewportHeight ? bottom - conversation.ViewportHeight : 0;
                if (Math.Abs(correction) > .5)
                    conversation.ScrollToVerticalOffset(Math.Clamp(conversation.VerticalOffset + correction, 0, conversation.ScrollableHeight));
            }
            catch (InvalidOperationException) { }
        }));
    }
}
