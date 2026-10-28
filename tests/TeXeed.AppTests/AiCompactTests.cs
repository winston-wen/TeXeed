using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using ICSharpCode.AvalonEdit;

internal static partial class Program
{
    private static async Task CheckAiCompactAsync(MainWindow owner, string output)
    {
        var checks = 0; void Check(bool ok, string error) { checks++; if (!ok) throw new Exception(error); }
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Width = 620, Height = 850, Content = panel, Title = "AI 编辑栏与过程记录验证" };
        host.Show(); await panel.SetProjectAsync(Path.Combine(output, "compact.tex"));
        var input = (TextBox)panel.FindName("Composer"); var numbers = (TextBoxLineNumbers)panel.FindName("ComposerLines");
        Rect[] GlyphBounds(FrameworkElement element)
        {
            var bounds = new List<Rect>();
            foreach (var visual in Descendants<Visual>(element).Prepend(element))
            {
                void Visit(Drawing drawing, Matrix transform)
                {
                    if (drawing is DrawingGroup group)
                    {
                        var next = group.Transform?.Value ?? Matrix.Identity; next.Append(transform);
                        foreach (var child in group.Children) Visit(child, next);
                    }
                    else if (drawing is GlyphRunDrawing glyph)
                    {
                        var ink = glyph.GlyphRun.ComputeInkBoundingBox(); if (ink.IsEmpty) return;
                        ink.Offset((Vector)glyph.GlyphRun.BaselineOrigin);
                        bounds.Add(visual.TransformToAncestor(panel).TransformBounds(new MatrixTransform(transform).TransformBounds(ink)));
                    }
                }
                if (VisualTreeHelper.GetDrawing(visual) is { } drawing) Visit(drawing, Matrix.Identity);
            }
            return bounds.Where(rect => !rect.IsEmpty && rect.Height > 0).ToArray();
        }
        await Task.Delay(80); host.UpdateLayout();
        Check(Math.Abs(input.ActualHeight - 81) < .1, "Composer does not default to three 20px lines plus padding");
        Check(input.Text.Length == 0 && numbers.PaintedNumbers.SequenceEqual(new[] { 1, 2, 3 }), "Default three line numbers changed the empty draft or are missing");
        input.Text = "对齐"; input.ScrollToHome(); await Task.Delay(60); host.UpdateLayout();
        Check(numbers.PaintedNumbers.Contains(1), "First logical line was skipped at the text viewport edge");
        var firstCharacter = input.GetRectFromCharacterIndex(0);
        Console.WriteLine($"Composer first character: {input.TranslatePoint(firstCharacter.TopLeft, panel)}; row height: {firstCharacter.Height}");
        var firstNumber = GlyphBounds(numbers).OrderBy(rect => rect.Top).First();
        var numberCenter = new Point(firstNumber.X + firstNumber.Width / 2, firstNumber.Y + firstNumber.Height / 2);
        var corner = ((Border)panel.FindName("ComposerBorder")).TranslatePoint(new Point(), panel);
        Console.WriteLine($"Composer first number center from corner: {numberCenter.X - corner.X:F2}, {numberCenter.Y - corner.Y:F2}");
        Check(Math.Abs(numberCenter.X - corner.X - numberCenter.Y + corner.Y) < .5, "First line number is not on the upper-left corner bisector");
        Check(Math.Abs(input.TranslatePoint(firstCharacter.TopLeft, panel).X - 56) < .5, "Composer text does not share the reading origin");
        panel.Visibility = Visibility.Collapsed; host.UpdateLayout(); panel.Visibility = Visibility.Visible; host.UpdateLayout(); await Task.Delay(50);
        Check(Math.Abs(input.ActualHeight - 81) < .1, "Switching away from a conversation changed its editor height");
        Check(numbers.ActualWidth == 33 && ((System.Windows.Media.SolidColorBrush)numbers.BorderBrush).Color.A == 0, "Composer gutter lost its reading origin or gained a dividing rule");
        var border = (Border)panel.FindName("ComposerBorder"); var toolbar = (Border)panel.FindName("ComposerToolbar");
        var send = (Button)panel.FindName("SubmitButton"); var add = (Button)panel.FindName("AddButton");
        Check(border.CornerRadius.TopLeft == 24 && toolbar.BorderThickness.Top == 0 && send.ActualWidth == 24 && add.ActualHeight == 24, "Compact composer geometry diverged");
        var center = send.TranslatePoint(new Point(12, 12), border);
        Check(Math.Abs(border.ActualWidth - center.X - 24) < .5 && Math.Abs(border.ActualHeight - center.Y - 24) < .5, "Send button is not concentric with the lower-right corner");
        foreach (var name in new[] { "PermissionPicker", "ModelPicker", "EffortPicker" })
        {
            var picker = (ComboBox)panel.FindName(name); picker.ApplyTemplate();
            Check(!Descendants<System.Windows.Shapes.Path>(picker).Any(), "Composer picker still displays a dropdown chevron");
            picker.IsDropDownOpen = true; host.UpdateLayout(); Check(picker.IsDropDownOpen, "Arrow-free picker cannot open"); picker.IsDropDownOpen = false;
        }
        foreach (var name in new[] { "PermissionPicker", "EntriesButton", "ContextUsageText", "ModelPicker", "EffortPicker" })
        {
            var control = (FrameworkElement)panel.FindName(name); var glyphs = GlyphBounds(control);
            if (glyphs.Length == 0) continue;
            var ink = glyphs.Aggregate(Rect.Empty, (total, rect) => { total.Union(rect); return total; });
            Console.WriteLine($"Toolbar {name} ink middle: {ink.Top + ink.Height / 2:F2}");
        }
        input.Text = string.Concat(Enumerable.Repeat("行号不应给自动折行编号。", 24));
        input.CaretIndex = 0; input.ScrollToHome(); await Task.Delay(80); host.UpdateLayout();
        Check(input.LineCount > 2 && numbers.LogicalLineCount == 1, "Soft wraps became logical line numbers");
        Check(numbers.PaintedNumbers.SequenceEqual(new[] { 1 }), "Soft-wrapped continuation has a phantom line number");
        input.Text = "第一行\r\n第二行\n第三行\r第四行"; await Task.Delay(80);
        Check(numbers.LogicalLineCount == 4, "Mixed newline styles changed logical numbering");
        var grip = (Thumb)panel.FindName("ComposerResize");
        void Drag(double dy) { grip.RaiseEvent(new DragDeltaEventArgs(0, dy) { RoutedEvent = Thumb.DragDeltaEvent }); host.UpdateLayout(); }
        Drag(-80); Check(input.ActualHeight > 120, "Editor cannot be dragged taller");
        Drag(500); Check(Math.Abs(input.ActualHeight - 41) < .1, "Editor cannot shrink to one line");
        Drag(-10000); Check(input.ActualHeight <= panel.ActualHeight * .65 + 1, "Resizing consumed the whole conversation viewport");
        input.Height = 81; input.Text = string.Join('\n', Enumerable.Range(1, 80).Select(i => "逻辑行 " + i));
        input.CaretIndex = input.Text.Length; host.UpdateLayout(); input.ScrollToEnd(); await Task.Delay(80); host.UpdateLayout();
        Check(numbers.PaintedNumbers.Contains(80) && !numbers.PaintedNumbers.Contains(1), "Logical numbers did not follow editor scrolling: " + string.Join(',', numbers.PaintedNumbers) + "; offset=" + input.VerticalOffset);
        input.Text = "行号右对齐。此处自动折行仍属于第一行，默认显示三行，可拖动顶部边缘调整高度。"; input.ScrollToHome();
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout();
        Check(numbers.PaintedNumbers.Contains(1) && GlyphBounds(numbers).Length > 0, "Replacing scrolled text with a short draft lost the rendered first line number");
        var segment = new AiSegment { Kind = "activity", Text = "已读取源码 · main.tex", Details = "main.tex:1\n\\documentclass{article}" };
        var other = new AiSegment { Kind = "activity", Text = "编译成功，PDF 预览已更新。" };
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Add(new("检查公式排版", "完成。") { SentAt = DateTimeOffset.Now, Segments = [segment, other, new() { Kind = "final", Text = "已完成公式校对。" }] });
        Invoke(panel, "RefreshTranscript"); await Task.Delay(100); host.UpdateLayout();
        var rows = Descendants<AiActivityView>(panel).Where(v => v.IsVisible).ToArray();
        Check(rows.Length == 2, "Settled activities did not use the shared text disclosure");
        var row = rows.Single(v => ReferenceEquals(v.DataContext, segment)); var header = (TextBlock)row.Header;
        var link = header.Inlines.OfType<Hyperlink>().Single();
        string Label() => string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text));
        Check(header.Inlines.OfType<Run>().First().Text == segment.DisplayTime && Label() == segment.Text, "Process summary must show time first without a redundant fold label");
        Check(!Descendants<ToggleButton>(row).Any() && !Descendants<System.Windows.Shapes.Path>(row).Any(), "A separate disclosure button/arrow remains");
        Check(Math.Abs(rows[1].TranslatePoint(new Point(), panel).Y - rows[0].TranslatePoint(new Point(), panel).Y - 26.5) < .5, "Process summary lines do not share the expanded metadata rhythm");
        Check(row.Content is null, "A collapsed activity eagerly constructs its code editor");
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
        var code = Descendants<TextEditor>(row).Single();
        Check(row.IsExpanded && Label() == segment.Text && code.Text.Contains("documentclass"), "Clicking the text did not show saved details");
        Check(code.IsReadOnly && code.ShowLineNumbers && code.FontSize == 13 && code.FontWeight == FontWeights.Light && code.Document.LineCount == 2, "Tool details are not a light 13px read-only numbered code block");
        Check(Math.Abs(code.TextArea.TextView.DefaultLineHeight - 20) < .1, "Process detail lines do not use the 20px rhythm");
        Check(Math.Abs(code.ActualHeight - 56) < .1, $"Two-line detail height: actual={code.ActualHeight}, requested={code.Height}, minimum={code.MinHeight}");
        segment.Details = new string('W', 200) + "\n底行工具结果"; host.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        code.ScrollToVerticalOffset(code.ExtentHeight); host.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var bottom = code.TextArea.TextView.GetVisualPosition(new TextViewPosition(2, 1), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom).Y - code.VerticalOffset;
        Check(code.HorizontalOffset == 0 && code.ExtentWidth > code.ViewportWidth && bottom <= code.ViewportHeight + .1, $"Detail bottom={bottom}, viewport={code.ViewportHeight}, extent={code.ExtentHeight}, vertical={code.VerticalOffset}, horizontal={code.HorizontalOffset}, width={code.ExtentWidth}/{code.ViewportWidth}; canScroll={((System.Windows.Controls.Primitives.IScrollInfo)code.TextArea.TextView).CanVerticallyScroll}");
        segment.Details = "更新后的完整工具输出";
        Check(code.Text == segment.Details, "Streaming detail updates were lost");
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
        Check(!row.IsExpanded && Label() == segment.Text, "Clicking the text did not collapse its details");
        var plain = rows.Single(v => ReferenceEquals(v.DataContext, other));
        Check(!((TextBlock)plain.Header).Inlines.OfType<Hyperlink>().Any() && plain.Content is null, "Identical single-line details created an unused disclosure");
        other.Details = other.Text + "\n第二行完整结果";
        ((TextBlock)plain.Header).Inlines.OfType<Hyperlink>().Single().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
        Check(plain.IsExpanded && Descendants<TextEditor>(plain).Single().Text == other.Details, "Multiline details cannot be opened after a streaming update");
        other.Details = other.Text;
        Check(!plain.IsExpanded && !((TextBlock)plain.Header).Inlines.OfType<Hyperlink>().Any(), "An updated identical single-line detail kept its disclosure open");
        plain.IsExpanded = false;
        var live = (ObservableCollection<AiSegment>)Field(panel, "_liveSegments")!; live.Add(segment);
        ((StackPanel)panel.FindName("LiveContainer")).Visibility = Visibility.Visible;
        host.UpdateLayout(); await Task.Delay(80);
        Check(Descendants<AiActivityView>((ItemsControl)panel.FindName("LiveSegments")).Any(), "Live output still uses the old boxed activity template");
        ((StackPanel)panel.FindName("LiveContainer")).Visibility = Visibility.Collapsed;
        foreach (var theme in new[] { "ristretto", "github-light-default" })
        { TeXeed.App.Services.ThemeManager.Apply(theme); host.UpdateLayout(); await Task.Delay(50); Capture(host, Path.Combine(output, "ai-compact-" + theme + ".png")); }
        TeXeed.App.Services.ThemeManager.Apply("ristretto");
        book.Turns.Clear();
        for (var n = 1; n <= 120; n++) book.Turns.Add(new("合成轮次 " + n, "合成回答") { SentAt = DateTimeOffset.UnixEpoch, AnsweredAt = DateTimeOffset.UnixEpoch.AddSeconds(1), Collapsed = true });
        Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll"); scroll.ScrollToEnd(); host.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout();
        var last = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == book.Turns[119].Id);
        var marker = Descendants<TextBlock>(last).Single(t => t.Text == "[120]");
        var markerBounds = VisibleBounds(marker, panel);
        Check(markerBounds.Width >= marker.ActualWidth - .1 && markerBounds.Height >= marker.ActualHeight - .1, "The three-digit hanging turn number is clipped");
        Console.WriteLine($"Turn 120 marker bounds: width {marker.ActualWidth:F2}, visible {markerBounds.Width:F2}");
        Capture(host, Path.Combine(output, "ai-compact-turn-120.png"));
        book.Turns.Clear();
        var sent = new DateTimeOffset(2000, 1, 1, 16, 20, 0, TimeSpan.FromHours(8));
        book.Turns.Add(new("先帮我列一份纸飞机试飞记录的提纲。", "记录折法、试飞距离和环境条件。") { SentAt = sent, AnsweredAt = sent.AddSeconds(18), Collapsed = true });
        book.Turns.Add(new("A 组的三次距离是 4.2、4.8 和 4.5 米。帮我算平均值。", "平均距离为 4.50 米。") { SentAt = sent.AddMinutes(2), AnsweredAt = sent.AddMinutes(2).AddSeconds(24), Collapsed = true });
        var review = new AiTurn("把结果整理成试飞手记，保留计算和记录表。", "")
        {
            SentAt = sent.AddMinutes(4), AnsweredAt = sent.AddMinutes(4).AddSeconds(16),
            Segments = [new() { Kind = "activity", Text = "核对 3 条试飞记录", Details = "trial,distance_m\n1,4.2\n2,4.8\n3,4.5\n\nmean = 4.5 m", CreatedAt = sent.AddMinutes(4).AddSeconds(2) }, new() { Kind = "final", Text = "### 平均飞行距离为 4.50 米\n\n把三次记录相加，再除以试飞次数。\n\n$$\\bar d = \\frac{4.2+4.8+4.5}{3}=4.50\\,\\mathrm{m}.$$\n\n| 试飞 | 距离 | 与平均值之差 |\n| --- | --- | --- |\n| 第一次 | 4.2 m | −0.3 m |\n| 第二次 | 4.8 m | +0.3 m |\n| 第三次 | 4.5 m | 0.0 m |\n\n三次记录相差最多 0.6 米。比较另一种折法时，可以同时看平均距离和波动范围。" }]
        };
        book.Turns.Add(review);
        input.Text = ""; input.Height = 81;
        panel.GetType().GetField("_syncingChoices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(panel, true);
        var modelPicker = (ComboBox)panel.FindName("ModelPicker");
        modelPicker.ItemsSource = new[] { new AiModel("synthetic", "GPT-6.1-Sol") }; modelPicker.SelectedIndex = 0;
        var effortPicker = (ComboBox)panel.FindName("EffortPicker"); effortPicker.ItemsSource = new[] { new AiEffort("high", "高") }; effortPicker.SelectedIndex = 0;
        ((TextBlock)panel.FindName("ContextUsageText")).Text = "6.6%";
        host.Width = 827; host.Height = 940;
        Invoke(panel, "ResizeChoices"); Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
        await Task.Delay(150); scroll.ScrollToTop(); host.UpdateLayout();
        await Task.WhenAll(Descendants<MarkdownView>(panel).Select(view => view.RenderTask));
        await AwaitMathAsync(panel);
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout();
        var middles = new List<double>();
        Check(numbers.PaintedNumbers.SequenceEqual(new[] { 1, 2, 3 }) && GlyphBounds(numbers).Length == 3, "The final empty composer does not render all three line-number glyphs");
        foreach (var name in new[] { "PermissionPicker", "EntriesButton", "ContextUsageText", "ModelPicker", "EffortPicker" })
        {
            var ink = GlyphBounds((FrameworkElement)panel.FindName(name)).Aggregate(Rect.Empty, (total, rect) => { total.Union(rect); return total; });
            middles.Add(ink.Top + ink.Height / 2); Console.WriteLine($"Populated toolbar {name} ink middle: {middles[^1]:F2}");
        }
        Check(middles.Max() - middles.Min() < 1, "Visible toolbar glyph centers differ by a pixel or more");
        Capture(host, Path.Combine(output, "native-final-conversation.png"));
        host.Close();
        Console.WriteLine($"AI compact UI: {checks} checks passed (logical lines, scroll, resize, plain activity text, details and live parity).");
    }
}
