using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiReadOnlyCommentListAsync(Window owner)
    {
        var selected = -1;
        var rows = Enumerable.Range(0, 60).Select(i => new AiReadOnlyCommentRow(
            $"作者{i}：评论{i} · 点击尝试近似定位", [new AiDocumentReply { Author = $"回复者{i}", Text = $"回复{i}" }],
            () => { selected = i; return Task.CompletedTask; })).ToArray();
        var view = new AiReadOnlyCommentList { MaxHeight = 220 };
        var host = new Window { Owner = owner, Width = 420, Height = 260, Content = view, ShowInTaskbar = false };
        try
        {
            view.SetRows(rows); host.Show(); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var list = Descendants<ListBox>(view).Single();
            int Realized() => Enumerable.Range(0, rows.Length).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);
            if (Realized() >= 30) throw new Exception("Read-only comments created too many offscreen rows.");
            var first = Descendants<Button>(view).First(b => b.Content is TextBlock t && t.Text.Contains("作者0："));
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (selected != 0 || !Descendants<TextBlock>(view).Any(t => t.Text == "回复者0：回复0")) throw new Exception("First comment or reply did not retain its author and navigation.");
            var scroll = Descendants<ScrollViewer>(list).First(); scroll.ScrollToEnd();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var last = Descendants<Button>(view).First(b => b.Content is TextBlock t && t.Text.Contains("作者59："));
            last.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (selected != 59 || !Descendants<TextBlock>(view).Any(t => t.Text == "回复者59：回复59")) throw new Exception("Scrolling reused the wrong comment or reply.");
            if (Realized() >= 30) throw new Exception("Scrolling retained too many offscreen rows.");
            view.Clear(); host.UpdateLayout();
            if (list.Items.Count != 0) throw new Exception("Closing read-only comments retained its rows.");
            Console.WriteLine("Virtual read-only comments: viewport creation, recycling, authors, replies and navigation passed.");
        }
        finally { host.Close(); }
    }
}
