using System.Diagnostics;
using TeXeed.Core;

namespace TeXeed.Tests;

internal static class NestedEntryTests
{
    public static async Task<int> RunRealAsync(string tectonic, string xelatex, string output)
    {
        try
        {
            foreach (var (engine, executable) in new[] { (TexEngine.XeLaTeX, xelatex), (TexEngine.Tectonic, tectonic) })
            {
                var root = Path.GetFullPath(Path.Combine(output, engine.ToString()));
                var nested = Path.Combine(root, "validation"); Directory.CreateDirectory(nested);
                var main = Path.Combine(nested, "main.tex");
                var preamble = Path.Combine(nested, "preamble.tex");
                var fragment = Path.Combine(root, "fragment.tex");
                await File.WriteAllTextAsync(Path.Combine(root, "preamble.tex"), "\\errmessage{WRONG ROOT TEMPLATE}\n");
                await File.WriteAllTextAsync(preamble, "\\newcommand{\\templateword}{Nested template selected}\n\\typeout{NESTED-TEMPLATE-SELECTED}\n");
                await File.WriteAllTextAsync(fragment, "\\typeout{PARENT-FRAGMENT-SELECTED}\nParent fragment selected.\\par\n");
                await File.WriteAllTextAsync(Path.Combine(root, "refs.bib"), "@book{rootref,title={WRONG ROOT BIB},author={Wrong},year={1900}}\n");
                await File.WriteAllTextAsync(Path.Combine(nested, "refs.bib"), "@book{nestedref,title={Nested bibliography selected},author={Entry, Correct},year={2026}}\n");
                await File.WriteAllTextAsync(Path.Combine(root, "parentrefs.bib"), "@book{parentref,title={Parent bibliography selected},author={Parent, Correct},year={2025}}\n");
                Directory.CreateDirectory(Path.Combine(root, "chapters"));
                await File.WriteAllTextAsync(Path.Combine(root, "chapters", "one.tex"), "Root chapter included.\n");
                await File.WriteAllTextAsync(Path.Combine(root, "chapters", "two.tex"), "Parent chapter included.\n");
                var mainText = """
                    \documentclass{article}
                    \input{preamble}
                    \begin{document}
                    \templateword.\par
                    \input{../fragment}
                    \include{chapters/one}
                    Nested citation\cite{nestedref,parentref}.
                    \bibliographystyle{plain}
                    \bibliography{refs,../parentrefs}
                    \end{document}
                    """;
                if (engine == TexEngine.Tectonic) mainText = mainText.Replace("\\include{chapters/one}", "");
                await File.WriteAllTextAsync(main, mainText);
                var project = await new ProjectService().LoadAsync(main, rootDirectory: root);
                Check.True(project.Files.Any(f => f.Path == preamble) && project.Files.Any(f => f.Path == fragment), "Nested template and parent fragment must be discovered");
                var settings = new AppSettings { Engine = engine, EnginePath = Path.GetFullPath(executable), SynctexPath = Path.Combine(root, "missing-synctex.exe") };
                var service = new CompileService();
                var result = await service.CompileAsync(project, settings);
                await File.WriteAllTextAsync(Path.Combine(root, ".texeed", "integration.log"), result.Log);
                Check.True(result.Success, engine + " nested compilation failed: " + result.Log);
                Check.True(result.InputsCurrent == true, engine + " could not verify stable nested inputs");
                Check.True(result.Log.Contains("NESTED-TEMPLATE-SELECTED") && result.Log.Contains("PARENT-FRAGMENT-SELECTED"), "Engine did not read the intended template/parent fragment");
                Check.True(!result.Diagnostics.Any(d => d.Message.Contains("undefined", StringComparison.OrdinalIgnoreCase)), "Nested bibliography failed to resolve");
                Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: true }, "Nested entry manifest cannot be restored");
                Check.True(TexPageLayout.Read(result.PdfPath!).Count > 0, "Nested layout metadata cannot be read");
                var pdfText = await ExtractTextAsync(Path.Combine(Path.GetDirectoryName(xelatex)!, "pdftotext.exe"), result.PdfPath!);
                await File.WriteAllTextAsync(Path.Combine(root, ".texeed", "rendered.txt"), pdfText);
                foreach (var expected in new[] { "Nested template selected", "Parent fragment selected", "Nested bibliography selected", "Parent bibliography selected" })
                    Check.True(pdfText.Contains(expected), engine + " PDF text missing: " + expected + "; actual: " + pdfText);
                if (engine == TexEngine.XeLaTeX) Check.True(pdfText.Contains("Root chapter included"), "XeLaTeX root-relative include lost its chapter/aux directory");
                Check.True(!pdfText.Contains("WRONG ROOT"), "Root template/bibliography shadowed the entry's local file");
                var sync = new SyncTexService();
                var point = await sync.ForwardAsync(project, result.PdfPath!, new(fragment, 2), settings);
                Check.True(point is not null, engine + " native SyncTeX failed to resolve ../fragment");
                var inverse = await sync.InverseAsync(project, result.PdfPath!, point!, settings);
                Check.True(inverse?.Path == fragment, engine + " native inverse did not resolve the parent fragment");
                var cliSettings = settings with { SynctexPath = Path.Combine(Path.GetDirectoryName(xelatex)!, "synctex.exe") };
                point = await sync.ForwardAsync(project, result.PdfPath!, new(fragment, 2), cliSettings);
                Check.True(point is not null, engine + " CLI SyncTeX failed for the nested entry");
                inverse = await sync.InverseAsync(project, result.PdfPath!, point!, cliSettings);
                Check.True(inverse?.Path == fragment, engine + " CLI inverse did not resolve the parent fragment");
                var diagnostic = CompileService.ParseDiagnostics("preamble.tex:2: Undefined control sequence.\n../fragment.tex:2: Undefined control sequence.", project);
                Check.True(diagnostic[0].Path == preamble && diagnostic[1].Path == fragment, "Relative diagnostics resolved against the project root instead of the actual entry");
                if (engine == TexEngine.XeLaTeX)
                {
                    await File.AppendAllTextAsync(Path.Combine(root, "preamble.tex"), "% unused root template changed\n");
                    Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: true }, "Recorder incorrectly bound nested preamble to root preamble");
                    var biberMain = Path.Combine(nested, "biber-main.tex");
                    await File.WriteAllTextAsync(biberMain, """
                        \documentclass{article}
                        \input{preamble}
                        \usepackage[backend=biber]{biblatex}
                        \addbibresource{refs.bib}
                        \addbibresource{../parentrefs.bib}
                        \begin{document}
                        \templateword.\par
                        \input{../fragment}
                        Nested citation\cite{nestedref,parentref}.
                        \printbibliography
                        \end{document}
                        """);
                    var biberProject = await new ProjectService().LoadAsync(biberMain, rootDirectory: root);
                    var biber = await service.CompileAsync(biberProject, settings);
                    await File.WriteAllTextAsync(Path.Combine(root, ".texeed", "biber.log"), biber.Log);
                    Check.True(biber.Success, "Nested Biber compilation failed: " + biber.Log);
                    var biberText = await ExtractTextAsync(Path.Combine(Path.GetDirectoryName(xelatex)!, "pdftotext.exe"), biber.PdfPath!);
                    Check.True(biberText.Contains("Nested bibliography selected") && biberText.Contains("Parent bibliography selected") && !biberText.Contains("WRONG ROOT"), "Nested Biber selected the wrong bibliography data");
                    var priorPdf = await File.ReadAllBytesAsync(result.PdfPath!);
                    var originalMain = await File.ReadAllTextAsync(main);
                    await File.WriteAllTextAsync(main, originalMain.Replace("\\include{chapters/one}", "\\include{../chapters/two}"));
                    var rejectedInclude = await service.CompileAsync(await new ProjectService().LoadAsync(main, rootDirectory: root), settings);
                    Check.True(!rejectedInclude.Success && rejectedInclude.Log.Contains("openout_any"), "Parent include aux must retain TeX Live's native write restriction");
                    var retainedPdf = await File.ReadAllBytesAsync(result.PdfPath!);
                    Check.True(priorPdf.SequenceEqual(retainedPdf), "Unsafe include failure replaced the last valid PDF");
                    await File.WriteAllTextAsync(main, originalMain);
                }
                else
                {
                    var priorPdf = await File.ReadAllBytesAsync(result.PdfPath!);
                    await File.WriteAllTextAsync(main, mainText.Replace("\\end{document}", "\\include{chapters/one}\n\\end{document}"));
                    var missingChapter = await service.CompileAsync(await new ProjectService().LoadAsync(main, rootDirectory: root), settings);
                    Check.True(!missingChapter.Success && missingChapter.Diagnostics.Any(d => d.Message.Contains("chapters/one.tex")), "Tectonic silently published a PDF with a missing chapter");
                    var retainedPdf = await File.ReadAllBytesAsync(result.PdfPath!);
                    Check.True(priorPdf.SequenceEqual(retainedPdf), "A missing chapter overwrote the complete last preview");
                    await File.WriteAllTextAsync(main, mainText);
                }
                await File.AppendAllTextAsync(preamble, "% changed nested template\n");
                Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: false }, "Nested dependency change did not invalidate its PDF");
                await File.WriteAllTextAsync(fragment, "\\undefinedNestedFragmentCommand\n");
                var badProject = await new ProjectService().LoadAsync(main, rootDirectory: root);
                var failure = await service.CompileAsync(badProject, settings);
                await File.WriteAllTextAsync(Path.Combine(root, ".texeed", "failure.log"), failure.Log);
                Check.True(!failure.Success, "Invalid parent fragment unexpectedly compiled");
                Check.True(failure.Diagnostics.Any(d => !d.IsWarning && d.Path == fragment && d.Line == 1), engine + " actual compiler diagnostics lost the parent fragment location: " + string.Join("; ", failure.Diagnostics));
                Console.WriteLine($"PASS: {engine} nested template precedence, ../fragment, bibliography, actual PDF text, native/CLI SyncTeX, layout, cache and relative diagnostics. PDF: {result.PdfPath}");
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static async Task<string> ExtractTextAsync(string executable, string pdf)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(pdf); start.ArgumentList.Add("-");
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check.True(process.ExitCode == 0, await error);
        return await text;
    }
}
