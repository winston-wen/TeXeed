using System.Windows;
using System.Windows.Documents;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private sealed record HeadingInfo(int Level, int Offset);
    private Dictionary<string, bool>? _folds;
    private string _foldPrefix = "";
    private Action? _foldChanged;
    private readonly Dictionary<Section, Action> _revealSections = [];
    private void Reveal(FrameworkContentElement target)
    {
        for (FrameworkContentElement? node = target; node is not null; node = node.Parent as FrameworkContentElement)
            if (node is Section section && _revealSections.TryGetValue(section, out var reveal)) reveal();
    }
    internal void ConfigureFolding(Dictionary<string, bool> folds, string prefix, Action changed)
    { _folds = folds; _foldPrefix = prefix; _foldChanged = changed; if (Markdown.Length > 0) Render(); }

    private void ApplyFolding(FlowDocument document)
    {
        if (_folds is null) return;
        _revealSections.Clear();
        var blocks = document.Blocks.ToArray(); document.Blocks.Clear();
        var parents = new Stack<(int Level, Section Body)>();
        foreach (var block in blocks)
        {
            if (block is Paragraph { Tag: HeadingInfo heading } paragraph)
            {
                while (parents.Count > 0 && parents.Peek().Level >= heading.Level) parents.Pop();
                var owner = new Section();
                if (parents.Count > 0) parents.Peek().Body.Blocks.Add(owner); else document.Blocks.Add(owner);
                owner.Blocks.Add(paragraph);
                var body = new Section(); owner.Blocks.Add(body);
                var key = _foldPrefix + ":" + heading.Offset;
                // Keep headings inside the text engine. A Button inside every heading
                // creates hundreds of embedded layout/hit-test islands in long answers.
                var link = new Hyperlink { TextDecorations = null };
                link.SetResourceReference(TextElement.ForegroundProperty, "Ink");
                while (paragraph.Inlines.FirstInline is { } inline) { paragraph.Inlines.Remove(inline); link.Inlines.Add(inline); }
                var hint = new Run("（已折叠）") { FontWeight = FontWeights.Normal, FontSize = paragraph.FontSize * .8 };
                hint.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                paragraph.Inlines.Add(link);
                // Attach/detach only this section's body; preserve nested controls and their state.
                link.Click += (_, e) =>
                {
                    var closed = !_folds.GetValueOrDefault(key); _folds[key] = closed;
                    if (closed) { owner.Blocks.Remove(body); link.Inlines.Add(hint); }
                    else { owner.Blocks.Add(body); link.Inlines.Remove(hint); }
                    LayoutVersion++; _annotationPointers.Clear(); _foldChanged?.Invoke(); e.Handled = true;
                };
                _revealSections[body] = () =>
                {
                    if (!_folds.GetValueOrDefault(key)) return;
                    _folds[key] = false; owner.Blocks.Add(body); link.Inlines.Remove(hint);
                    LayoutVersion++; _annotationPointers.Clear(); _foldChanged?.Invoke();
                };
                if (_folds.GetValueOrDefault(key)) { owner.Blocks.Remove(body); link.Inlines.Add(hint); }
                parents.Push((heading.Level, body));
            }
            else if (parents.Count > 0) parents.Peek().Body.Blocks.Add(block);
            else document.Blocks.Add(block);
        }
    }
}
