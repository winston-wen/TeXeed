using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
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
}
