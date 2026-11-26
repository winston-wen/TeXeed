using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

// Each row keeps its reading surface until keyboard focus actually reaches it.
internal sealed class RevisionLineHost : Border
{
    private readonly string? _imageBaseDirectory;
    private readonly Func<AiRevisionRow, FrameworkElement> _render;
    private readonly Action<RevisionLineEditor> _connected;
    private readonly Action<RevisionLineEditor> _closed;
    private FrameworkElement _preview;
    private AiRevisionRow _previewRow;
    internal AiRevisionRow Row { get; set; }
    internal RevisionLineEditor? Editor => Child as RevisionLineEditor;

    internal RevisionLineHost(AiRevisionRow row, string? images, Func<AiRevisionRow, FrameworkElement> render,
        Action<RevisionLineEditor> connected, Action<RevisionLineEditor> closed)
    {
        Row = _previewRow = row;
        _render = render;
        _imageBaseDirectory = images;
        _connected = connected;
        _closed = closed;
        Focusable = true;
        Cursor = Cursors.IBeam;
        Background = System.Windows.Media.Brushes.Transparent;
        Child = _preview = render(row);
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (Equals(e.NewValue, false) && Editor is { } editor)
            {
                if (!ReferenceEquals(_previewRow, Row))
                {
                    _preview = _render(Row);
                    _previewRow = Row;
                }
                Child = _preview;
                _closed(editor);
            }
        };
    }

    internal RevisionLineEditor Activate()
    {
        if (Editor is { } current) return current;
        var editor = new RevisionLineEditor(RevisionBuffer.From(Row, editableInlineMath: true), _imageBaseDirectory);
        Child = editor;
        _connected(editor);
        editor.ApplyTemplate();
        if (!editor.TextArea.IsVisible && IsVisible) UpdateLayout();
        return editor;
    }

    internal void FocusSource(int offset)
    {
        var editor = Activate();
        editor.TextArea.Focus();
        editor.CaretOffset = Math.Clamp(offset, 0, editor.Document.TextLength);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this)) Activate().TextArea.Focus();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (Editor != null || e.Handled) return;
        var offset = 0;
        if (_preview is TextBlock plain && plain.GetPositionFromPoint(e.GetPosition(plain), true) is { } plainPosition)
            offset = new TextRange(plain.ContentStart, plainPosition).Text.Length;
        else if (_preview is MarkdownView rich && rich.GetPositionFromPoint(e.GetPosition(rich), true) is { } position)
        {
            var visible = rich.CopySourceLength(rich.Document.ContentStart, position);
            var mapping = AiRevisionText.SelectionText(Row.Text, rich.ReferenceText);
            var raw = visible == 0 ? 0 : visible > mapping.Ends.Count ? Row.Text.Length : mapping.Ends[visible - 1];
            offset = RevisionBuffer.From(Row, editableInlineMath: true).DisplayOffset(raw);
        }
        FocusSource(offset);
        e.Handled = true;
    }
}
