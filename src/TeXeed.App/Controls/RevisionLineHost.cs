using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TeXeed.App.Controls;

// A paragraph only needs the native text-input/IME surface once the user reaches it.
internal sealed class RevisionLineHost : Decorator
{
    private readonly RevisionBuffer _buffer;
    private readonly string? _imageBaseDirectory;
    private readonly Action<RevisionLineEditor> _connected;
    internal RevisionLineEditor? Editor => Child as RevisionLineEditor;

    internal RevisionLineHost(RevisionBuffer buffer, string? images, Action<RevisionLineEditor> connected)
    {
        _buffer = buffer; _imageBaseDirectory = images; _connected = connected;
        Focusable = true; Cursor = Cursors.IBeam;
        if (buffer.Atoms.Any(atom => atom.HasObject)) { Activate(); return; }
        var text = new TextBlock { Text = buffer.Display, FontFamily = ChatReadingTypography.Family, FontSize = AiChatTypography.FontSize,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(2, 1, 2, 1), MinHeight = AiChatTypography.LineHeight,
            Background = System.Windows.Media.Brushes.Transparent };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); Child = text;
        MouseEnter += (_, _) => Activate();
    }

    internal RevisionLineEditor Activate()
    {
        if (Editor != null) return Editor;
        var editor = new RevisionLineEditor(_buffer, _imageBaseDirectory);
        Child = editor;
        _connected(editor);
        if (IsLoaded)
        {
            editor.ApplyTemplate();
            UpdateLayout();
        }
        return editor;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this)) Activate().TextArea.Focus();
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.OriginalSource is not TextBlock || Editor?.IsAncestorOf((DependencyObject)e.OriginalSource) == true) return;
        var point = e.GetPosition(this);
        var editor = Activate(); UpdateLayout(); editor.TextArea.Focus();
        var view = editor.TextArea.TextView;
        var position = view.GetPosition(TranslatePoint(point, view) + view.ScrollOffset);
        if (position.HasValue) editor.CaretOffset = editor.Document.GetOffset(position.Value.Location);
        e.Handled = true;
    }
}
