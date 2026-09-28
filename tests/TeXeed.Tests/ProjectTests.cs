using System.Text;
using TeXeed.Core;

namespace TeXeed.Tests;

public static class ProjectTests
{
    public static async Task RunAsync()
    {
        var temporaryParent = Path.GetFullPath(Path.GetTempPath());
        var fixture = Path.Combine(temporaryParent, "texeed-project-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            await DiscoveryAndSaveAsync(fixture);
            await EncodingAndConflictAsync(fixture);
            Indexing();
            Formatting();
        }
        finally
        {
            var resolvedFixture = Path.GetFullPath(fixture);
            if (Path.GetDirectoryName(resolvedFixture) == Path.TrimEndingDirectorySeparator(temporaryParent) &&
                Path.GetFileName(resolvedFixture).StartsWith("texeed-project-tests-", StringComparison.Ordinal))
                Directory.Delete(resolvedFixture, true);
        }
    }

    private static async Task DiscoveryAndSaveAsync(string fixture)
    {
        var root = Path.Combine(fixture, "discovery");
        Directory.CreateDirectory(Path.Combine(root, "chapters", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "figures"));
        var main = Path.Combine(root, "main.tex");
        var source = """
            \documentclass{localclass}
            \usepackage{localstyle,amsmath}
            \bibliographystyle{localbib}
            % \input{commented}
            \input{chapters/one}
            \input{missing}
            \input{../outside}
            \input{\jobname-not-static}
            \bibliography{refs,other}
            \addbibresource[location=local]{third.bib}
            \graphicspath{{figures/}}
            \includegraphics[width=3cm]{chart}
            \verb|\input{verb-fake}|
            \begin{verbatim}
            \input{block-fake}
            \end{verbatim}
            """;
        await File.WriteAllTextAsync(main, source);
        await File.WriteAllTextAsync(Path.Combine(fixture, "outside.tex"), "outside");
        await File.WriteAllTextAsync(Path.Combine(root, "chapters", "one.tex"), "\\input{../main}\n\\subimport{nested/}{two}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "chapters", "nested", "two.tex"), "\\section{Nested}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "localstyle.sty"), "\\RequirePackage{another}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "another.sty"), "% local package");
        await File.WriteAllTextAsync(Path.Combine(root, "localclass.cls"), "\\LoadClass{article}");
        await File.WriteAllTextAsync(Path.Combine(root, "localbib.bst"), "ENTRY{}");
        foreach (var name in new[] { "refs", "other", "third" })
            await File.WriteAllTextAsync(Path.Combine(root, name + ".bib"), "@article{" + name + ", title={A title}}");
        await File.WriteAllBytesAsync(Path.Combine(root, "figures", "chart.png"), [137, 80, 78, 71, 0, 255]);
        var service = new ProjectService();
        var project = await service.LoadAsync(main);
        Check.Equal(11, project.Files.Count);
        Check.Equal(main, project.Files[0].Path);
        Check.True(project.Files.Any(f => f.RelativePath == Path.Combine("chapters", "nested", "two.tex")), "Nested imports are discovered");
        Check.True(project.Files.Single(f => f.Name == "chart.png") is { Kind: ProjectFileKind.Asset, Content: "" }, "Binary assets are metadata only");
        Check.True(project.Warnings.Any(w => w.Contains("missing")), "Missing input is reported");
        Check.True(project.Warnings.Any(w => w.Contains("outside")), "Root escape is reported");
        Check.True(project.Warnings.Any(w => w.Contains("jobname")), "Dynamic input is reported");
        Check.True(!project.Warnings.Any(w => w.Contains("fake") || w.Contains("commented") || w.Contains("amsmath")), "Comments, literals, and installed packages are not missing dependencies");

        await File.WriteAllTextAsync(Path.Combine(root, "new.tex"), "\\section{New}");
        var saved = await service.SaveAsync(project, new Dictionary<string, string> { [main] = source + "\n\\input{new}\n" });
        Check.True(saved.Files.Any(f => f.Name == "new.tex"), "Saving rebuilds the dependency graph");
        Check.True(!Directory.EnumerateFiles(root, ".texeed-*.tmp").Any(), "Atomic-save staging files are cleaned up");
        await Check.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(saved, new Dictionary<string, string> { [Path.Combine(fixture, "outside.tex")] = "changed" }));
        await Check.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(saved, new Dictionary<string, string> { [Path.Combine(root, "unloaded.tex")] = "changed" }));
        await Check.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(saved, new Dictionary<string, string> { [Path.Combine(root, "figures", "chart.png")] = "changed" }));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => service.LoadAsync(main, canceled.Token));

        // Creating links requires Developer Mode or elevated permission on some Windows hosts.
        var link = Path.Combine(root, "linked.tex");
        var linkCreated = false;
        try { File.CreateSymbolicLink(link, Path.Combine(fixture, "outside.tex")); linkCreated = true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { }
        if (linkCreated)
        {
            await File.AppendAllTextAsync(main, "\n\\input{linked}");
            var linked = await service.LoadAsync(main);
            Check.True(!linked.Files.Any(f => f.Name == "linked.tex"), "Symlinked dependencies are not followed");
            Check.True(linked.Warnings.Any(w => w.Contains("linked")), "Symlink warning is visible");
        }
    }

    private static async Task EncodingAndConflictAsync(string fixture)
    {
        var root = Path.Combine(fixture, "encoding");
        Directory.CreateDirectory(root);
        var main = Path.Combine(root, "main.tex");
        var child = Path.Combine(root, "child.tex");
        var legacy = Path.Combine(root, "legacy.tex");
        var bom = new UTF8Encoding(true, true);
        await File.WriteAllTextAsync(main, "\\input{child}\n\\input{legacy}\n你好", bom);
        await File.WriteAllTextAsync(child, "original", new UnicodeEncoding(false, true, true));
        await File.WriteAllBytesAsync(legacy, [0xD6, 0xD0, 0xCE, 0xC4]);
        var service = new ProjectService();
        var project = await service.LoadAsync(main);
        Check.True(project.Files.Single(f => f.Path == legacy).Kind == ProjectFileKind.Asset, "Unknown encodings cannot be accidentally overwritten");
        var updated = await service.SaveAsync(project, new Dictionary<string, string>
        {
            [main] = project.Files[0].Content + " world",
            [child] = "更新后的文字"
        });
        Check.True((await File.ReadAllBytesAsync(main)).AsSpan().StartsWith(bom.GetPreamble()), "UTF-8 BOM is preserved");
        Check.True((await File.ReadAllBytesAsync(child)).AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }), "UTF-16 BOM is preserved");
        Check.Equal("更新后的文字", updated.Files.Single(f => f.Path == child).Content);
        var mainBefore = await File.ReadAllBytesAsync(main);
        await File.WriteAllTextAsync(child, "external change");
        await Check.ThrowsAsync<IOException>(() => service.SaveAsync(updated, new Dictionary<string, string> { [main] = "should not save", [child] = "conflict" }));
        Check.True((await File.ReadAllBytesAsync(main)).SequenceEqual(mainBefore), "All changes are validated before the first replacement");
        Check.Equal("external change", await File.ReadAllTextAsync(child));
        Check.True(!Directory.EnumerateFiles(root, ".texeed-*.tmp").Any(), "Staging files are removed after a rejected save");
    }

    private static void Indexing()
    {
        var source = """
            % \section{Hidden} TODO: write introduction
            \section[Short]{A \textbf{bold} heading}
            \label{sec:intro}
            \subsection*{Next}
            \todo[inline]{Explain the proof}
            \verb|\label{hidden:inline}|
            \begin{minted}{tex}
            \label{hidden:block}
            % FIXME hidden in code
            \end{minted}
            A literal \% followed by text % FIXME: add a citation
            \bibitem[Example]{manual-key}
            """;
        var bib = """
            % @article{hidden, title={No}}
            @comment{ @article{also-hidden, title={No}} }
            @string{prefix = "Example"}
            @article{smith2026,
              title = {A real paper}, author = {Smith}
            }
            @book(jones, title={Second title ) and an @ sign})
            """;
        ProjectFile[] files = [new("main.tex", "main.tex", ProjectFileKind.Tex, source), new("refs.bib", "refs.bib", ProjectFileKind.Bibliography, bib)];
        var index = LatexIndexer.Build(files);
        Check.Equal(2, index.Outline.Count);
        Check.Equal("A bold heading", index.Outline[0].Title);
        Check.Equal(2, index.Outline[0].Line);
        Check.Equal(1, index.Labels.Count);
        Check.Equal("sec:intro", index.Labels[0].Title);
        Check.True(index.Labels[0].Detail.Contains("A bold heading"), "Reference completion includes the enclosing section");
        Check.Equal(3, index.Citations.Count);
        Check.True(index.Citations.Select(c => c.Title).SequenceEqual(new[] { "manual-key", "smith2026", "jones" }));
        Check.Equal(3, index.Todos.Count);
        Check.True(index.Todos.Any(t => t.Title == "write introduction" && t.Line == 1));
        Check.Equal(1, LatexIndexer.Search(files, "REAL PAPER").Count);
        Check.Equal(0, LatexIndexer.Search(files, " ").Count);
        Check.Equal(1, LatexIndexer.Search(files, "[Short]").Count);
    }

    private static void Formatting()
    {
        var source = "\\begin{document}\r\n\\begin{itemize}\r\n\\item First % keep { this comment\r\n\\end{itemize}\r\n\\begin{verbatim}\r\n     { leave   exactly  }\r\n% literal comment\r\n\\end{verbatim}\r\n\\[\r\nx + y\r\n\\]\r\n\\end{document}\r\n";
        var formatted = LatexFormatter.Format(source);
        Check.True(formatted.Contains("  \\begin{itemize}\r\n    \\item First % keep { this comment\r\n  \\end{itemize}"));
        Check.True(formatted.Contains("\\begin{verbatim}\r\n     { leave   exactly  }\r\n% literal comment\r\n\\end{verbatim}"), "Verbatim content remains byte-for-byte unchanged");
        Check.True(formatted.Contains("  \\[\r\n    x + y\r\n  \\]"), "Display math indentation is balanced");
        Check.Equal(formatted, LatexFormatter.Format(formatted));
        Check.True(formatted.EndsWith("\\end{document}\r\n", StringComparison.Ordinal));
        Check.Equal("", LatexFormatter.Format(""));
        Check.Equal("plain", LatexFormatter.Format("   plain"));
    }
}
