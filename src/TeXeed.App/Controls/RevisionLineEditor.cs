using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using Markdig;
using System.Windows.Media;
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
        Document.UndoStack.SizeLimit = 0;
        Document.UpdateFinished += (_, _) => Changed?.Invoke();
        TextArea.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(RunCommand), true);
        PreviewDragOver += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; };
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
            var selected = Buffer.Slice(SelectionStart, SelectionLength);
            var plain = selected.Apply(new("", "", "", 0, false, [])).Text;
            try { if (plain.Length > 0) Clipboard.SetText(plain); }
            catch (System.Runtime.InteropServices.ExternalException) { ToolTip = "剪贴板暂时被占用"; return; }
            if (e.Command == ApplicationCommands.Cut) PasteText?.Invoke("");
        }
    }

    private sealed class ProtectedSections(RevisionLineEditor line) : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => true;
        public IEnumerable<ISegment> GetDeletableSegments(ISegment selection)
        {
            var start = selection.Offset;
            foreach (var (position, _) in line._atoms)
            {
                var offset = position.Offset;
                if (offset < start) continue;
                if (offset >= selection.EndOffset) break;
                if (offset > start) yield return new TextSegment { StartOffset = start, Length = offset - start };
                start = offset + 1;
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
            foreach (var (position, _) in line._atoms)
                if (position.Offset >= startOffset) return position.Offset;
            return -1;
        }
        public override VisualLineElement? ConstructElement(int offset)
        {
            foreach (var (pos, atom) in line._atoms)
            {
                if (pos.Offset != offset) continue;
                var box = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
                if (atom.Source.Length > 0)
                    box.Children.Add(ProtectedObject(atom.Source));
                foreach (var link in atom.Links.Where(link => !link.Id.StartsWith("bookmark:", StringComparison.Ordinal)))
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
