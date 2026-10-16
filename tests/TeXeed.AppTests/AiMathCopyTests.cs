using System.Windows;
using System.IO;
using System.Windows.Documents;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiMathCopyAsync(MainWindow window, string output)
    {
        var view = new MarkdownView();
        var host = new Window { Owner = window, Content = view, Width = 600, Height = 400 };
        host.Show();
        try
        {
            var tick = ((char)96).ToString();
            foreach (var (source, expected) in new[]
            {
                (@"甲\(x\)乙", "甲 $x$ 乙\n"),
                ("甲  $x$  乙", "甲 $x$ 乙\n"),
                (@"甲**\(x\)**乙", "甲 $x$ 乙\n"),
                ("$x$$y$", " $x$ $y$ \n"),
                (@"\[\frac{a}{b}\]", "$$\n\\frac{a}{b}$$\n"),
                ("$$\nx^2\n$$", "$$\nx^2$$\n"),
                (@"\begin{equation}x\tag{A}\label{eq}\end{equation}", "$$\nx\\tag{A}\\label{eq}$$\n"),
                (tick + @"\[x\]" + tick + " and " + tick + "$y$" + tick, "\\[x\\] and $y$\n"),
                (tick + tick + tick + "tex\n$x$\n\\[y\\]\n" + tick + tick + tick, "$x$\n\\[y\\]\n")
            })
            {
                view.Markdown = source; host.UpdateLayout();
                var actual = view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd);
                if (actual != expected) throw new Exception($"Copy whitespace mismatch: {source}\nExpected: {expected}\nActual: {actual}");
                var data = view.ClipboardData(view.Document.ContentStart, view.Document.ContentEnd);
                if (data.GetDataPresent(DataFormats.Rtf, false) || data.GetDataPresent(DataFormats.XamlPackage, false))
                    throw new Exception("Copy still prefers rich formula images");
            }
            view.Markdown = @"前\(x_i\)后";
            var canonical = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            var paragraph = (Paragraph)view.Document.Blocks.FirstBlock!;
            var formula = paragraph.Inlines.OfType<InlineUIContainer>().Single();
            view.Selection.Select(formula.ElementStart, formula.ElementEnd);
            string? copied = null;
            DataObjectCopyingEventHandler capture = (_, e) =>
            {
                copied = e.DataObject.GetData(DataFormats.UnicodeText) as string;
                if (e.DataObject.GetDataPresent(DataFormats.Rtf, false) || e.DataObject.GetDataPresent(DataFormats.XamlPackage, false))
                    throw new Exception("Copy command emitted rich formula images");
                e.CancelCommand();
            };
            DataObject.AddCopyingHandler(view, capture);
            ApplicationCommands.Copy.Execute(null, view);
            if (copied != " $x_i$ ") throw new Exception("Copy command lost the formula-only selection or exact spaces: " + copied);
            view.CopyToClipboard(view.Document.ContentStart, view.Document.ContentEnd);
            if (copied != "前 $x_i$ 后\n") throw new Exception("Whole-answer copy differs from selection copy");
            DataObject.RemoveCopyingHandler(view, capture);
            if (view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) != canonical)
                throw new Exception("Clipboard formatting changed stored annotation coordinates");
            view.Markdown = @"$$\begin{aligned}H(X)&=\frac12\times1+\frac14\times2+\frac14\times2\\&=1.5\text{ 位}.\end{aligned}$$"
                + "\n\n" + @"$$H(X)=\log_2 2^{256}=256\text{ 位}.$$";
            await AwaitMathAsync(view);
            Capture(host, Path.Combine(output, "chinese-math-svg.png"));
            Console.WriteLine("Math copy: exact inline spaces/block newlines, explicit labels/tags, untouched code, plain-text command payload, formula-only/full copy, stable annotation offsets, and both Chinese screenshot formulas passed.");
        }
        finally { host.Close(); }
    }
}
