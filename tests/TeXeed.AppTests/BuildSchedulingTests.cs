using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
        var saveGate = (SemaphoreSlim)Field(window, "_saveGate")!;
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
        long LiveDeadline() => (long)Field(window, "_liveBuildDueAt")!;
        void CheckImmediateTimer()
        {
            // _compileTask appears only after the next build saves, reads dependencies and
            // checks its cache. The armed timer is the actual debounce decision.
            if (timer.Interval != TimeSpan.FromMilliseconds(1) || LiveDeadline() > Environment.TickCount64)
                throw new Exception($"Expected an already-due build; timer interval={timer.Interval.TotalMilliseconds}ms, due={LiveDeadline()}, now={Environment.TickCount64}");
        }
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
            long due;
            await saveGate.WaitAsync();
            try
            {
                try
                {
                    editor.Document.Insert(editor.Document.TextLength, "% expired debounce\n");
                    due = LiveDeadline();
                    await Until(() => (bool)Field(window, "_buildAgain")!, "Expired edit debounce did not queue the newer revision");
                }
                finally { compileGate.Release(); }
                await first;
                if (LiveDeadline() != due) throw new Exception("The expired source deadline was replaced with a new debounce");
                CheckImmediateTimer();
                // Keep the new build in its real save phase so compiler startup and
                // scheduler dispatch are independently observable, regardless of disk speed.
                await Until(() => (bool)Field(window, "_building")! && Field(window, "_compileTask") is null,
                    "The already-due timer did not dispatch the next build to the save gate");
            }
            finally { saveGate.Release(); }
            await LatestPdf();
            Console.WriteLine("PASS: an expired source debounce starts the next revision immediately after the running engine finishes");

            Configure(true, 2000);
            first = await StartHeld();
            // Edit when WPF receives the old engine's log. A slow Tectonic process
            // cannot consume the remaining-debounce fixture's entire editing interval.
            var log = (TextBox)window.FindName("LogText");
            log.Clear();
            due = 0;
            long editFinishedAt = 0;
            long dispatchedAt = 0;
            void ObserveTick(object? sender, EventArgs e) => dispatchedAt = Environment.TickCount64;
            void EditOnResult(object sender, TextChangedEventArgs e)
            {
                log.TextChanged -= EditOnResult;
                editor.Document.Insert(editor.Document.TextLength, "% remaining debounce\n");
                due = LiveDeadline();
                editFinishedAt = Environment.TickCount64;
            }
            log.TextChanged += EditOnResult;
            timer.Tick += ObserveTick;
            compileGate.Release();
            try
            {
                await first;
                var earliestInterval = Math.Max(1, due - Environment.TickCount64);
                var latestInterval = Math.Max(1, due - editFinishedAt);
                if (due == 0 || LiveDeadline() != due || timer.Interval.TotalMilliseconds < earliestInterval || timer.Interval.TotalMilliseconds > latestInterval)
                    throw new Exception($"Pending build lost the original deadline {due}: due={LiveDeadline()}, interval={timer.Interval.TotalMilliseconds}ms, expected {earliestInterval}..{latestInterval}ms");
                await LatestPdf();
                if (dispatchedAt < due) throw new Exception($"The next build ran before its source-edit deadline: dispatch={dispatchedAt}, due={due}");
            }
            finally { log.TextChanged -= EditOnResult; timer.Tick -= ObserveTick; }
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
            CheckImmediateTimer();
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
            var otherDirectory = Path.Combine(output, "build-scheduling-other");
            Directory.CreateDirectory(otherDirectory);
            var other = Path.Combine(otherDirectory, "other.tex"); File.WriteAllText(other, content);
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
