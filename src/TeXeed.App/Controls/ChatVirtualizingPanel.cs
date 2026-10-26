using System.Windows.Controls;

namespace TeXeed.App.Controls;

/// <summary>Native pixel virtualization with an explicit navigation entry point.</summary>
public sealed class ChatVirtualizingPanel : VirtualizingStackPanel
{
    internal void Reveal(int index) => BringIndexIntoView(index);
}
