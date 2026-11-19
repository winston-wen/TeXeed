using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiControlViewportAsync(Window owner, string output)
    {
        static void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
        }

        var project = Path.Combine(output, "control-viewport-" + Guid.NewGuid().ToString("N") + ".tex");
        const string formulaSource = "x_1 + x_2 + x_3";
        // A wide cached SVG exercises the production equation scroller without a TeX subprocess.
        var mathCache = Path.Combine(AiStorage.Home, "math-svg");
        Directory.CreateDirectory(mathCache);
        File.WriteAllText(Path.Combine(mathCache, ChatTexRenderer.CacheFileName(formulaSource, true)),
            "<svg viewBox='0 0 1000 10' data-texeed-height='8'><path d='M 0 2 H 1000 V 7 H 0 Z'/></svg>");
        var text = "参见第三组公式 " + @"\eqref{wide-3}" + "。\n\n" + string.Join("\n\n", Enumerable.Range(1, 4).Select(section =>
            string.Join("\n\n", Enumerable.Range(0, 35).Select(i => $"合成测试第 {section} 组第 {i} 段。Testing a cross-control scroll boundary.")) +
            $"\n\n## 第 {section} 组\n\n$${formulaSource}\\label{{wide-{section}}}$$\n\n{{{{text:field{section}}}}}\n\n{{{{checkbox:choice{section}:选择 {section}}}}}\n\n" +
            $"| 项目 | 答案 |\n| --- | --- |\n| 表格 {section} | {{{{text:table{section}}}}} |"));
        var longTurn = AiForms.WithSubmission(new AiTurn("合成测试问题", text), null);
        var book = new AiNotebook();
        book.Turns.Add(longTurn);
        foreach (var n in Enumerable.Range(1, 30)) book.Turns.Add(new AiTurn($"后续问题 {n}", "合成的后续回答。"));
        AiStorage.Save(project, book);

        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window
        {
            Owner = owner, Width = 900, Height = 730, Content = panel, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -12000, Top = 20, ShowActivated = false, ShowInTaskbar = false
        };
        host.Show();
        try
        {
            async Task LayoutAsync()
            {
                host.UpdateLayout();
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
            }

            await panel.SetProjectAsync(project);
            await LayoutAsync();
            var turn = Descendants<AiTurnView>(panel).First(view => view.Turn.Id == longTurn.Id);
            var answer = turn.Answers.Single();
            await AwaitMathAsync(answer);
            await LayoutAsync();
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            var tables = Descendants<ScrollViewer>(answer).Where(view => view.Content is MarkdownView).ToArray();
            var equations = Descendants<ScrollViewer>(answer).Where(view => view.Content is ChatFormula).ToArray();
            Check(tables.Length == 4 && equations.Length == 4 && answer.ActualHeight > 6 * scroll.ViewportHeight,
                "The fixture did not render four tables and wide equations in a long answer.");
            double Top(FrameworkElement target) => target.TranslatePoint(new Point(), scroll).Y;
            var failures = new List<string>();

            foreach (var section in new[] { 1, 3 })
            {
                var table = tables[section - 1];
                var nested = (MarkdownView)table.Content;
                var field = Descendants<TextBox>(answer).Single(box => AutomationProperties.GetName(box) == $"field{section}");
                var choice = Descendants<CheckBox>(answer).Single(box => Equals(box.Content, $"选择 {section}"));
                var tableField = Descendants<TextBox>(nested).Single();
                var equation = equations[section - 1];
                var formulaScrollBar = Descendants<ScrollBar>(equation).Single(bar => bar.Orientation == Orientation.Horizontal);
                Check(equation.ScrollableWidth > 0 && formulaScrollBar.IsVisible, "The equation did not overflow horizontally.");
                var controls = new (string Name, FrameworkElement Element)[]
                {
                    ("table viewport", table), ("selectable table text", nested), ("table field", tableField),
                    ("standalone field", field), ("checkbox", choice), ("wide equation viewport", equation),
                    ("wide equation content", (ChatFormula)equation.Content), ("wide equation scrollbar", formulaScrollBar)
                };
                foreach (var (name, target) in controls)
                {
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + Top(table) - 260);
                    await LayoutAsync();
                    var targetY = Top(target);
                    var tableY = Top(table);
                    var offset = scroll.VerticalOffset;
                    Check(Math.Abs(tableY - 260) < 2 && targetY >= 0 && targetY + target.ActualHeight < scroll.ViewportHeight,
                        $"Section {section} {name} was not already visible at {targetY:F1}.");
                    var capture = section == 3 && ReferenceEquals(target, field) && Environment.GetCommandLineArgs().Contains("--capture-control-viewport");
                    if (capture) Capture(host, Path.Combine(output, "control-viewport-before.png"));

                    // WPF uses this routed request when an inline control moves focus.
                    // Raise it without moving the system pointer or activating the window.
                    var largestMove = 0d;
                    void TrackMove(object sender, ScrollChangedEventArgs change)
                    {
                        if (ReferenceEquals(change.OriginalSource, scroll) && change.VerticalChange != 0)
                            largestMove = Math.Max(largestMove, table.IsLoaded ? Math.Abs(Top(table) - tableY) : double.PositiveInfinity);
                    }
                    scroll.ScrollChanged += TrackMove;
                    target.BringIntoView();
                    await LayoutAsync();
                    scroll.ScrollChanged -= TrackMove;
                    if (capture) Capture(host, Path.Combine(output, "control-viewport-after.png"));
                    if (largestMove >= 2 || Math.Abs(Top(table) - tableY) >= 2 || Math.Abs(Top(target) - targetY) >= 2)
                        failures.Add($"Section {section} {name} moved existing content: table Y {tableY:F1} -> {Top(table):F1}, " +
                            $"control Y {targetY:F1} -> {Top(target):F1}, offset {offset:F1} -> {scroll.VerticalOffset:F1}, " +
                            $"largest observed move {largestMove:F1} DIP.");
                }
            }
            Check(failures.Count == 0, string.Join(Environment.NewLine, failures));

            var beforeHorizontal = Top(tables[2]);
            Check(AiPanel.ScrollWheel(equations[2], -120, horizontal: true), "The wide equation did not accept horizontal scrolling.");
            await LayoutAsync();
            Check(equations[2].HorizontalOffset > 0 && Math.Abs(Top(tables[2]) - beforeHorizontal) < 2,
                "An intentional horizontal scroll moved the reading viewport.");

            var beforeWheel = scroll.VerticalOffset;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            tables[2].RaiseEvent(wheel);
            await LayoutAsync();
            Check(scroll.VerticalOffset < beforeWheel, "The viewport guard blocked an intentional wheel gesture.");

            scroll.ScrollToTop();
            await LayoutAsync();
            var equationLink = ((Paragraph)answer.Document.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>().Single();
            Check(Top(equations[2]) > scroll.ViewportHeight, "The equation navigation target was already visible.");
            equationLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            await LayoutAsync();
            Check(Top(equations[2]) >= -1 && Top(equations[2]) + equations[2].ActualHeight <= scroll.ViewportHeight + 1,
                $"The equation link did not reveal the actual formula: Y={Top(equations[2]):F1}.");

            var virtualHistory = Descendants<ChatVirtualizingPanel>(panel).Single();
            AiTurnView? VisibleTurn(int index) => Descendants<AiTurnView>(panel).FirstOrDefault(view =>
                view.Turn.Id == book.Turns[index].Id && Top(view) < scroll.ViewportHeight && Top(view) + view.ActualHeight > 0);
            virtualHistory.Reveal(18);
            await LayoutAsync();
            Check(VisibleTurn(17) is not null, "Intentional navigation did not reveal the target turn.");

            ((FrameworkElement)panel.FindName("MessageSearchPanel")).Visibility = Visibility.Visible;
            ((TextBox)panel.FindName("MessageSearchBox")).Text = "后续问题 25";
            await LayoutAsync();
            Check(VisibleTurn(25) is not null, "Searching the conversation did not reveal the matching turn.");
            virtualHistory.Reveal(9);
            await LayoutAsync();
            var readingTurn = VisibleTurn(8) ?? throw new Exception("The fixture did not navigate away from the search result.");
            var readingY = Top(readingTurn);
            ((AiNotebook)Field(panel, "_book")!).Turns.Add(new AiTurn("后续问题 31", "追加的合成回答。"));
            Invoke(panel, "RefreshTranscript");
            await LayoutAsync();
            Check(VisibleTurn(8) is { } readingAfter && Math.Abs(Top(readingAfter) - readingY) < 2,
                "Refreshing search results jumped to the match after the reader moved elsewhere.");
            Check(!host.IsActive, "The offscreen test unexpectedly activated its window.");
            Console.WriteLine("PASS: WPF visibility requests from tables, fields and wide equations retain the reading viewport; wheel, search, equation links and turn navigation still work.");
        }
        finally { host.Close(); }
    }
}
