using System.IO;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task InspectOutlineClicksAsync(MainWindow window, string output)
    {
        var fixture = Path.Combine(output, "outline-click");
        Directory.CreateDirectory(fixture);
        var main = Path.Combine(fixture, "main.tex");
        var lines = Enumerable.Range(1, 180).Select(n => $"Row {n:000}: Click an outline heading to navigate here.").ToArray();
        lines[19] = "\\section{Alpha heading}";
        lines[29] = "\\subsection{First child}";
        lines[34] = "\\subsection{Second child}";
        lines[79] = "\\section{Beta heading}";
        lines[89] = "\\subsection{Third child}";
        File.WriteAllLines(main, lines);
        await CallAsync(window, "OpenAsync", main);
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        var editor = (LatexEditor)window.FindName("Editor");
        var clicks = 0;
        window.Title = "TeXeed outline click diagnostic — 0 clicks";
        outline.NavigateRequested += (_, source) =>
        {
            window.Title = $"TeXeed outline click diagnostic — {++clicks} clicks — line {editor.CurrentLocation?.Line}";
            Console.WriteLine($"CLICK {clicks}: target={source.Line}, caret={editor.CurrentLocation?.Line}");
        };
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        await closed.Task;
    }
}
