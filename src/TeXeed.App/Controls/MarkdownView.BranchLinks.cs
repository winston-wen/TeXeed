using System.Windows.Documents;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private readonly Dictionary<string, BlockUIContainer> _branchLinkBlocks = [];
    internal Dictionary<string, BlockUIContainer> BranchLinkBlocks => _branchLinkBlocks;
}
