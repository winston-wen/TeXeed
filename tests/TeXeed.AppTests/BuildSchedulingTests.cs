using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckBuildSchedulingAsync(MainWindow window, string root, string output)
    {
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        if (!File.Exists(engine)) throw new Exception("Build scheduling regression requires the local Tectonic engine");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var directory = Path.Combine(output, "build-scheduling"); Directory.CreateDirectory(directory);
        var main = Path.Combine(directory, "main.tex");
        const string content = "\\documentclass{article}\n\\begin{document}\nScheduling regression.\n\\end{document}\n";
        File.WriteAllText(main, content);
        var settingsField = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalSettings = (AppSettings)settingsField.GetValue(window)!;
        var timer = (DispatcherTimer)Field(window, "_liveTimer")!;
        var editor = (LatexEditor)window.FindName("Editor");
        var compiler = (CompileService)Field(window, "_compiler")!;
        var compileGate = (SemaphoreSlim)compiler.GetType().GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compiler)!;
        void Configure(bool live, int debounce = 400)
        {
            settingsField.SetValue(window, new AppSettings { Engine = TexEngine.Tectonic, EnginePath = engine, LivePreview = live,
                FollowMode = PreviewFollowMode.None, DebounceMs = debounce });
            timer.Stop(); timer.Interval = TimeSpan.FromMilliseconds(debounce);
        }
        Task Until(Func<bool> ready, string message) => WaitUntilAsync(ready, message);
        Task Started() => Until(() => Field(window, "_compileTask") is Task, "Scheduled build did not reach the real compiler");
        async Task LatestPdf()
        {
            await Until(() => !(bool)Field(window, "_building")! && (int)Field(window, "_pdfRevision")! == (int)Field(window, "_revision")!,
                "Newest requested revision was not saved and compiled");
        }
        async Task<Task> StartHeld()
        {
            await compileGate.WaitAsync();
            var build = CallAsync(window, "BuildAsync");
            try { await Started(); return build; }
            catch { compileGate.Release(); throw; }
        }
        try
        {
            Configure(false);
            await CallAsync(window, "OpenAsync", main);
            var initialGeneration = (int)Field(window, "_pdfGeneration")!;
            var first = await StartHeld();
            try
            {
                await CallAsync(window, "BuildAsync");
                await CallAsync(window, "BuildAsync");
            }
            finally { compileGate.Release(); }
            await first;
            if ((int)Field(window, "_pdfGeneration")! != initialGeneration + 1 || timer.IsEnabled || (bool)Field(window, "_buildAgain")!)
                throw new Exception("Repeated Ctrl+S on the active revision queued or produced a second build");
            await Task.Delay(500);
            if ((int)Field(window, "_pdfGeneration")! != initialGeneration + 1 || (bool)Field(window, "_building")!)
                throw new Exception("A duplicate build ran after the completed revision was already shown");
            Console.WriteLine("PASS: repeated explicit builds of the running revision compile exactly once, including with live preview disabled");

            // Hold the compiler's existing serialization gate, so its first revision
            // remains in flight while the newer source's debounce actually expires.
            Configure(true, 1000);
            first = await StartHeld();
            try
            {
                editor.Document.Insert(editor.Document.TextLength, "% expired debounce\n");
                await Task.Delay(1150);
                if (!(bool)Field(window, "_buildAgain")!) throw new Exception("Expired edit debounce did not queue the newer revision");
            }
            finally { compileGate.Release(); }
            await first;
            var continuation = Stopwatch.StartNew();
            await Started();
            if (continuation.ElapsedMilliseconds > 450)
                throw new Exception("A debounce that already expired waited again after the old build completed: " + continuation.ElapsedMilliseconds + "ms");
            await LatestPdf();
            Console.WriteLine("PASS: an expired source debounce starts the next revision immediately after the running engine finishes");

            Configure(true, 2000);
            first = await StartHeld();
            long due;
            try
            {
                editor.Document.Insert(editor.Document.TextLength, "% remaining debounce\n");
                due = (long)Field(window, "_liveBuildDueAt")!;
            }
            finally { compileGate.Release(); }
            await first;
            var remaining = due - Environment.TickCount64;
            if (remaining <= 100) throw new Exception("The remaining-debounce fixture did not finish its first build before the deadline");
            if (!timer.IsEnabled || Math.Abs(timer.Interval.TotalMilliseconds - remaining) > 130)
                throw new Exception($"Pending debounce was restarted instead of retaining its remaining time ({timer.Interval.TotalMilliseconds:0}ms vs {remaining}ms)");
            await Started();
            var startedAt = Environment.TickCount64;
            if (startedAt < due - 50 || startedAt > due + 400)
                throw new Exception("The next engine did not start at the original source-edit deadline");
            await LatestPdf();
            Console.WriteLine("PASS: when the old build finishes early, only the remaining edit debounce is retained");

            Configure(false, 2000);
            first = await StartHeld();
            try
            {
                editor.Document.Insert(editor.Document.TextLength, "% explicit latest revision\n");
                await CallAsync(window, "BuildAsync");
            }
            finally { compileGate.Release(); }
            await first;
            continuation.Restart(); await Started();
            if (continuation.ElapsedMilliseconds > 450) throw new Exception("An explicit new revision waited for automatic debounce with live preview off");
            await LatestPdf();
            if (!File.ReadAllText(main).Contains("explicit latest revision")) throw new Exception("Explicit pending build did not save the latest source");
            Console.WriteLine("PASS: an explicit newer revision saves and compiles promptly even when live preview is disabled");

            // Cancel invalidates an active revision. An immediate Ctrl+S must still
            // retry that same revision rather than being mistaken for a duplicate.
            first = await StartHeld();
            try
            {
                Invoke(window, "Cancel_Click", window, new RoutedEventArgs());
                await CallAsync(window, "BuildAsync");
            }
            finally { compileGate.Release(); }
            await first; await Started(); await LatestPdf();
            Console.WriteLine("PASS: cancel followed immediately by Ctrl+S retries the same revision");

            // A plain cancel drops queued revisions, including an explicit one.
            first = await StartHeld();
            try
            {
                editor.Document.Insert(editor.Document.TextLength, "% canceled pending revision\n");
                await CallAsync(window, "BuildAsync");
                Invoke(window, "Cancel_Click", window, new RoutedEventArgs());
            }
            finally { compileGate.Release(); }
            await first;
            if (timer.IsEnabled || (bool)Field(window, "_buildAgain")!) throw new Exception("Cancel retained a scheduled compilation");
            await CallAsync(window, "SaveAsync");
            await CallAsync(window, "BuildAsync");
            await LatestPdf();

            // Saving during an in-flight engine must still cancel before publishing
            // new source to disk; it must not leave a blocked save/compile cycle.
            first = await StartHeld();
            Task saving;
            try
            {
                editor.Document.Insert(editor.Document.TextLength, "% concurrent explicit save\n");
                saving = CallAsync(window, "SaveAsync");
                await saving;
            }
            finally { compileGate.Release(); }
            await first;
            if (!File.ReadAllText(main).Contains("concurrent explicit save") || (bool)Field(window, "_building")!)
                throw new Exception("Concurrent save did not finish cleanly after canceling the running compile");
            Console.WriteLine("PASS: cancel clears queued work and concurrent saves retain compiler cancellation/file-write ordering");

            editor.Text = "\\documentclass{article}\n\\begin{document}\n\\MissingSchedulerProbeCommand\n\\end{document}\n";
            var failed = CallAsync(window, "BuildAsync"); await Started(); var failedTask = Field(window, "_compileTask"); await failed;
            var failedRevision = (int)Field(window, "_revision")!;
            var retry = CallAsync(window, "BuildAsync"); await Started();
            if (ReferenceEquals(failedTask, Field(window, "_compileTask")) || (int)Field(window, "_revision")! != failedRevision)
                throw new Exception("An explicit retry of an unchanged failed revision did not launch a new compile");
            await retry;
            editor.Text = content; await CallAsync(window, "BuildAsync"); await LatestPdf();
            Console.WriteLine("PASS: unchanged failed revisions remain explicitly retryable and successful repair publishes the latest PDF");

            first = await StartHeld();
            var other = Path.Combine(directory, "other.tex"); File.WriteAllText(other, content);
            try
            {
                editor.Document.Insert(editor.Document.TextLength, "% discarded queued revision\n");
                await CallAsync(window, "BuildAsync");
                editor.Undo(); // Restore the saved buffer so project switching needs no confirmation.
                await CallAsync(window, "OpenAsync", other);
            }
            finally { compileGate.Release(); }
            await first; await Task.Delay(100);
            if (((TexProject)Field(window, "_project")!).MainPath != other || timer.IsEnabled || (bool)Field(window, "_building")!)
                throw new Exception("Switching projects leaked a pending build from the previous session");
            Console.WriteLine("PASS: project switching cancels both active and queued revisions from the old session");
        }
        finally
        {
            settingsField.SetValue(window, originalSettings);
            timer.Stop(); timer.Interval = TimeSpan.FromMilliseconds(originalSettings.DebounceMs);
        }
    }
}
