using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Windows.Threading;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiMathPerformanceAsync(string output)
    {
        var sources = Enumerable.Range(1, 24).Select(i => @"\mathbb E[X_{" + i + @"}]=\frac{1}{" + i + @"}\sum_{k=1}^{" + i + @"}x_k^2").ToArray();
        var heartbeat = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => heartbeat++;
        timer.Start();
        var watch = Stopwatch.StartNew();
        var tasks = sources.Select(s => ChatTexRenderer.RenderAsync(s, true)).ToArray();
        await Task.WhenAny(tasks);
        var first = watch.Elapsed.TotalMilliseconds;
        var rendered = await Task.WhenAll(tasks);
        var all = watch.Elapsed.TotalMilliseconds;
        timer.Stop();
        if (all > 200 && heartbeat < 2) throw new Exception("Math compilation blocked the UI dispatcher");
        watch.Restart();
        var again = await Task.WhenAll(sources.Select(s => ChatTexRenderer.RenderAsync(s, true)));
        var memory = watch.Elapsed.TotalMilliseconds;
        if (!rendered.Zip(again).All(pair => ReferenceEquals(pair.First, pair.Second)))
            throw new Exception("Memory cache did not reuse rendered vectors");
        if (rendered.Any(svg => !File.Exists(svg.Path) || svg.Geometry.Bounds.IsEmpty))
            throw new Exception("Performance fixture did not generate valid SVG");
        var report = new { Formulas = sources.Length, FirstMs = first, AllMs = all, MemoryMs = memory, UiTicks = heartbeat };
        var json = JsonSerializer.Serialize(report);
        Console.WriteLine("Math performance: " + json);
        await File.AppendAllTextAsync(Path.Combine(output, "math-performance.jsonl"), json + Environment.NewLine);
    }

    private static async Task CheckAiMathBatchAsync(string output)
    {
        // Mixed valid/invalid TeX exercises recovery after a real engine error, not just validation.
        var sources = new[] { @"x_{707}+\frac{a}{b}", @"\frac{a}", @"\large x_{709}", @"x_{710}", @"\begin{pmatrix}1&2\\3&4\end{pmatrix}" };
        var tasks = sources.Select((s, i) => ChatTexRenderer.RenderAsync(s, i % 2 == 0)).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(45)); } catch (Exception) when (tasks.All(t => t.IsCompleted)) { }
        for (var i = 0; i < tasks.Length; i++)
            if (i == 1 ? !tasks[i].IsFaulted : !tasks[i].IsCompletedSuccessfully)
                throw new Exception("One malformed formula affected other batch results: " + i, tasks[i].Exception);
        if (!ReferenceEquals(tasks[1], ChatTexRenderer.RenderAsync(sources[1], false)))
            throw new Exception("Streaming rerender immediately retried failed TeX");

        var originalHome = Environment.GetEnvironmentVariable("TEXEED_HOME");
        var compareHome = Path.Combine(output, "single-formulas");
        try
        {
            Environment.SetEnvironmentVariable("TEXEED_HOME", compareHome);
            // Compare batch and separate single-job typography, including state after a size command.
            foreach (var i in new[] { 0, 2, 3, 4 })
            {
                var alone = await ChatTexRenderer.RenderAsync(sources[i], i % 2 == 0);
                var batched = await tasks[i];
                if (alone.Bounds != batched.Bounds || Math.Abs(alone.Baseline - batched.Baseline) > .0001 ||
                    alone.Geometry.ToString() != batched.Geometry.ToString())
                    throw new Exception("Batch changed equation geometry, order or style: " + i);
            }
            // Simulate both compiler slots occupied. A fresh process's disk hit must still complete.
            var diskHome = Path.Combine(output, "disk-only");
            var diskFolder = Path.Combine(diskHome, "ai", "math-svg");
            Directory.CreateDirectory(diskFolder);
            var seed = await tasks[0];
            File.Copy(seed.Path, Path.Combine(diskFolder, Path.GetFileName(seed.Path)), overwrite: true);
            Environment.SetEnvironmentVariable("TEXEED_HOME", diskHome);
            var gate = (SemaphoreSlim)typeof(ChatTexRenderer).GetField("Workers", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            await gate.WaitAsync(); await gate.WaitAsync();
            Task<ChatSvg>? queued = null;
            try
            {
                queued = ChatTexRenderer.RenderAsync("x_{99991}", true);
                var cached = await ChatTexRenderer.RenderAsync(sources[0], true).WaitAsync(TimeSpan.FromSeconds(3));
                if (queued.IsCompleted || cached.Bounds != seed.Bounds)
                    throw new Exception("Disk-hit bypass fixture did not exercise a blocked compile queue");
            }
            finally { gate.Release(2); }
            await queued!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { Environment.SetEnvironmentVariable("TEXEED_HOME", originalHome); }
        Console.WriteLine("Math batches: engine-error isolation, failure reuse, mixed inline/display styles, unchanged single-job vectors, and disk hits bypassing occupied compiler slots passed.");
    }
}
