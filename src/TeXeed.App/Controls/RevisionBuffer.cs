using System.Text;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

// One editor position per protected object. Persist offsets in the original Markdown text.
internal sealed record RevisionAtom(int At, string Source, IReadOnlyList<AiRevisionLink> Links);
internal sealed record RevisionBuffer(string Display, IReadOnlyList<RevisionAtom> Atoms)
{
    internal const char Marker = '\ufffc';
    internal static RevisionBuffer From(AiRevisionRow row)
    {
        var spans = AiRevisionText.ProtectedSpans(row.Text).OrderBy(span => span.Start).ToArray();
        var links = row.Links.OrderBy(link => link.Offset).ToArray();
        var display = new StringBuilder();
        var atoms = new List<RevisionAtom>();
        var at = 0; var nextLink = 0; var nextSpan = 0;
        void AddAtom(string source, IReadOnlyList<AiRevisionLink> inside)
        {
            atoms.Add(new(display.Length, source, inside));
            display.Append(Marker);
        }
        while (at <= row.Text.Length)
        {
            while (nextLink < links.Length && links[nextLink].Offset <= at)
                AddAtom("", [links[nextLink++] with { Offset = 0 }]);
            if (at == row.Text.Length) break;
            if (nextSpan < spans.Length && spans[nextSpan].Start == at)
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
            start = atom.At + 1;
        }
        text.Append(Display, start, Display.Length - start);
        return row with { Text = text.ToString(), Links = links };
    }

    internal RevisionBuffer Slice(int start, int length) =>
        new(Display.Substring(start, length), Atoms.Where(a => a.At >= start && a.At < start + length)
            .Select(a => a with { At = a.At - start }).ToArray());

    internal RevisionBuffer Append(RevisionBuffer suffix) =>
        new(Display + suffix.Display, [.. Atoms, .. suffix.Atoms.Select(a => a with { At = Display.Length + a.At })]);

    internal RevisionBuffer ProtectedOnly() =>
        new(new string(Marker, Atoms.Count), Atoms.Select((a, i) => a with { At = i }).ToArray());

    internal static RevisionBuffer Plain(string value) => new(value, []);
}
