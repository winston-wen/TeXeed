using TeXeed.Core;

namespace TeXeed.Tests;

internal static class TitleTests
{
    internal static async Task RunAsync()
    {
        Check.Equal("中文 Bold italic & 50%", LatexTitle.PlainText(@"中文 \textbf{Bold \emph{italic}} \& 50\%"));
        Check.Equal("公式 α_i^2 + (1)/(√(x))", LatexTitle.PlainText(@"公式 $\alpha_i^2 + \frac{1}{\sqrt{x}}$"));
        Check.Equal("保留 \\unknown内容", LatexTitle.PlainText(@"保留 \unknown{内容}"));
        Check.Equal("Visible", LatexTitle.PlainText(@"\texorpdfstring{\textbf{Visible}}{PDF}\label{hidden}\thanks{hidden}"));
        Check.Equal("unfinished", LatexTitle.PlainText(@"\textbf{unfinished"));
        foreach (var source in new[] { @"$\sum_{k=0}^{114}\frac{(-1)^{2k+1}}{(2k+1)!}$", @"\(\sum_{k=0}^{114}\frac{(-1)^{2k+1}}{(2k+1)!}\)", @"\[\sum_{k=0}^{114}\frac{(-1)^{2k+1}}{(2k+1)!}\]" })
            Check.Equal(@"\sum_{k=0}^{114}\frac{(-1)^{2k+1}}{(2k+1)!}", LatexTitle.Parse(source).Single().Text);
        Check.True(LatexTitle.PlainText(new string('{', 10000)).Length < 8300, "Pathological titles are bounded");
        var index = LatexIndexer.Build([new ProjectFile("main.tex", "main.tex", ProjectFileKind.Tex,
            "\\section[Short]{Energy $E=mc^2$}\n\\subsection{\\frac{a}{b} 与 \\textbf{中文}}")]);
        Check.Equal(2, index.Outline.Count);
        Check.Equal("Energy E=mc^2", index.Outline[0].Title);
        Check.Equal("Energy $E=mc^2$", index.Outline[0].Latex);
        Check.Equal(@"\frac{a}{b} 与 \textbf{中文}", index.Outline[1].Latex);
        Check.Equal(2, index.Outline[1].Line);

        var root = Path.Combine(Path.GetTempPath(), "texeed-title-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var main = Path.Combine(root, "main.tex");
            await File.WriteAllTextAsync(main, "% \\title{Not this}\n\\title{My \\textbf{Paper} $\\alpha$}\\section{Start}");
            var service = new ProjectService();
            var project = await service.LoadAsync(main);
            Check.Equal(new DirectoryInfo(root).Name, project.Name);
            Check.Equal("My Paper α", project.DocumentTitle);
            project = await service.SetNameAsync(project, "  论文项目  ");
            Check.Equal("论文项目", (await service.LoadAsync(main)).Name);
            project = await service.SaveAsync(project, new Dictionary<string, string> { [main] = project.Files[0].Content + "\nMore" });
            Check.Equal("论文项目", project.Name);
            await service.SetNameAsync(project, "");
            Check.Equal(new DirectoryInfo(root).Name, (await service.LoadAsync(main)).Name);
            await Check.ThrowsAsync<ArgumentException>(() => service.SetNameAsync(project, "bad\nname"));
            await File.WriteAllTextAsync(Path.Combine(root, ".texeed", "project.json"), "invalid");
            project = await service.LoadAsync(main);
            Check.Equal(new DirectoryInfo(root).Name, project.Name);
            Check.True(project.Warnings.Any(w => w.Contains("名称配置")), "Invalid metadata is reported without preventing opening");
        }
        finally
        {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                && Path.GetFileName(root).StartsWith("texeed-title-tests-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
        Console.WriteLine("PASS: LaTeX title parsing, preserved outline source, project folder defaults, title extraction and name persistence");
    }
}
