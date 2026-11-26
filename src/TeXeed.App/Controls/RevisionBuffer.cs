using System.Text;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

// Protected content occupies a range in the editor. Persist offsets in the original Markdown text.
internal sealed record RevisionAtom(int At, string Source, IReadOnlyList<AiRevisionLink> Links, int Length = 1, bool HasObject = true)
{
    internal int End => At + Length;
}
internal sealed record RevisionBuffer(string Display, IReadOnlyList<RevisionAtom> Atoms)
{
    internal const char Marker = '\ufffc';
    internal int DisplayOffset(int sourceOffset)
    {
        var shift = 0;
        foreach (var atom in Atoms)
        {
            var sourceStart = atom.At - shift;
            if (sourceOffset <= sourceStart) break;
            if (sourceOffset < sourceStart + atom.Source.Length) return atom.End;
            shift += atom.Length - atom.Source.Length;
        }
        return Math.Clamp(sourceOffset + shift, 0, Display.Length);
    }
    internal static RevisionBuffer From(AiRevisionRow row, bool editableInlineMath = false)
    {
        var spans = AiRevisionText.ProtectedSpans(row.Text, editableInlineMath);
        var links = row.Links.OrderBy(link => link.Offset).ToArray();
        var display = new StringBuilder();
        var atoms = new List<RevisionAtom>();
        var at = 0; var nextLink = 0; var nextSpan = 0;
        void AddAtom(string source, IReadOnlyList<AiRevisionLink> inside)
        {
            var showSource = editableInlineMath && (source.StartsWith(@"\eqref{", StringComparison.Ordinal) ||
                source.StartsWith(@"\ref{", StringComparison.Ordinal));
            var showObject = !showSource || inside.Any(link => !IsBookmark(link));
            var start = display.Length;
            if (showSource) display.Append(source);
            if (showObject) display.Append(Marker);
            atoms.Add(new(start, source, inside, display.Length - start, showObject));
        }
        while (at <= row.Text.Length)
        {
            while (nextLink < links.Length && links[nextLink].Offset <= at)
                AddAtom("", [links[nextLink++] with { Offset = 0 }]);
            if (at == row.Text.Length) break;
            if (nextSpan < spans.Count && spans[nextSpan].Start == at)
            {
                var end = at + spans[nextSpan++].Length;
                var inside = new List<AiRevisionLink>();
                while (nextLink < links.Length && links[nextLink].Offset < end)
                {
                    var link = links[nextLink++];
                    inside.Add(link with { Offset = link.Offset - at });
                }
                AddAtom(row.Text[at..end], inside); at = end;
            }
            else display.Append(row.Text[at++]);
        }
        return new(display.ToString(), atoms);
    }

    internal AiRevisionRow Apply(AiRevisionRow row)
    {
        var text = new StringBuilder();
        var links = new List<AiRevisionLink>();
        var start = 0;
        foreach (var atom in Atoms)
        {
            text.Append(Display, start, atom.At - start);
            foreach (var link in atom.Links) links.Add(link with { Offset = text.Length + link.Offset });
            text.Append(atom.Source);
            start = atom.End;
        }
        text.Append(Display, start, Display.Length - start);
        return row with { Text = text.ToString(), Links = links };
    }

    internal RevisionBuffer Slice(int start, int length) =>
        new(Display.Substring(start, length), Atoms.Where(a => a.At >= start && a.At < start + length)
            .Select(a => a with { At = a.At - start }).ToArray());

    // A soft wrap can place the caret inside protected source. Edits and clipboard
    // commands still operate on the whole reference whenever a selection touches it.
    internal (int Start, int End) WholeAtoms(int start, int end)
    {
        foreach (var atom in Atoms)
        {
            if (atom.At >= end) break;
            if (start > atom.At && start < atom.End) start = atom.At;
            if (end > atom.At && end < atom.End) end = atom.End;
        }
        return (start, end);
    }

    internal RevisionBuffer Append(RevisionBuffer suffix) =>
        new(Display + suffix.Display, [.. Atoms, .. suffix.Atoms.Select(a => a with { At = Display.Length + a.At })]);

    internal RevisionBuffer ProtectedOnly()
    {
        var display = new StringBuilder();
        var atoms = new List<RevisionAtom>(Atoms.Count);
        foreach (var atom in Atoms)
        {
            atoms.Add(atom with { At = display.Length });
            display.Append(Display, atom.At, atom.Length);
        }
        return new(display.ToString(), atoms);
    }

    internal static bool IsBookmark(AiRevisionLink link) => link.Id.StartsWith("bookmark:", StringComparison.Ordinal);

    internal static RevisionBuffer Plain(string value) => new(value, []);
}
