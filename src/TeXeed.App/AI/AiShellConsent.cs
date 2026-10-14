using System.Text.RegularExpressions;

namespace TeXeed.App.AI;

internal static class AiShellConsent
{
    // Only these fixed, read-only Git grammar forms share a grant. No shell metacharacters,
    // git configuration, output files, external diff/textconv or arbitrary flags are accepted.
    internal static string Key(string command, string directory)
    {
        var text = command.Trim();
        var match = Regex.Match(text, @"\Agit (?<verb>log|status)(?: (?:--oneline|--all|--short|--name-status|--name-only|--stat|--no-pager|-\d+|--max-count=\d+))*\z", RegexOptions.CultureInvariant);
        return directory.ToUpperInvariant() + "\n" + (match.Success ? "git-read:" + match.Groups["verb"].Value : "exact:" + text);
    }
}
