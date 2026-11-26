using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using Markdig;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace TeXeed.App.Controls;

internal sealed class RevisionLineEditor : TextEditor
{
    private readonly List<(TextAnchor Position, RevisionAtom Atom)> _atoms = [];
    private readonly string? _imageBaseDirectory;
    internal Action? Changed;
    internal Action<string>? PasteText;
    internal Action? UndoEdit;
    internal RevisionBuffer Buffer => new(Text, _atoms.Select(item => item.Atom with { At = item.Position.Offset }).ToArray());

    internal RevisionLineEditor(RevisionBuffer content, string? imageBaseDirectory)
    {
        _imageBaseDirectory = imageBaseDirectory;
        FontFamily = ChatReadingTypography.Family;
        FontSize = AiChatTypography.FontSize;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "Ink");
        WordWrap = true;
        AllowDrop = false;
        Options.EnableTextDragDrop = false;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        MinHeight = AiChatTypography.LineHeight;
        Padding = new Thickness(2, 1, 2, 1);
        Text = content.Display;
        foreach (var atom in content.Atoms)
        {
            var anchor = Document.CreateAnchor(atom.At);
            anchor.MovementType = AnchorMovementType.AfterInsertion;
            anchor.SurviveDeletion = true;
            _atoms.Add((anchor, atom));
        }
        TextArea.ReadOnlySectionProvider = new ProtectedSections(this);
        TextArea.TextView.ElementGenerators.Insert(0, new AtomRenderer(this));
        TextArea.Caret.CaretBrush = Brushes.Transparent;
        var caret = new SourceCaret(TextArea);
        TextArea.TextView.InsertLayer(caret, KnownLayer.Caret, LayerInsertionPosition.Above);
        Document.UndoStack.SizeLimit = 0;
        Document.UpdateFinished += (_, _) => Changed?.Invoke();
        TextArea.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(RunCommand), true);
        PreviewDragOver += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; };
    }

    // The input caret still belongs to AvalonEdit (including IME positioning).
    // Paint its bounds at a readable width without changing the system-wide caret.
    internal sealed class SourceCaret : FrameworkElement
    {
        private const int HalfPeriodMilliseconds = 250;
        private readonly TextArea _area;
        private readonly DispatcherTimer _blink;
        private long _blinkStart;
        private bool HasInsertionPoint => _area.IsKeyboardFocusWithin && _area.Selection.IsEmpty;
        internal Brush Fill => (Brush)GetValue(System.Windows.Shapes.Shape.FillProperty);
        internal Rect InkBounds
        {
            get
            {
                if (!HasInsertionPoint || !_area.TextView.VisualLinesValid) return Rect.Empty;
                var rect = _area.Caret.CalculateCaretRectangle();
                if (rect.IsEmpty) return rect;
                rect.Offset(-_area.TextView.ScrollOffset.X, -_area.TextView.ScrollOffset.Y);
                rect.Width = Math.Max(3, rect.Width);
                return rect;
            }
        }
        internal SourceCaret(TextArea area)
        {
            _area = area;
            IsHitTestVisible = false;
            _blink = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(HalfPeriodMilliseconds) };
            _blink.Tick += Blink;
            SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "RevisionCaret");
            area.Caret.PositionChanged += (_, _) => UpdatePosition();
            area.SelectionChanged += (_, _) => UpdatePosition();
            area.IsKeyboardFocusWithinChanged += (_, _) => UpdatePosition();
            area.Document.TextChanged += (_, _) => RestartBlink();
            Loaded += (_, _) => RestartBlink();
            Unloaded += (_, _) => StopBlink();
            IsVisibleChanged += (_, _) => RestartBlink();
            area.TextView.VisualLinesChanged += (_, _) => InvalidateVisual();
            area.TextView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        }
        private void UpdatePosition()
        {
            InvalidateVisual();
            RestartBlink();
        }
        private void RestartBlink()
        {
            if (!IsLoaded || !IsVisible || !HasInsertionPoint) { StopBlink(); return; }
            _blinkStart = Environment.TickCount64;
            Opacity = 1;
            _blink.Start();
        }
        private void StopBlink()
        {
            _blink.Stop();
            Opacity = 0;
        }
        private void Blink(object? sender, EventArgs e)
        {
            // Keep the phase tied to the last edit even if a busy dispatcher delivers a late tick.
            var elapsed = Environment.TickCount64 - _blinkStart;
            Opacity = elapsed / HalfPeriodMilliseconds % 2 == 0 ? 1 : 0;
            _blink.Interval = TimeSpan.FromMilliseconds(HalfPeriodMilliseconds - elapsed % HalfPeriodMilliseconds);
        }
        protected override void OnRender(DrawingContext drawingContext)
        {
            var bounds = InkBounds;
            if (!bounds.IsEmpty) drawingContext.DrawRectangle(Fill, null, bounds);
        }
    }

    private void RunCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Undo) { e.Handled = true; UndoEdit?.Invoke(); return; }
        if (e.Command == ApplicationCommands.Paste)
        {
            e.Handled = true;
            try { if (Clipboard.ContainsText()) PasteText?.Invoke(Clipboard.GetText()); }
            catch (System.Runtime.InteropServices.ExternalException) { ToolTip = "剪贴板暂时被占用"; }
        }
        else if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut)
        {
            e.Handled = true;
            if (SelectionLength == 0) return;
            var buffer = Buffer;
            var (start, end) = buffer.WholeAtoms(SelectionStart, SelectionStart + SelectionLength);
            var selected = buffer.Slice(start, end - start);
            var plain = selected.Apply(new("", "", "", 0, false, [])).Text;
            try { if (plain.Length > 0) Clipboard.SetText(plain); }
            catch (System.Runtime.InteropServices.ExternalException) { ToolTip = "剪贴板暂时被占用"; return; }
            if (e.Command == ApplicationCommands.Cut) PasteText?.Invoke("");
        }
    }

    private sealed class ProtectedSections(RevisionLineEditor line) : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset)
        {
            foreach (var (position, atom) in line._atoms)
            {
                if (position.Offset >= offset) break;
                if (offset < position.Offset + atom.Length) return false;
            }
            return true;
        }
        public IEnumerable<ISegment> GetDeletableSegments(ISegment selection)
        {
            var start = selection.Offset;
            foreach (var (position, atom) in line._atoms)
            {
                var offset = position.Offset;
                if (offset + atom.Length <= start) continue;
                if (offset >= selection.EndOffset) break;
                if (offset > start) yield return new TextSegment { StartOffset = start, Length = offset - start };
                start = offset + atom.Length;
            }
            if (start < selection.EndOffset) yield return new TextSegment { StartOffset = start, Length = selection.EndOffset - start };
        }
    }

    private sealed class AtomRenderer(RevisionLineEditor line) : VisualLineElementGenerator
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

        private FrameworkElement ProtectedObject(string source)
        {
            var math = new ChatMath();
            math.Prepare(source, Pipeline);
            if (math.Tokens.Values.FirstOrDefault() is { Reference: null } token)
            {
                var formula = new ChatFormula(token.Source, token.Display);
                formula.SetValue(TextElement.FontSizeProperty, line.FontSize);
                formula.SetResourceReference(TextElement.ForegroundProperty, "Ink");
                return formula;
            }
            if (source.StartsWith("![", StringComparison.Ordinal))
                return new MarkdownView { ImageBaseDirectory = line._imageBaseDirectory, UseChatEmphasis = true,
                    Width = 120, MaxHeight = 90, Padding = new Thickness(0), Markdown = source };
            var literal = new TextBlock { Text = source, FontSize = line.FontSize, FontFamily = line.FontFamily };
            literal.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return literal;
        }
        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach (var (position, atom) in line._atoms)
                if (atom.HasObject && position.Offset + atom.Length - 1 >= startOffset) return position.Offset + atom.Length - 1;
            return -1;
        }
        public override VisualLineElement? ConstructElement(int offset)
        {
            foreach (var (pos, atom) in line._atoms)
            {
                if (!atom.HasObject || pos.Offset + atom.Length - 1 != offset) continue;
                var box = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
                if (atom.Length == 1 && atom.Source.Length > 0)
                    box.Children.Add(ProtectedObject(atom.Source));
                foreach (var link in atom.Links.Where(link => !RevisionBuffer.IsBookmark(link)))
                {
                    var bubble = new TextBlock { Text = "🗨️ " + link.Label, FontFamily = ChatReadingTypography.Family, FontSize = AiChatTypography.FontSize };
                    bubble.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                    box.Children.Add(bubble);
                }
                if (box.Children.Count == 0) box.Width = .5;
                return new InlineObjectElement(1, box);
            }
            return null;
        }

    }
}
