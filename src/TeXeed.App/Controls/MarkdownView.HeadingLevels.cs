using Markdig.Syntax;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    internal static IReadOnlyList<int> HeadingLevels(string source)
    {
        var math = new ChatMath();
        var prepared = math.Prepare(source, Pipeline);
        var document = Markdig.Markdown.Parse(prepared, Pipeline);
        return document.Descendants<HeadingBlock>().Select(heading => heading.Level).ToArray();
    }
}
