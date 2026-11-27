using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckConversationFormulaWrapAsync(Window owner, string output, string tex)
    {
        var project = Path.Combine(output, "saved-ring.tex");
        var book = new AiNotebook();
        const string prefix = "对多项式舍入时，逐系数应用同一规则，结果属于 ";
        var target = new AiTurn("舍入后属于哪个环？", prefix + "$" + tex + "$。继续看下一句。");
        var tableTurn = new AiTurn("环的例子", $"| 名称 | 公式 |\n|---|---|\n| 多项式环 | 元素属于 ${tex}$。 |");
        for (var i = 0; i < 15; i++) book.Turns.Add(i == 7 ? target : i == 8 ? tableTurn : new AiTurn("前后相邻的轮次", "先看定义，再看计算。"));
        book.Draft = "未发送的草稿";
        AiStorage.Save(project, book);
        using var panel = new AiPanel();
        var store = await AiWorkspaceStore.LoadAsync(project);
        panel.Bind(store, store.Book.Active.Id);
        var host = new Window { Owner = owner, Content = panel, Width = 950, Height = 750, Left = -3000, Top = -3000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        async Task Settle()
        {
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
        }
        try
        {
            await Settle();
            if (!await panel.NavigateSameConversationTurnAsync(target.Id)) throw new Exception("Could not open the saved formula's turn");
            await Settle();
            var view = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == target.Id).Answers.Single();
            await view.RenderTask;
            await AwaitMathAsync(view);
            await Settle();
            var pieces = Descendants<ChatFormula>(view).Where(f => f.Source == tex).ToArray();
            if (pieces.Length < 2) throw new Exception("The reopened answer lacks the breakable inline equation");
            var left = pieces[0].TranslatePoint(new(), view).X;
            var desired = left + pieces[0].ActualWidth + pieces[1].ActualWidth * .5 + view.Padding.Right;
            host.Width -= view.ActualWidth - Math.Ceiling(desired);
            await Settle();
            await WaitUntilAsync(() => pieces[^1].TranslatePoint(new(), view).Y - pieces[0].TranslatePoint(new(), view).Y > 8,
                "Resizing a real conversation did not wrap its saved formula");
            var turns = Descendants<AiTurnView>(panel).Where(v => v.IsVisible).OrderBy(v => v.TranslatePoint(new(), panel).Y).ToArray();
            if (turns.Length < 2 || turns.Zip(turns.Skip(1)).Any(pair =>
                pair.First.TranslatePoint(new(0, pair.First.ActualHeight), panel).Y > pair.Second.TranslatePoint(new(), panel).Y + 1))
                throw new Exception("Adjacent turns overlap after reflowing a saved formula");
            pieces[^1].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            var menu = view.ContextMenu!;
            menu.IsOpen = true;
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "批注到当前会话"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            await Settle();
            var entry = panel.AnnotationEntries.Single();
            if (entry.AnswerAnchor is not { PreviewKind: "formula" } anchor || anchor.TurnId != target.Id ||
                anchor.PreviewSource != tex || anchor.Quote != "$" + tex + "$" || anchor.TextOffset != prefix.Length)
                throw new Exception("A continuation-line annotation lost its original equation, turn or text offset");
            await panel.SaveAsync();
            var disk = AiStorage.Load(project);
            if (disk.Draft != book.Draft || disk.Entries.Single().AnswerAnchor?.PreviewSource != tex ||
                disk.Turns.Single(t => t.Id == target.Id).Answer != target.Answer)
                throw new Exception("Saving a wrapped annotation altered the draft, source Markdown or equation");

            var paragraph = view.Document.Blocks.OfType<Paragraph>().Single();
            view.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
            var expected = view.CopyForClipboard(view.Selection.Start, view.Selection.End);
            if (!expected.Contains(prefix.TrimEnd()) || !expected.Contains("继续看下一句") ||
                !expected.Contains("$" + tex + "$")) throw new Exception("Missing mixed-selection fixture text");
            pieces = Descendants<ChatFormula>(view).Where(f => f.Source == tex).ToArray();
            pieces[^1].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            if (view.CopyForClipboard(view.Selection.Start, view.Selection.End) != expected)
                throw new Exception("Right-clicking a selected continuation discarded adjacent prose");
            menu.IsOpen = true;
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "批注到当前会话"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            await Settle();
            if (!panel.AnnotationEntries.Any(a => a.AnswerAnchor?.Quote == target.Answer))
                throw new Exception("The mixed selection did not reach the annotation as one complete quote");

            await panel.NavigateSameConversationTurnAsync(tableTurn.Id);
            await Settle();
            var tableAnswer = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == tableTurn.Id).Answers.Single();
            await tableAnswer.RenderTask;
            await AwaitMathAsync(tableAnswer);
            await Settle();
            var tablePiece = Descendants<ChatFormula>(tableAnswer).Last(f => f.Source == tex);
            tablePiece.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            if (tableAnswer.SelectedReferenceObject() is not ("formula", var selectedTex, _) || selectedTex != tex)
                throw new Exception("Right-clicking an unselected formula in a table selected the whole table");
        }
        finally { host.Content = null; host.Close(); }
    }
}
