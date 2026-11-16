using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAsyncLayoutAnchorAsync(MainWindow window)
    {
        var panel = (AiPanel)Field(window, "_aiPanel")!;
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Clear();
        var first = new AiTurn("第一个问题", string.Join("\n\n", Enumerable.Repeat("上一轮的普通文字，用于构造布局。", 18)) + "\n\n公式 $x$。\n\n![示例图片](missing-fixture.png)");
        var second = new AiTurn("第二个问题", string.Join("\n\n", Enumerable.Repeat("这一轮是当前可见的阅读内容。", 32)));
        book.Turns.Add(first); book.Turns.Add(second);
        Invoke(window, "OpenAiTab"); Invoke(panel, "RefreshTranscript"); window.UpdateLayout(); await Task.Delay(500); window.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        scroll.ScrollToEnd(); window.UpdateLayout(); await Task.Delay(300); window.UpdateLayout();
        var turns = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
        if (!turns.TryGetValue(first.Id, out var firstView) || !turns.TryGetValue(second.Id, out var secondView)) throw new Exception("Async layout fixture did not realize both turns");
        double Top() => secondView.TranslatePoint(new Point(), scroll).Y;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + Top() - scroll.ViewportHeight * .3); window.UpdateLayout();
        await Task.Delay(200); window.UpdateLayout();
        var formula = AnnotationVisuals<ChatFormula>(firstView).First();
        await formula.RenderTask; window.UpdateLayout();
        var formulaChanged = (Action)Field(formula, "_layoutChanged")!;
        var before = Top();
        typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, 200)), new Rect(0, 0, 30, 200), 150, "fixture"));
        formulaChanged(); formula.InvalidateMeasure();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await Task.Delay(100); window.UpdateLayout();
        if (Math.Abs(Top() - before) > 2) throw new Exception($"Late formula moved visible content: {before:F1} -> {Top():F1}");
        var picture = AnnotationVisuals<MarkdownPicture>(firstView).First();
        var pictureChanged = (Action)Field(picture, "_changed")!;
        before = Top();
        picture.Child = new Border { Width = 80, Height = 260 };
        pictureChanged();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await Task.Delay(100); window.UpdateLayout();
        if (Math.Abs(Top() - before) > 2) throw new Exception($"Late image moved visible content: {before:F1} -> {Top():F1}");

        var beforeWheelTop = Top();
        var beforeWheelOffset = scroll.VerticalOffset;
        typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, 300)), new Rect(0, 0, 30, 300), 220, "fixture"));
        formulaChanged(); formula.InvalidateMeasure();
        if (!(bool)Field(panel, "_layoutAnchorPending")!) throw new Exception("Late formula did not schedule a temporary viewport anchor");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        scroll.RaiseEvent(wheel);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await Task.Delay(100); window.UpdateLayout();
        var wheelStep = SystemParameters.WheelScrollLines < 0 ? scroll.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
        var expectedTop = beforeWheelTop + Math.Min(beforeWheelOffset, wheelStep);
        if (!wheel.Handled || Math.Abs(Top() - expectedTop) > 2)
            throw new Exception($"Pending layout lost the user wheel movement: expected {expectedTop:F1}, actual {Top():F1}");
        async Task PrepareInputInterruptionAsync(int height)
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + Top() - scroll.ViewportHeight * .3); window.UpdateLayout();
            await Task.Delay(100); window.UpdateLayout();
            typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, height)), new Rect(0, 0, 30, height), height * .7, "fixture"));
            formulaChanged(); formula.InvalidateMeasure();
            if (!(bool)Field(panel, "_layoutAnchorPending")!) throw new Exception("Async layout anchor was not prepared");
        }
        await PrepareInputInterruptionAsync(360);
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.PageUp) { RoutedEvent = UIElement.PreviewKeyDownEvent };
        scroll.RaiseEvent(key);
        if ((bool)Field(panel, "_layoutAnchorPending")!) throw new Exception("Keyboard navigation did not cancel layout compensation");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await PrepareInputInterruptionAsync(420);
        var scrollbar = AnnotationVisuals<System.Windows.Controls.Primitives.ScrollBar>(scroll).First(bar => bar.Orientation == Orientation.Vertical);
        scrollbar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        if ((bool)Field(panel, "_layoutAnchorPending")!) throw new Exception("Scrollbar interaction did not cancel layout compensation");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var nested = new ScrollViewer { Width = 500, Height = 180, HorizontalAlignment = HorizontalAlignment.Center, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 500, Height = 500, Background = Brushes.Transparent } };
        ((Border)nested.Content).Width = 900;
        secondView.Children.Insert(1, nested); window.UpdateLayout();
        nested.ScrollToVerticalOffset(150); nested.ScrollToHorizontalOffset(150); window.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + nested.TranslatePoint(new Point(0, nested.ActualHeight / 2), scroll).Y - scroll.ViewportHeight / 2);
        window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, 450)), new Rect(0, 0, 30, 450), 315, "fixture"));
        var nestedTop = Top();
        formulaChanged(); formula.InvalidateMeasure();
        var innerAnchor = Field(panel, "_layoutAnchor") ?? throw new Exception("No anchor before inner wheel");
        if (!nested.IsAncestorOf((FrameworkElement)Field(innerAnchor, "Owner")!)) throw new Exception("Viewport anchor is not within the inner activity");
        var nestedBefore = nested.VerticalOffset;
        nested.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        if (nested.VerticalOffset >= nestedBefore - .1) throw new Exception("Pending layout undid an inner activity's vertical wheel");
        if (Math.Abs(Top() - nestedTop) > 2) throw new Exception($"Inner activity wheel moved outer conversation: {nestedTop:F1} -> {Top():F1}");
        nested.ScrollToVerticalOffset(200); window.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + nested.TranslatePoint(new Point(0, nested.ActualHeight / 2), scroll).Y - scroll.ViewportHeight / 2);
        window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, 455)), new Rect(0, 0, 30, 455), 318, "fixture"));
        nestedTop = Top(); nestedBefore = nested.VerticalOffset;
        formulaChanged(); formula.InvalidateMeasure();
        innerAnchor = Field(panel, "_layoutAnchor") ?? throw new Exception("No anchor before consecutive inner wheels");
        if (!nested.IsAncestorOf((FrameworkElement)Field(innerAnchor, "Owner")!)) throw new Exception("Viewport anchor is not within the inner activity");
        var firstInnerWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        var secondInnerWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        nested.RaiseEvent(firstInnerWheel); nested.RaiseEvent(secondInnerWheel);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await Task.Delay(100); window.UpdateLayout();
        var innerStep = SystemParameters.WheelScrollLines < 0 ? nested.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
        var expectedInner = Math.Max(0, nestedBefore - innerStep * 2);
        if (!firstInnerWheel.Handled || !secondInnerWheel.Handled || Math.Abs(nested.VerticalOffset - expectedInner) > 2)
            throw new Exception($"Consecutive inner wheels lost movement: expected {expectedInner:F1}, actual {nested.VerticalOffset:F1}");
        if (Math.Abs(Top() - nestedTop) > 2) throw new Exception($"Consecutive inner wheels moved outer conversation: {nestedTop:F1} -> {Top():F1}");
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + nested.TranslatePoint(new Point(0, nested.ActualHeight / 2), scroll).Y - scroll.ViewportHeight / 2);
        window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, 460)), new Rect(0, 0, 30, 460), 322, "fixture"));
        nestedTop = Top(); formulaChanged(); formula.InvalidateMeasure();
        innerAnchor = Field(panel, "_layoutAnchor") ?? throw new Exception("No anchor before inner horizontal wheel");
        if (!nested.IsAncestorOf((FrameworkElement)Field(innerAnchor, "Owner")!)) throw new Exception("Viewport anchor is not within the inner activity");
        var horizontalBefore = nested.HorizontalOffset;
        var shiftWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = nested };
        panel.RouteMessagesWheel(shiftWheel, ModifierKeys.Shift);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        if (!shiftWheel.Handled || nested.HorizontalOffset >= horizontalBefore - .1) throw new Exception("Pending layout undid an inner horizontal wheel");
        if (Math.Abs(Top() - nestedTop) > 2) throw new Exception($"Inner horizontal wheel moved outer conversation: {nestedTop:F1} -> {Top():F1}");
        nested.ScrollToVerticalOffset(200); window.UpdateLayout();
        var normalBefore = nested.VerticalOffset;
        for (var i = 0; i < 2; i++)
        {
            nested.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        }
        if (Math.Abs(nested.VerticalOffset - Math.Max(0, normalBefore - innerStep * 2)) > 2)
            throw new Exception("Ordinary inner wheels changed behavior without a pending layout");
        await PrepareInputInterruptionAsync(470);
        var burstTop = Top(); var burstOffset = scroll.VerticalOffset;
        var burstFirst = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        var burstSecond = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        scroll.RaiseEvent(burstFirst); scroll.RaiseEvent(burstSecond);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await Task.Delay(100); window.UpdateLayout();
        var burstExpected = burstTop + Math.Min(burstOffset, wheelStep * 2);
        if (!burstFirst.Handled || !burstSecond.Handled || Math.Abs(Top() - burstExpected) > 2)
            throw new Exception($"Rapid consecutive wheels lost movement during pending layout: expected {burstExpected:F1}, actual {Top():F1}");
        await PrepareInputInterruptionAsync(480);
        Invoke(panel, "RevealLiveTurn");
        if ((bool)Field(panel, "_layoutAnchorPending")!) throw new Exception("Programmatic send navigation did not cancel layout compensation");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await CheckSnapshotFormulaLayoutAsync(window, panel, book);
        await CheckSameTurnAsyncLayoutAsync(window, panel, book);
        await CheckTableAndGapAnchorAsync(window, panel, book);
        Console.WriteLine("PASS: late formula/image layout preserves content, wheel bursts and nested wheels survive, keyboard/scrollbar/send cancel correction");
    }

    private static async Task CheckTableAndGapAnchorAsync(MainWindow window, AiPanel panel, AiNotebook book)
    {
        book.Turns.Clear();
        var longCell = string.Concat(Enumerable.Repeat("abcdef", 20));
        var table = "| 首列 | 第二列 | 第三列 | 第四列 |\n|---|---|---|---|\n" +
            string.Join("\n", Enumerable.Range(0, 8).Select(i => $"| 第 {i} 行 | {longCell} | `{longCell}_{i}` | {longCell} |"));
        var turn = new AiTurn("问题", "前置公式 $x$。\n\n" + string.Join("\n\n", Enumerable.Repeat("之前的段落。", 20)) + "\n\n" + table + "\n\n" + string.Join("\n\n", Enumerable.Repeat("后续段落。", 30)));
        book.Turns.Add(turn); Invoke(panel, "RefreshTranscript"); window.UpdateLayout(); await Task.Delay(500); window.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        var turns = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
        var view = turns[turn.Id].Answers.Single();
        var formula = AnnotationVisuals<ChatFormula>(view).First(); await formula.RenderTask; window.UpdateLayout();
        var tableScroll = AnnotationVisuals<ScrollViewer>(view).First(s => s.ScrollableWidth > 0);
        double TableY() => tableScroll.TranslatePoint(new Point(), scroll).Y;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + TableY() + tableScroll.ActualHeight / 2 - scroll.ViewportHeight / 2);
        window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
        var before = TableY();
        typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, 180)), new Rect(0, 0, 30, 180), 130, "fixture"));
        ((Action)Field(formula, "_layoutChanged")!)(); formula.InvalidateMeasure();
        if (!(bool)Field(panel, "_layoutAnchorPending")!) throw new Exception("Table-centered viewport did not capture a temporary anchor");
        var anchor = Field(panel, "_layoutAnchor")!;
        if (Field(anchor, "Owner") is AiTurnView) throw new Exception("Table-centered viewport fell back to the whole turn");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        if (Math.Abs(TableY() - before) > 2) throw new Exception($"Late formula moved the visible table: {before:F1} -> {TableY():F1}");

        var block = view.Document.Blocks.OfType<System.Windows.Documents.BlockUIContainer>().First(item => ReferenceEquals(item.Child, tableScroll));
        var gap = new Border { Height = 220, IsHitTestVisible = false };
        view.Document.Blocks.InsertBefore(block, new System.Windows.Documents.BlockUIContainer(gap));
        window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        double GapY() => gap.TranslatePoint(new Point(), scroll).Y;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + GapY() + gap.ActualHeight / 2 - scroll.ViewportHeight / 2);
        window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, 240)), new Rect(0, 0, 30, 240), 180, "fixture"));
        ((Action)Field(formula, "_layoutChanged")!)(); formula.InvalidateMeasure();
        if (!(bool)Field(panel, "_layoutAnchorPending")!) throw new Exception("Gap-centered viewport did not capture a temporary anchor");
        anchor = Field(panel, "_layoutAnchor")!;
        if (Field(anchor, "Owner") is AiTurnView || Field(anchor, "Pointer") is null)
            throw new Exception("Gap-centered viewport did not use nearby selectable text");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Console.WriteLine("PASS: table and empty gap use a nearby visual/text anchor instead of the turn top");
    }

    private static async Task CheckSnapshotFormulaLayoutAsync(MainWindow window, AiPanel panel, AiNotebook book)
    {
        book.Turns.Clear();
        var first = new AiTurn("引用公式的问题", string.Join("\n\n", Enumerable.Repeat("上一轮的回答。", 15)));
        var second = new AiTurn("后续问题", string.Join("\n\n", Enumerable.Repeat("可见的后续正文。", 30)));
        book.Turns.Add(first); book.Turns.Add(second); Invoke(panel, "RefreshTranscript"); window.UpdateLayout(); await Task.Delay(500); window.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        scroll.ScrollToEnd(); window.UpdateLayout(); await Task.Delay(250); window.UpdateLayout();
        var turns = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
        var firstView = turns[first.Id]; var secondView = turns[second.Id];
        double Top() => secondView.TranslatePoint(new Point(), scroll).Y;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + Top() - scroll.ViewportHeight * .3); window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        var question = firstView.Questions[0];
        var paragraph = (System.Windows.Documents.Paragraph)question.Document.Blocks.FirstBlock!;
        var fallback = new System.Windows.Documents.Run("公式");
        var link = new System.Windows.Documents.Hyperlink(fallback); paragraph.Inlines.Add(link);
        window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        var before = Top();
        var beforeHeight = firstView.ActualHeight;
        var notified = false;
        RoutedEventHandler changed = (_, _) => notified = fallback.Parent == link;
        question.AddHandler(MarkdownView.AsyncLayoutChangingEvent, changed);
        var formula = new ChatFormula("x", true, asset: new ChatSvg(new RectangleGeometry(new Rect(0, 0, 20, 28)), new Rect(0, 0, 20, 28), 20, "fixture"));
        formula.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 16d);
        try
        {
            await (Task)Invoke(question, "CompleteSnapshotFormulaAsync", link, fallback, formula, "$x$", (int)Field(question, "_renderVersion")!)!;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally { question.RemoveHandler(MarkdownView.AsyncLayoutChangingEvent, changed); }
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        Console.WriteLine($"Snapshot reference formula fixture: first turn height {beforeHeight:F1} -> {firstView.ActualHeight:F1}, second top {before:F1} -> {Top():F1}, formula size {formula.DesiredSize.Width:F1}x{formula.DesiredSize.Height:F1}");
        if (!link.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Any()) throw new Exception("Snapshot formula was not substituted into the reference");
        if (!notified) throw new Exception("Late reference formula did not notify the conversation before changing layout");
        if (Math.Abs(Top() - before) > 2) throw new Exception($"Late snapshot formula moved visible content: {before:F1} -> {Top():F1}");
        Console.WriteLine("PASS: late reference formula preserves visible content");
    }

    private static async Task CheckSameTurnAsyncLayoutAsync(MainWindow window, AiPanel panel, AiNotebook book)
    {
        book.Turns.Clear();
        var turn = new AiTurn("问题", "公式 $y$。\n\n![示例图片](missing-fixture.png)\n\n" +
            string.Join("\n\n", Enumerable.Repeat("同一回答的正文段落，用于验证当前视口中的文字位置。", 80)));
        book.Turns.Add(turn); Invoke(panel, "RefreshTranscript"); window.UpdateLayout(); await Task.Delay(500); window.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        scroll.ScrollToVerticalOffset(400); window.UpdateLayout(); await Task.Delay(200); window.UpdateLayout();
        var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
        var view = views[turn.Id].Answers.Single();
        var formula = AnnotationVisuals<ChatFormula>(view).First(); await formula.RenderTask; window.UpdateLayout();
        var position = view.GetPositionFromPoint(scroll.TranslatePoint(new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2), view), true)
            ?? throw new Exception("Same-turn viewport did not contain selectable text");
        double TextY() => view.TranslatePoint(position.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward).TopLeft, scroll).Y;
        var before = TextY();
        typeof(ChatFormula).GetField("_svg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(formula, new ChatSvg(new RectangleGeometry(new Rect(0, 0, 30, 220)), new Rect(0, 0, 30, 220), 160, "fixture"));
        ((Action)Field(formula, "_layoutChanged")!)(); formula.InvalidateMeasure();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await Task.Delay(100); window.UpdateLayout();
        if (Math.Abs(TextY() - before) > 2) throw new Exception($"Late formula moved visible text within one turn: {before:F1} -> {TextY():F1}");
        var image = AnnotationVisuals<MarkdownPicture>(view).First();
        before = TextY(); image.Child = new Border { Width = 80, Height = 220 }; ((Action)Field(image, "_changed")!)();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        await Task.Delay(100); window.UpdateLayout();
        if (Math.Abs(TextY() - before) > 2) throw new Exception($"Late image moved visible text within one turn: {before:F1} -> {TextY():F1}");
        Console.WriteLine("PASS: late formula and image above the viewport preserve text within the same turn");
    }
}
