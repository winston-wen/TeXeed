using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    // Use a pre-0.6.12 clarification so the same fixture can run against both releases.
    private static async Task MeasureTurnInputAsync(MainWindow owner, string output)
    {
        var project = Path.Combine(output, "input-perf.tex");
        File.WriteAllText(project, @"\documentclass{article}\begin{document}Input.\end{document}");
        var book = new AiNotebook();
        var original = new AiTurn("两种写法有什么区别？", "两种写法使用了不同的例子。");
        var clarification = AiForms.WithSubmissions(new AiTurn("请帮我改写第一轮。", "先选一种写法。\n\n{{radio:style:long:保留推导}}\n\n{{radio:style:short:只保留结论}}")
        {
            AnsweredAt = DateTimeOffset.UtcNow,
            RevisionRequest = new(original.Id, "task", AwaitingReply: true)
        }, []);
        book.Turns.AddRange([original, clarification]);
        AiStorage.Save(project, book);
        var store = await AiWorkspaceStore.LoadAsync(project);
        using var panel = new AiPanel();
        panel.Bind(store, book.Active.Id);
        var host = new Window { Owner = owner, Content = panel, Width = 960, Height = 740, Left = -3200, Top = -3200,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        try
        {
            await panel.NavigateSameConversationTurnAsync(clarification.Id);
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var view = Descendants<AiTurnView>(panel).Single(t => t.Turn.Id == clarification.Id);
            await Task.WhenAll(view.Answers.Select(a => a.RenderTask));
            var radios = Descendants<RadioButton>(view).ToArray();
            if (radios.Length != 2) throw new InvalidOperationException("Expected two rendered choices.");
            var samples = new List<object>();
            for (var index = 0; index < 18; index++)
            {
                var option = radios[index % radios.Length];
                var start = Stopwatch.GetTimestamp();
                var before = GC.GetAllocatedBytesForCurrentThread();
                option.IsChecked = true;
                var syncMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var idleMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var uiBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (option.IsChecked != true || AiForms.Draft(view.Turn).GetValueOrDefault("radio:style") != (option.Content?.ToString() == "保留推导" ? "long" : "short"))
                    throw new InvalidOperationException("Selected choice did not reach the turn draft.");
                if (index >= 4) samples.Add(new { syncMs, idleMs, uiBytes, controls = Descendants<Control>(view).Count(c => c.IsVisible) });
            }
            File.WriteAllText(Path.Combine(output, "input-perf.json"), JsonSerializer.Serialize(samples));
        }
        finally { host.Content = null; host.Close(); }
    }
}
