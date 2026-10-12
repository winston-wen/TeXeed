using TeXeed.Core;

namespace TeXeed.Tests;

public static class TabWorkspaceTests
{
    public static void Run()
    {
        var tabs = new TabWorkspace(); tabs.Reset("main.tex"); tabs.Open("one.tex");
        Check.Equal(TabSide.Left, tabs.Owner("one.tex")!.Value);
        tabs.Move(TabWorkspace.PreviewKey, TabSide.Left);
        Check.True(!tabs.Has(TabSide.Right), "Moving the final tab closes its empty pane");
        tabs.Split(TabWorkspace.PreviewKey, TabSide.Left);
        Check.True(tabs.Tabs(TabSide.Left).SequenceEqual(new[] { TabWorkspace.PreviewKey }));
        Check.True(tabs.Tabs(TabSide.Right).SequenceEqual(new[] { "main.tex", "one.tex" }), "A left-edge split preserves the other tabs on the right");
        tabs.Move("one.tex", TabSide.Left, 0);
        tabs.Close(TabWorkspace.PreviewKey, TabCloseRange.This);
        Check.True(tabs.Owner(TabWorkspace.PreviewKey) is not null, "Preview cannot close");
        var closed = tabs.Close("one.tex", TabCloseRange.All); Check.Equal(1, closed.Count);
        tabs.Move("main.tex", TabSide.Left); Check.True(!tabs.Has(TabSide.Right));
        tabs.Split(TabWorkspace.PreviewKey, TabSide.Right);
        Check.True(tabs.Tabs(TabSide.Right).SequenceEqual(new[] { TabWorkspace.PreviewKey }));
        tabs.Move("main.tex", TabSide.Right); Check.True(!tabs.Has(TabSide.Left));
        tabs.Open("new.tex"); Check.Equal(TabSide.Left, tabs.Owner("new.tex")!.Value);
        tabs.Close("main.tex", TabCloseRange.All); Check.True(tabs.Owner(TabWorkspace.PreviewKey) is not null);
        Check.Equal(1, tabs.Tabs(TabSide.Right).Count);
        Console.WriteLine("PASS: source-left defaults, protected singleton preview, reorder/move, edge splitting, empty-pane collapse and scoped bulk close");
    }
}
