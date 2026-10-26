using System.Diagnostics;
using TeXeed.Core;

namespace TeXeed.Tests;

public static class IndexCacheTests
{
    public static void Run()
    {
        const int rows = 20000;
        var body = "\\section{Dense}\r\n" + string.Concat(Enumerable.Repeat("\\BookInline{\\frac{a_i}{1+x^2}} % formula\n", rows))
            + "\\label{tail}\r\n% TODO: finish\r\n\\begin{verbatim}\n\\section{fake}\n\\label{fake}\n\\end{verbatim}\n";
        var file = new ProjectFile("dense.tex", "dense.tex", ProjectFileKind.Tex, body);
        var watch = Stopwatch.StartNew(); var index = LatexIndexer.Build([file]);
        Check.Equal(1, index.Outline.Count); Check.Equal(1, index.Labels.Count);
        Check.Equal(rows + 2, index.Labels[0].Line); Check.Equal(rows + 3, index.Todos[0].Line);
        Check.True(watch.ElapsedMilliseconds < 1500, "Dense irrelevant commands must not trigger repeated source-prefix scans");
        Console.WriteLine($"PASS: dense {body.Length} character index and exact mixed-newline/literal line mapping in {watch.Elapsed.TotalMilliseconds:F1}ms");

        var other = new ProjectFile("refs.bib", "refs.bib", ProjectFileKind.Bibliography, "@book{one, title={First}}\n@article{two, title={Second}}");
        var asset = new ProjectFile("figure.pdf", "figure.pdf", ProjectFileKind.Asset, "");
        var cache = new ProjectIndexCache(); var first = cache.Update([file, other, asset]);
        Check.Equal(2, cache.LastParsedFileCount);
        var cloned = file with { Content = new string(body.ToCharArray()) };
        Check.True(ReferenceEquals(first, cache.Update([cloned, other, asset])), "Identical contents reuse the complete snapshot even after a disk reload");
        Check.Equal(0, cache.LastParsedFileCount);
        var changed = file with { Content = body.Replace("{tail}", "{edited}", StringComparison.Ordinal) };
        var next = cache.Update([changed, other, asset]);
        Check.Equal(1, cache.LastParsedFileCount); Check.Equal("edited", next.Labels[0].Title);
        Check.True(ReferenceEquals(first.Citations[0], next.Citations[0]), "An unchanged file retains its parsed entries");
        var reordered = cache.Update([other, changed]);
        Check.Equal(0, cache.LastParsedFileCount);
        Check.True(reordered.Citations.Select(citation => citation.Title).SequenceEqual(new[] { "one", "two" }), "Reordered merge preserves citation order");
        var renamed = changed with { RelativePath = "chapters/dense.tex" };
        Check.True(cache.Update([renamed]).Labels[0].Detail.Contains("chapters/dense.tex"), "Relative path changes refresh entry descriptions");
        Check.Equal(1, cache.LastParsedFileCount);
        Check.Equal(0, cache.Update([]).Labels.Count);
        cache.Clear(); cache.Update([other]); Check.Equal(1, cache.LastParsedFileCount);
        Console.WriteLine("PASS: per-file incremental indexes, equal-content reuse, changed metadata, ordering, removals and session reset");
    }
}
