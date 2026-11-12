using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiFormOwnLineAsync(MainWindow owner, string output)
    {
        const string markdown = "前文保留\n{{text:soft}}\n中间保留\n{{textarea:softmulti}}\n结尾保留\n\n硬换行前  \n{{text:hard}}  \n硬换行后\n\n前缀 {{text:withbefore}}\n{{text:withafter}} 后缀\n**强调** {{text:withstrong}}\n\n| 独占单元格 | 同行有文字 |\n|---|---|\n| {{text:alone}} | 前缀 {{text:shared}} |";
        var values = new Dictionary<string, string>();
        var view = new MarkdownView { ChatMode = true };
        view.ConfigureForms(new AiFormContext("own-line-test", (type, id) => values.GetValueOrDefault(type + ":" + id, ""), (type, id, value) => values[type + ":" + id] = value, () => false));
        view.Markdown = markdown;
        var host = new Window { Owner = owner, Width = 820, Height = 700, Content = view }; host.Show();
        try
        {
            await view.RenderTask; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var fields = Descendants<TextBox>(view).Where(t => !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(t))).ToDictionary(t => System.Windows.Automation.AutomationProperties.GetName(t));
            if (fields.Count != 8) throw new Exception("A line-based form field was lost");
            if (fields["soft"].ActualWidth < 300 || fields["hard"].ActualWidth < 300 || fields["softmulti"].ActualWidth < 300 || !fields["softmulti"].AcceptsReturn || fields["softmulti"].MinLines != 3)
                throw new Exception("A form field alone on a soft or hard Markdown line did not fill its line");
            foreach (var id in new[] { "withbefore", "withafter", "withstrong" })
                if (fields[id].ActualWidth > 150 || fields[id].AcceptsReturn) throw new Exception("A field with same-line content incorrectly filled the line");
            var consecutive = new[] { "withbefore", "withafter", "withstrong" }.Select(id => fields[id]).ToArray();
            for (var i = 1; i < consecutive.Length; i++)
            {
                var previous = consecutive[i - 1].TranslatePoint(new Point(), view).Y + consecutive[i - 1].ActualHeight;
                var current = consecutive[i].TranslatePoint(new Point(), view).Y;
                if (previous > current + 1) throw new Exception("Form fields overlapped across lines containing emphasis");
            }
            if (fields["alone"].ActualWidth <= 150 || fields["shared"].ActualWidth > 150) throw new Exception("A single field did not fill its table cell, or a shared cell stretched incorrectly");
            var wideCell = fields["alone"].ActualWidth;
            host.Width = 480; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (fields["alone"].ActualWidth >= wideCell || fields["alone"].ActualWidth < 90 || fields["shared"].ActualWidth > 150)
                throw new Exception("Table input widths did not adapt to a narrower viewport");
            host.Width = 820; host.UpdateLayout();
            var copy = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            foreach (var expected in new[] { "前文保留", "中间保留", "结尾保留", "硬换行前", "硬换行后", "前缀", "后缀", "强调" })
                if (!copy.Contains(expected)) throw new Exception("A neighboring text fragment was swallowed: " + expected);
            Capture(host, Path.Combine(output, "ai-forms-own-line.png"));
            Console.WriteLine("PASS: standalone soft/hard Markdown lines, same-line text and preserved surrounding content.");
        }
        finally { host.Close(); }
    }
}
