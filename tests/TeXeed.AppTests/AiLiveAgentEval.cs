using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.Core;

// Explicit, opt-in paid evaluation. Never selected by an ordinary test run.
// The caller supplies a disposable project copy and a shared request ledger.
internal static class AiLiveAgentEval
{
    internal sealed record Options(string Provider, string Model, string Project, string Profile, string Output,
        string Ledger, string PromptFile, string CredentialsHome, TexEngine Engine, string EnginePath,
        int TimeoutSeconds, int MaxRequests, string? InstructionsFile, bool RequireCompile, bool ReadOnly,
        string[] MustRead, string[] MustChange, string[] MustPreserve, string[] MustUseTool, string[] OnlyChange, string[] ExpectedEntry);

    internal static Func<Options, HttpMessageHandler>? ProviderHandlerFactory { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run(string[] args)
    {
        Options options;
        try { options = ParseOptions(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        Directory.CreateDirectory(options.Output);
        Directory.CreateDirectory(options.Profile);
        Environment.SetEnvironmentVariable("TEXEED_HOME", options.Profile);
        File.WriteAllText(Path.Combine(options.Profile, "settings.json"), JsonSerializer.Serialize(new AppSettings
        { LivePreview = false, FollowMode = PreviewFollowMode.None, Engine = options.Engine, EnginePath = options.EnginePath }));
        if (options.Provider != "sol") new AiCredentials { ApiKey = "isolated-evaluation-provider", UseApiKey = true }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TeXeed;component/Theme.xaml", UriKind.Relative) });
        var window = new MainWindow { Width = 1200, Height = 820, Title = "TeXeed · isolated agent evaluation" };
        var exitCode = 1;
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(options.Output, "unhandled.txt"), e.Exception.ToString());
            e.Handled = true; app.Shutdown(1);
        };
        window.Loaded += async (_, _) =>
        {
            try { exitCode = await EvaluateAsync(window, options); }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                // Evaluation results have already recorded unsaved buffers. Clear the test
                // window's dirty flags so a failed run cannot hang on a close confirmation.
                if (Field(window, "_changes") is System.Collections.IDictionary changes) changes.Clear();
                app.Shutdown(exitCode);
            }
        };
        window.Show(); app.Run(); return exitCode;
    }

    private static async Task<int> EvaluateAsync(MainWindow window, Options options)
    {
        var root = Path.GetDirectoryName(options.Project)!;
        var before = SourceManifest(root);
        File.WriteAllText(Path.Combine(options.Output, "before.json"), JsonSerializer.Serialize(before, JsonOptions));
        await ((Task)Invoke(window, "OpenAsync", options.Project)!);
        Invoke(window, "OpenAiTab");
        var project = (TexProject)Field(window, "_project")!;
        if (!string.Equals(project.MainPath, options.Project, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The opened project differs from the explicitly supplied copied project.");
        var prompt = File.ReadAllText(options.PromptFile);
        var instructions = options.InstructionsFile is null ? null : File.ReadAllText(options.InstructionsFile);
        var buffer = new AiStreamBuffer();
        var calls = new List<ToolRecord>();
        var ioGate = new object();
        void Event(object value)
        {
            lock (ioGate) File.AppendAllText(Path.Combine(options.Output, "events.jsonl"), JsonSerializer.Serialize(value) + "\n");
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var nativeRunner = window.CreateAiToolRunner();
        async Task<string> Runner(string name, string arguments, CancellationToken token)
        {
            var started = DateTimeOffset.UtcNow;
            Event(new { type = "tool-start", time = started, name, arguments });
            try
            {
                var result = await nativeRunner(name, arguments, token);
                lock (calls) calls.Add(new ToolRecord(name.Split('.').Last(), arguments, result, started, true));
                Event(new { type = "tool-result", time = DateTimeOffset.UtcNow, name, result });
                return result;
            }
            catch (Exception ex)
            {
                lock (calls) calls.Add(new ToolRecord(name.Split('.').Last(), arguments, ex.Message, started, false));
                Event(new { type = "tool-error", time = DateTimeOffset.UtcNow, name, error = ex.Message });
                throw;
            }
        }
        var inner = ProviderHandlerFactory?.Invoke(options) ?? new HttpClientHandler();
        using var counter = new CountLedgerHandler(options, inner, Event);
        using var connection = new AiConnection(counter, options.Provider == "sol" ? options.CredentialsHome : null);
        string? failure = null;
        var completed = false;
        Event(new { type = "evaluation-start", time = DateTimeOffset.UtcNow, options.Provider, options.Model,
            promptSha256 = AiStorage.Hash(prompt), prompt, instructionsSha256 = AiStorage.Hash(instructions ?? AiResponses.AgentInstructions) });
        try
        {
            // Network parsing runs on the pool, while the real runner marshals all editor
            // operations to the Dispatcher, exactly as the application does.
            await Task.Run(() => new AiResponses(connection).RunAsync(options.Model,
                [new AiEntry { Question = prompt }], [], project.Files, _ => { }, _ => { }, timeout.Token,
                "low", Runner, evt =>
                {
                    buffer.Add(evt);
                    Event(new { type = "segment", time = DateTimeOffset.UtcNow, evt.Id, evt.Kind, evt.Text, evt.Append });
                }, instructions), timeout.Token);
            completed = true;
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? "Evaluation cancelled or timed out." : ex.Message;
            Event(new { type = "evaluation-error", time = DateTimeOffset.UtcNow, error = failure });
        }
        var after = SourceManifest(root);
        var changed = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(p => before.GetValueOrDefault(p)?.Sha256 != after.GetValueOrDefault(p)?.Sha256).Order().ToArray();
        var segments = buffer.Snapshot();
        var answer = string.Join("\n", segments.Where(s => s.Kind == "final").Select(s => s.Text));
        var checks = new Dictionary<string, bool> { ["terminal_response"] = completed && answer.Length > 0 };
        ToolRecord[] callSnapshot; lock (calls) callSnapshot = calls.ToArray();
        var lastCompile = callSnapshot.LastOrDefault(c => c.Name == "compile_project");
        var lastCompileIndex = Array.FindLastIndex(callSnapshot, c => c.Name == "compile_project");
        var lastEditIndex = Array.FindLastIndex(callSnapshot, c => c.Succeeded && (c.Name is "replace_source" or "create_source"));
        var compileIsCurrent = lastCompileIndex >= 0 && lastCompileIndex > lastEditIndex;
        var actualCompileEntry = "";
        var expectedCompileEntry = lastCompile is not null && AiEvaluationChecks.MatchesExpectedEntry(options.Project, lastCompile.Arguments, options.ExpectedEntry, out actualCompileEntry);
        if (options.RequireCompile)
        {
            checks["verified_compile"] = lastCompile is { Succeeded: true } && lastCompile.Result.StartsWith("编译成功", StringComparison.Ordinal) && compileIsCurrent && expectedCompileEntry;
            checks["compiled_after_final_edit"] = compileIsCurrent;
            checks["compiled_expected_entry"] = lastCompile is not null && expectedCompileEntry;
        }
        if (options.ReadOnly)
        {
            checks["read_only_sources"] = changed.Length == 0;
            checks["read_only_operations"] = callSnapshot.All(c => c.Name is not ("replace_source" or "create_source" or "save_project" or "compile_project"));
        }
        foreach (var path in options.MustRead)
            checks["read:" + path] = callSnapshot.Any(c => c.Succeeded && (c.Name is "read_source" or "read_file") && ArgumentPath(c.Arguments).Equals(Normalize(path), StringComparison.OrdinalIgnoreCase));
        foreach (var path in options.MustChange) checks["changed:" + path] = changed.Contains(Normalize(path), StringComparer.OrdinalIgnoreCase);
        foreach (var path in options.MustPreserve) checks["preserved:" + path] = before.ContainsKey(Normalize(path)) && !changed.Contains(Normalize(path), StringComparer.OrdinalIgnoreCase);
        foreach (var name in options.MustUseTool) checks["tool:" + name] = callSnapshot.Any(c => c.Name == name && c.Succeeded);
        if (options.OnlyChange.Length > 0) checks["only_expected_files_changed"] = changed.All(path => options.OnlyChange.Select(Normalize).Contains(path, StringComparer.OrdinalIgnoreCase));
        var dirty = Field(window, "_changes") as System.Collections.IDictionary;
        checks["no_unsaved_edits"] = dirty is null || dirty.Count == 0;
        var summary = new
        {
            options.Provider, options.Model, reasoningEffort = "low", options.Project, promptSha256 = AiStorage.Hash(prompt),
            completed, failure, seconds = watch.Elapsed.TotalSeconds, requests = counter.Requests,
            changedFiles = changed, checks, passed = checks.Values.All(v => v),
            answer, lastCompile = lastCompile?.Result, compileIsCurrent, actualCompileEntry, expectedCompileEntries = options.ExpectedEntry,
            calls = callSnapshot, segments,
            unsavedFiles = dirty?.Keys.Cast<object>().Select(x => x.ToString()).ToArray() ?? []
        };
        File.WriteAllText(Path.Combine(options.Output, "after.json"), JsonSerializer.Serialize(after, JsonOptions));
        File.WriteAllText(Path.Combine(options.Output, "result.json"), JsonSerializer.Serialize(summary, JsonOptions));
        File.WriteAllText(Path.Combine(options.Output, "answer.md"), answer);
        Console.WriteLine($"{(checks.Values.All(v => v) ? "PASS" : "FAIL")} live {options.Provider}: {counter.Requests} requests, {callSnapshot.Length} tools, {watch.Elapsed.TotalSeconds:F1}s");
        foreach (var check in checks) Console.WriteLine($"  {(check.Value ? "PASS" : "FAIL")} {check.Key}");
        return checks.Values.All(v => v) ? 0 : 1;
    }

    private sealed record ToolRecord(string Name, string Arguments, string Result, DateTimeOffset StartedAt, bool Succeeded);
    private sealed record SourceFingerprint(long Bytes, string Sha256);
    private static Dictionary<string, SourceFingerprint> SourceManifest(string root)
    {
        var manifest = new Dictionary<string, SourceFingerprint>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        var extensions = new HashSet<string>([".tex", ".sty", ".cls", ".bib", ".md", ".txt"], StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry); var name = Path.GetFileName(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || name.StartsWith('.')) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (name is not ("build" or "_build" or "node_modules")) pending.Push(entry);
                    continue;
                }
                if (!AiWorkspaceFiles.IsSource(entry) && !extensions.Contains(Path.GetExtension(entry))) continue;
                using var stream = File.OpenRead(entry);
                manifest[Normalize(Path.GetRelativePath(root, entry))] = new SourceFingerprint(stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
            }
        }
        return manifest;
    }

    private static Options ParseOptions(string[] args)
    {
        string Value(string name, string? fallback = null)
        {
            var index = Array.IndexOf(args, name);
            if (index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--")) return args[index + 1];
            return fallback ?? throw new ArgumentException("Missing required evaluation argument " + name);
        }
        string[] Values(string name) => args.Select((a, i) => (a, i)).Where(p => p.a == name)
            .Select(p => p.i + 1 < args.Length ? args[p.i + 1] : throw new ArgumentException("Missing value for " + name)).ToArray();
        var provider = Value("--provider", "sol").ToLowerInvariant();
        if (provider is not ("sol" or "deepseek")) throw new ArgumentException("Only sol and deepseek are authorized for evaluation.");
        var model = Value("--model", provider == "sol" ? "gpt-6.1-sol" : "deepseek-flash");
        if (provider == "sol" && model != "gpt-6.1-sol" || provider == "deepseek" && !model.StartsWith("deepseek-"))
            throw new ArgumentException("Evaluation model is outside the user's authorization.");
        var project = Path.GetFullPath(Value("--project"));
        var profile = Path.GetFullPath(Value("--profile"));
        var output = Path.GetFullPath(Value("--output"));
        var ledger = Path.GetFullPath(Value("--ledger"));
        var promptFile = Path.GetFullPath(Value("--prompt-file"));
        if (!File.Exists(project) || !File.Exists(promptFile)) throw new FileNotFoundException("Project and prompt file must exist.");
        if (new[] { "events.jsonl", "result.json", "answer.md" }.Any(name => File.Exists(Path.Combine(output, name))))
            throw new InvalidOperationException("Use a fresh evaluation output directory to avoid mixing runs or reusing stale results.");
        // Requiring this marker makes accidentally pointing at the user's source folder
        // fail before opening any file or sending any request. The copy owner creates it.
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(project)!, ".texeed-evaluation-copy")))
            throw new InvalidOperationException("Evaluation refuses a project without a .texeed-evaluation-copy marker beside its entry file.");
        var realProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed");
        if (string.Equals(profile.TrimEnd(Path.DirectorySeparatorChar), realProfile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The evaluation profile must not be the user's normal profile.");
        var timeout = int.Parse(Value("--timeout-seconds", "900"));
        var limit = int.Parse(Value("--max-requests", "40"));
        if (timeout is < 10 or > 7200 || limit is < 1 or > 200) throw new ArgumentException("Invalid timeout or per-case request cap.");
        return new Options(provider, model, project, profile, output, ledger, promptFile,
            Path.GetFullPath(Value("--credentials-home", AiStorage.Home)),
            Enum.Parse<TexEngine>(Value("--engine", "Tectonic"), true), Value("--engine-path", ""), timeout, limit,
            args.Contains("--instructions-file") ? Path.GetFullPath(Value("--instructions-file")) : null,
            args.Contains("--require-compile"), args.Contains("--read-only"), Values("--must-read"), Values("--must-change"), Values("--must-preserve"), Values("--must-use-tool"), Values("--only-change"), Values("--expected-entry"));
    }

    private sealed class CountLedgerHandler(Options options, HttpMessageHandler inner, Action<object> log) : DelegatingHandler(inner)
    {
        internal int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/responses") == true)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var model = body.RootElement.GetProperty("model").GetString();
                var effort = body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString();
                if (model != options.Model || effort != "low") throw new InvalidOperationException("Request model or reasoning effort escaped evaluation restrictions.");
                if (Requests >= options.MaxRequests) throw new InvalidOperationException("Per-case request budget exhausted.");
                var total = await CountAsync(token); Requests++;
                log(new { type = "request", time = DateTimeOffset.UtcNow, provider = options.Provider, model, effort, caseCount = Requests, total });
                Console.WriteLine($"Live {options.Provider} request {Requests}/{options.MaxRequests}; shared total {total}/{(options.Provider == "sol" ? 200 : 500)}");
            }
            // A failed or cancelled network attempt remains charged in the ledger. No
            // automatic transport retries are performed by this harness.
            return await base.SendAsync(request, token);
        }
        private async Task<int> CountAsync(CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.Ledger)!);
            var lease = options.Ledger + ".lease";
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            FileStream? gate = null;
            while (gate is null)
            {
                token.ThrowIfCancellationRequested();
                // CreateNew also interoperates with Node's fs.openSync(path, "wx").
                // Never remove a lease owned by another process, even after a timeout.
                try { gate = new FileStream(lease, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex)
                {
                    if (waiting.Elapsed >= TimeSpan.FromSeconds(15))
                        throw new IOException("Timed out after 15 seconds waiting for the shared evaluation budget lease.", ex);
                    await Task.Delay(100, token);
                }
            }
            try
            {
                var ledger = File.Exists(options.Ledger) ? JsonNode.Parse(File.ReadAllText(options.Ledger))!.AsObject() : new JsonObject();
                var count = ledger[options.Provider]?.GetValue<int>() ?? 0;
                if (count < 0) throw new InvalidOperationException("Invalid shared request ledger; refusing to reset it.");
                var hardCap = options.Provider == "sol" ? 200 : 500;
                if (count >= hardCap) throw new InvalidOperationException($"Authorized {options.Provider} request budget exhausted ({hardCap}).");
                ledger[options.Provider] = ++count;
                ledger["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
                var temporary = options.Ledger + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, ledger.ToJsonString(JsonOptions));
                File.Move(temporary, options.Ledger, true);
                return count;
            }
            finally
            {
                gate.Dispose();
                File.Delete(lease);
            }
        }
    }
    private static string ArgumentPath(string arguments)
    {
        try { using var json = JsonDocument.Parse(arguments); return Normalize(json.RootElement.TryGetProperty("file", out var file) ? file.GetString() ?? "" : json.RootElement.TryGetProperty("path", out var path) ? path.GetString() ?? "" : ""); }
        catch (JsonException) { return ""; }
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static object? Invoke(object instance, string name, params object?[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var values = method.GetParameters().Select((p, i) => i < args.Length ? args[i] : p.DefaultValue).ToArray();
        return method.Invoke(instance, values);
    }
}
