var project = Path.Combine(Path.GetTempPath(), "texeed-eval-offline", "main.tex");
var checks = 0;
void Check(bool success, string label)
{
    if (!success) throw new Exception(label);
    checks++;
}
bool Accept(string args, string[]? expected = null) => AiEvaluationChecks.MatchesExpectedEntry(project, args, expected ?? [], out _);
Check(Accept("{}"), "Missing entry_file must mean the actual default project.");
Check(Accept("{\"entry_file\":\"\"}"), "Empty entry_file must mean the default project.");
Check(Accept("{\"entry_file\":\"main.tex\"}"), "Explicit default entry must be accepted.");
Check(Accept("{\"entry_file\":\"./main.tex\"}"), "Equivalent relative paths must match.");
Check(!Accept("{\"entry_file\":\"other.tex\"}"), "An unrelated successful document must not pass.");
Check(!Accept("{\"entry_file\":\"wrapper.tex\"}"), "An unapproved wrapper must not pass by default.");
Check(Accept("{\"entry_file\":\"wrapper.tex\"}", ["wrapper.tex"]), "Explicitly expected wrapper should pass.");
Check(Accept("{\"entry_file\":\"second.tex\"}", ["first.tex", "second.tex"]), "Multiple expected entries should work.");
Check(!Accept("{}", ["wrapper.tex"]), "Compiling the default cannot satisfy a wrapper-specific test.");
Check(!Accept("{\"entry_file\":\"../outside.tex\"}", ["../outside.tex"]), "Out-of-project paths must never pass.");
Check(!Accept("{\"entry_file\":17}"), "Malformed entry_file must fail closed.");
Check(!Accept("not json"), "Malformed tool arguments must fail closed.");
Console.WriteLine($"PASS {checks} offline live-evaluation acceptance checks (no model or WPF).");
