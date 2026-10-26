using TeXeed.Core;

namespace TeXeed.Tests;

public static class BookmarkTests
{
    public static void Run()
    {
        var main = new ProjectFile("main.tex", "main.tex", ProjectFileKind.Tex,
            "\\pdfbookmark[0]{Root}{bm:root}\n\\input{a}\n\\pdfbookmark[0]{Second}{bm:second}\n\\input{b}\n\\label{sec:real}\n% \\pdfbookmark{fake}{fake}\n\\begin{verbatim}\n\\bookmark[level=0]{fake}\n\\end{verbatim}\n");
        var a = new ProjectFile("a.tex", "a.tex", ProjectFileKind.Tex,
            "\\pdfbookmark[2]{Nested $x^2$}{bm:nested}\n\\belowpdfbookmark{Below}{bm:below}\n\\currentpdfbookmark{Same}{bm:same}\n\\subpdfbookmark{Sub}{bm:sub}\n\\bookmark[level=section,dest={pkg:one},color={1,0,0}]{Package}\n");
        var b = new ProjectFile("b.tex", "b.tex", ProjectFileKind.Tex, "\\bookmark[rellevel=1,dest=child]{Child}\n\\input{main}\n");
        ProjectFile[] files = [main, a, b]; var index = LatexIndexer.Build(files);
        Check.Equal(8, index.Bookmarks.Count); Check.Equal(1, index.Labels.Count); Check.Equal("sec:real", index.Labels[0].Title);
        Check.True(index.Bookmarks.Select(item => item.Title).SequenceEqual(new[] { "Root", "Nested x^2", "Below", "Same", "Sub", "Package", "Second", "Child" }), "Bookmarks follow expanded input order, separately from reference labels");
        Check.True(index.Bookmarks.Select(item => item.Level).SequenceEqual(new[] { 0, 2, 3, 2, 3, 1, 0, 1 }), "Explicit/current/sub/below and package levels are resolved across files");
        Check.Equal("pkg:one", index.Bookmarks[5].Detail); Check.Equal("Nested $x^2$", index.Bookmarks[1].Latex!);
        Check.Equal(3, index.Bookmarks[6].Line);
        var cache = new ProjectIndexCache(); var first = cache.Update(files);
        Check.True(first.Bookmarks.Select(item => item.Title).SequenceEqual(index.Bookmarks.Select(item => item.Title)));
        Check.True(ReferenceEquals(first, cache.Update(files))); Check.Equal(0, cache.LastParsedFileCount);
        var changed = main with { Content = main.Content.Replace("[0]{Second}", "[3]{Second}", StringComparison.Ordinal) };
        var next = cache.Update([changed, a, b]); Check.Equal(1, cache.LastParsedFileCount); Check.Equal(4, next.Bookmarks[^1].Level);
        Check.Equal(2, cache.Update([main]).Bookmarks.Count);
        Console.WriteLine("PASS: independent PDF bookmark index, source/input order, levels, destinations, rich titles, literal exclusion, include cycles and incremental invalidation");
    }
}
