using System.IO;
using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public sealed partial class LatexEditor
{
    private readonly Dictionary<string, (TextDocument Document, TextAnchor? Anchor)> _annotationAnchors = [];
    internal Point? AnnotationPosition(AiEntry entry, string root)
    {
        if (entry.Sources.FirstOrDefault() is not { } source || Document is null || CurrentFilePath is null
            || !string.Equals(Path.GetFullPath(Path.Combine(root, source.File)), CurrentFilePath, StringComparison.OrdinalIgnoreCase)) return null;
        if (!_annotationAnchors.TryGetValue(entry.Id, out var cached) || cached.Document != Document)
        {
            int offset = -1;
            if (AiStorage.Hash(Text) == source.Revision) offset = Math.Min(source.Offset, Document.TextLength);
            else if (source.Text.Length > 0)
            {
                offset = Text.IndexOf(source.Text, StringComparison.Ordinal);
                if (offset >= 0 && Text.IndexOf(source.Text, offset + 1, StringComparison.Ordinal) >= 0) offset = -1;
            }
            TextAnchor? anchor = offset >= 0 ? Document.CreateAnchor(offset) : null;
            if (anchor is not null) anchor.MovementType = AnchorMovementType.AfterInsertion;
            _annotationAnchors[entry.Id] = cached = (Document, anchor);
            if (_annotationAnchors.Count > 500) { _annotationAnchors.Clear(); _annotationAnchors[entry.Id] = cached; }
        }
        if (cached.Anchor is { IsDeleted: true } && AiStorage.Hash(Text) == source.Revision)
        {
            var restored = Document.CreateAnchor(Math.Min(source.Offset, Document.TextLength));
            restored.MovementType = AnchorMovementType.AfterInsertion;
            _annotationAnchors[entry.Id] = cached = (Document, restored);
        }
        if (cached.Anchor is not { IsDeleted: false } position || !TextArea.TextView.VisualLinesValid) return null;
        var point = TextArea.TextView.GetVisualPosition(new TextViewPosition(Document.GetLocation(position.Offset)), VisualYPosition.LineTop) - TextArea.TextView.ScrollOffset;
        if (point.Y < 0 || point.Y >= TextArea.TextView.ActualHeight || point.X < 0 || point.X > TextArea.TextView.ActualWidth) return null;
        return TextArea.TextView.TranslatePoint(point, this);
    }
}
