using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task MeasureAiBuildFailurePerfAsync(Window owner, string output)
    {
        var source = "\\documentclass{article}\n\\begin{document}\nHello.\n\\end{document}\n";
        var doc = new AiSharedDocument(); doc.TryInitialize(source, "model");
        using var view = new AiSessionPdfView(Path.Combine(output, "perf-build-project.tex"), "perf-build", doc, new AppSettings());
        var host = new Window { Owner = owner, Width = 900, Height = 700, Content = view, ShowInTaskbar = false };
        var log = string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"line {i}: Undefined control sequence; check the source and retry."));
        var present = typeof(AiSessionPdfView).GetMethod("ShowBuildFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var status = (TextBlock)Field(view, "_status")!;
        var current = false;
        void Show()
        {
            current = !current; var text = log + (current ? "A" : "B");
            if (present is null) status.Text = "编译失败：" + text;
            else present.Invoke(view, ["编译失败", text]);
        }
        host.Show();
        try
        {
            async Task<(double Total, long Bytes)> Run(Action action)
            {
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var bytes = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew(); action();
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                return (timer.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes);
            }
            var samples = new List<object>();
            for (int i=0; i<9; i++)
            {
                var idle = await Run(() => { }); var sample = await Run(Show);
                if (i>=2) samples.Add(new { elapsedMs=sample.Total, allocatedBytes=sample.Bytes, idleMs=idle.Total, idleBytes=idle.Bytes,
                    adjustedMs=Math.Max(0,sample.Total-idle.Total), adjustedBytes=Math.Max(0,sample.Bytes-idle.Bytes) });
            }
            host.UpdateLayout();
            var collapsedControls = VisibleControls(host); var collapsedHeight = view.Preview.ActualHeight;
            var expandSamples = new List<object>(); var collapseSamples = new List<object>();
            int? expandedControls = null; double? expandedHeight = null;
            if (present is not null)
            {
                var expander = (Expander)Field(view, "_buildLog")!;
                for (var i = 0; i < 9; i++)
                {
                    var idleOpen = await Run(() => { }); var open = await Run(() => expander.IsExpanded = true);
                    if (i == 2) { host.UpdateLayout(); expandedControls = VisibleControls(host); expandedHeight = view.Preview.ActualHeight; }
                    var idleClose = await Run(() => { }); var close = await Run(() => expander.IsExpanded = false);
                    if (i >= 2)
                    {
                        expandSamples.Add(new { elapsedMs = open.Total, allocatedBytes = open.Bytes, idleMs = idleOpen.Total, idleBytes = idleOpen.Bytes, adjustedMs = Math.Max(0, open.Total - idleOpen.Total), adjustedBytes = Math.Max(0, open.Bytes - idleOpen.Bytes) });
                        collapseSamples.Add(new { elapsedMs = close.Total, allocatedBytes = close.Bytes, idleMs = idleClose.Total, idleBytes = idleClose.Bytes, adjustedMs = Math.Max(0, close.Total - idleClose.Total), adjustedBytes = Math.Max(0, close.Bytes - idleClose.Bytes) });
                    }
                }
            }
            var result = new { fixture="WPF 900x700; synthetic 1000-line compiler failure; alternating final character", version=present is null?"before":"after", samples,
                controls=collapsedControls, pdfHeight=collapsedHeight, logLength=log.Length, expandSamples, collapseSamples, expandedControls, expandedHeight };
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "build-failure-performance.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            Capture(host,Path.Combine(output,"build-failure-performance.png"));
            Console.WriteLine("Build failure performance saved to " + output);
        }
        finally { host.Close(); }
    }
}
