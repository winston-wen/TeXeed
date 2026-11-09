using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiBuildFailureUiAsync(Window owner, string output)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var project = Path.Combine(output, "build-failure-ui", "main.tex");
        var document = new AiSharedDocument();
        var valid = "\\documentclass{article}\n\\begin{document}\nHello.\n\\end{document}\n";
        Check(document.TryInitialize(valid, "模型"), "Could not initialize document.");
        document.Comments.Add(new AiDocumentComment { Start = valid.IndexOf("Hello", StringComparison.Ordinal), Length = 5, Quote = "Hello", Text = "保留此评论" });
        var settings = new AppSettings { Engine = TexEngine.PdfLaTeX, AutoCropPdf = false };
        using var view = new AiSessionPdfView(project, "failure-ui-" + Guid.NewGuid().ToString("N"), document, settings);
        var host = new Window { Owner = owner, Content = view, Width = 900, Height = 700, ShowInTaskbar = false };
        host.Show();
        try
        {
            await view.CompileAsync(); var previous = view.Displayed;
            Check(previous is not null && view.Preview.PageCount > 0, "Initial real PDF compile failed.");
            var invalid = valid.Replace("Hello.", "\\undefinedcommand", StringComparison.Ordinal);
            Check(document.TryApply(document.Revision, invalid, "用户"), "Could not prepare invalid source.");
            var comments = JsonSerializer.Serialize(document.Comments);
            await view.CompileAsync(); host.UpdateLayout();
            Check(document.Source == invalid && JsonSerializer.Serialize(document.Comments) == comments, "Failed compile changed source or comments.");
            Check(ReferenceEquals(view.Displayed, previous) && view.Preview.PageCount > 0 && File.Exists(previous!.PdfPath), "Failed compile discarded last successful PDF.");
            Check(view.StatusText.Contains("编译失败") && view.StatusText.Length < 100 && view.Preview.ActualHeight > 100, "Failure status obscured the previous PDF.");
            var logExpander = (Expander)Field(view, "_buildLog")!;
            Check(logExpander.Visibility == Visibility.Visible && !logExpander.IsExpanded && logExpander.Content is null, "Failure log was not available lazily.");
            Check(Descendants<Button>(view).Any(b => Equals(b.Content, "编译会话文稿") && b.IsEnabled), "Recompile entry became unavailable.");
            Capture(host, Path.Combine(output, "session-build-failed.png"));
            logExpander.IsExpanded = true; host.UpdateLayout();
            var text = Descendants<TextBox>(logExpander).Single();
            await File.WriteAllTextAsync(Path.Combine(output, "captured-compiler-log.txt"), text.Text);
            Check(text.IsReadOnly && text.Text.Contains("\\undefinedcommand", StringComparison.Ordinal) && text.Text.Length > 100, "Full compiler output was not selectable: " + text.Text[..Math.Min(text.Text.Length, 250)]);
            var copied = "";
            Check(view.CopyBuildLog(value => { copied = value; return true; }) && copied == text.Text, "Copy omitted log content.");
            Check(Descendants<Button>(logExpander).Any(b => Equals(b.Content, "复制日志")), "Copy entry missing.");
            Capture(host, Path.Combine(output, "session-build-log-expanded.png"));
            logExpander.IsExpanded = false; host.UpdateLayout(); Check(logExpander.Content is null, "Collapsed log retained controls.");
            Check(document.TryApply(document.Revision, valid, "用户"), "Could not repair source.");
            await view.CompileAsync(); host.UpdateLayout();
            Check(view.Displayed is not null && !ReferenceEquals(previous, view.Displayed) && view.StatusText.Contains("编译成功") && logExpander.Visibility == Visibility.Collapsed, "Recompile did not replace the old preview and clear the old error.");

            var bytes = Encoding.UTF8.GetBytes(new string('x', 4 * 1024 * 1024 + 17) + "END");
            var reader = typeof(CompileService).GetMethod("ReadLimitedAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                null, [typeof(Stream), typeof(CancellationToken), typeof(int)], null)!;
            var captured = await (Task<string>)reader.Invoke(null, [new MemoryStream(bytes), CancellationToken.None, int.MaxValue])!;
            Check(captured.Length == bytes.Length && captured.EndsWith("END", StringComparison.Ordinal), "Full TeX log reader truncated content.");
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var arguments = new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write(('x' * 4194321) + 'END')" };
            async Task<(int ExitCode, string Output)> RunOutputAsync(int limit)
            {
                var method = typeof(CompileService).GetMethod("RunProcessAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                var task = (Task)method.Invoke(null, [shell, arguments, output, null, CancellationToken.None, limit])!;
                await task;
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                return ((int)result.GetType().GetProperty("ExitCode")!.GetValue(result)!, (string)result.GetType().GetProperty("Output")!.GetValue(result)!);
            }
            var fullOutput = await RunOutputAsync(int.MaxValue);
            Check(fullOutput.ExitCode == 0 && fullOutput.Output.Length == 4194324 && fullOutput.Output.EndsWith("END", StringComparison.Ordinal), "Full process output was truncated.");
            var limitedOutput = await RunOutputAsync(4 * 1024 * 1024);
            Check(limitedOutput.Output.Contains("日志超过 4 MB，已截断", StringComparison.Ordinal), "Ordinary project output lost its existing limit.");
            Console.WriteLine("Session build failure: real PDF, preserved source/comments/old PDF, concise status, lazy selectable/copyable log, full log reader and successful recompile passed.");
        }
        finally { host.Close(); }
    }
}
