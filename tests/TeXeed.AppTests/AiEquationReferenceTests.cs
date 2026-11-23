using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    private static void DoubleClickEquation(Hyperlink link)
    {
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(down.ClickCount))!.SetValue(down, 2);
        link.Inlines.FirstInline!.RaiseEvent(down);
        if (!down.Handled) throw new Exception("The equation reference did not handle the second press.");
    }

    private static void ClickEquation(Hyperlink link)
    {
        foreach (var evt in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
        {
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = evt };
            link.Inlines.FirstInline!.RaiseEvent(press);
            if (!press.Handled) throw new Exception("Missing equation reference gesture: " + evt.Name);
        }
    }

    private static async Task SendEquationClicksAsync(Window host, MarkdownView view, Hyperlink link, int count)
    {
        var hwnd = new WindowInteropHelper(host).Handle;
        var first = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var last = link.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        var center = new Point((first.X + last.X) / 2, first.Top + first.Height / 2);
        var inWindow = view.TranslatePoint(center, host);
        if (host.InputHitTest(inWindow) is not Run run || run.Parent != link) throw new Exception("Mouse missed the equation reference");
        var device = PresentationSource.FromVisual(host)!.CompositionTarget.TransformToDevice.Transform(inWindow);
        int position = ((int)device.Y << 16) | ((int)device.X & 0xffff);
        // A private desktop cannot move the user's pointer. A captured move activates the
        // HWND's WPF input provider; all subsequent messages still undergo normal hit testing.
        SetCapture(hwnd);
        try
        {
            PostMessage(hwnd, 0x0200, 0, position);
            await Task.Delay(50);
            for (int i = 0; i < count; i++)
            {
                if (!PostMessage(hwnd, 0x0201, 1, position) || !PostMessage(hwnd, 0x0202, 0, position))
                    throw new Exception("Could not send native button messages to the test window");
                await Task.Delay(45);
            }
        }
        finally { ReleaseCapture(); }
    }

    private static async Task CheckEquationReferenceAsync(Window owner, string output, bool realTex, bool windowMouse)
    {
        const string single = @"z^x=\operatorname{Rec}(w',c_x)";
        const string multi = @"\begin{aligned}w_x&=(\boldsymbol b'_x)^{\mathsf T}(\boldsymbol v\bmod p)+h_1,\\w'&=\boldsymbol b^{\mathsf T}(\boldsymbol u\bmod p)+h_1.\end{aligned}";
        var fixtures = new[] { (Name: "single", Tex: single, Label: "(1)"), (Name: "aligned", Tex: multi, Label: "(17)") };
        var mathHome = Path.Combine(AiStorage.Home, "math-svg");
        Directory.CreateDirectory(mathHome);
        if (!realTex)
            foreach (var fixture in fixtures)
            {
                using var svg = typeof(Program).Assembly.GetManifestResourceStream("TeXeed.AppTests.Fixtures.eqref." + fixture.Name + ".svg")
                    ?? throw new Exception("Missing TeX fixture: " + fixture.Name);
                using var file = File.Create(Path.Combine(mathHome, ChatTexRenderer.CacheFileName(fixture.Tex, true)));
                svg.CopyTo(file);
            }

        var view = new MarkdownView { ChatMode = true, Margin = new Thickness(20) };
        var folds = new Dictionary<string, bool>();
        view.ConfigureFolding(folds, "test", () => { });
        var filler = string.Join("\n\n", Enumerable.Repeat("这里是连续的阅读正文。", 30));
        // Fifteen intermediate displays make the unlabelled aligned expression equation 17.
        var intervening = string.Join("\n\n", Enumerable.Repeat("$$" + single + "$$", 15));
        view.Markdown = @"公式\eqref{decode}、\eqref{eq-17}说明了接收方怎样计算。另有\eqref{missing}。" +
            "\n\n" + filler + "\n\n# 解码\n\n$$" + single + @"\label{decode}$$" +
            "\n\n" + filler + "\n\n" + intervening + "\n\n# 接收方\n\n## 中间量\n\n$$" + multi + "$$\n\n" + filler;
        var scroll = new ChatScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var host = new Window { Owner = owner, Content = scroll, Width = 720, Height = 530,
            Left = windowMouse ? 40 : -12000, Top = 30, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = windowMouse, ShowInTaskbar = false };

        host.Show();
        async Task Layout()
        {
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
        }
        try
        {
            await view.RenderTask; await AwaitMathAsync(view); await Layout();
            if (windowMouse) { host.Activate(); await Layout(); }
            var references = ((Paragraph)view.Document.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>().ToArray();
            var headings = view.Document.Blocks.OfType<Section>().ToArray();
            Hyperlink Toggle(Section section) => ((Paragraph)section.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>().Single();
            var inner = ((Section)headings.Last().Blocks.LastBlock!).Blocks.OfType<Section>().Single();
            Toggle(inner).RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Toggle(headings.Last()).RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            await Layout();
            if (folds.Count(p => p.Value) != 2) throw new Exception("Expected two folded ancestors");
            ScrollChangedEventArgs? priorScroll = null;
            scroll.ScrollChanged += (_, e) => { if (e.OriginalSource == scroll && e.VerticalChange != 0) priorScroll = e; };
            scroll.ScrollToVerticalOffset(60); await Layout();
            scroll.ScrollToTop(); await Layout();
            var delayed = priorScroll ?? throw new Exception("Missing scroll notification from the initial read");
            foreach (var fixture in fixtures)
            {
                scroll.ScrollToTop(); await Layout();
                var link = references.Single(l => new TextRange(l.ContentStart, l.ContentEnd).Text == fixture.Label);
                var before = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var foldsBefore = folds.Count(p => p.Value);
                var viewPosition = scroll.VerticalOffset;
                if (windowMouse) await SendEquationClicksAsync(host, view, link, 1);
                else ClickEquation(link);
                await Layout();
                if (Field(view, "_equationPreview") is not Popup { IsOpen: true, Child: FrameworkElement surface } popup)
                    throw new Exception("The single click did not open the equation preview: " + fixture.Name);
                var shown = Descendants<ChatFormula>(surface).Single(); await shown.RenderTask; await Layout();
                // A queued layout notification from the preceding scroll may arrive after this click.
                scroll.RaiseEvent(delayed); await Layout();
                if (!popup.IsOpen) throw new Exception("An earlier scroll notification dismissed the stationary reference");
                var after = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var contents = Descendants<ScrollViewer>(surface).Single();
                if (shown.Source != fixture.Tex || !File.Exists(shown.SvgPath) || shown.ActualHeight < (fixture.Name == "aligned" ? 32 : 12) ||
                    contents.ScrollableWidth > 0 || contents.ScrollableHeight > 0 ||
                    folds.Count(p => p.Value) != foldsBefore || Math.Abs(scroll.VerticalOffset - viewPosition) > .5 || Math.Abs(before.Y-after.Y) > .5)
                    throw new Exception($"Incorrect or clipped preview, changed folds or lost position for {fixture.Name}: {shown.ActualWidth}×{shown.ActualHeight}, Δscroll {scroll.VerticalOffset-viewPosition}");
                SaveLiteralVisual(surface, Path.Combine(output, "eqref-preview-" + fixture.Name + ".png"));
                if (windowMouse)
                {
                    await Task.Delay((int)GetDoubleClickTime() + 40);
                    await SendEquationClicksAsync(host, view, link, 2);
                }
                else DoubleClickEquation(link);
                await Layout();
                var target = Descendants<ChatFormula>(view).First(f => f.Source == fixture.Tex);
                var y = target.TranslatePoint(new Point(), scroll).Y;
                if (popup.IsOpen || y < -1 || y + target.ActualHeight > scroll.ViewportHeight + 1 || !target.IsVisible ||
                    folds.Count(p => p.Value) != (fixture.Name == "aligned" ? 0 : foldsBefore))
                    throw new Exception($"Double click did not reveal {fixture.Name}: y={y}, height={target.ActualHeight}, viewport={scroll.ViewportHeight}, open={popup.IsOpen}");
                Capture(host, Path.Combine(output, "eqref-target-" + fixture.Name + ".png"));
                Console.WriteLine($"{fixture.Name} {fixture.Label}: preview {shown.Source}, {shown.ActualHeight:F1} DIP; scroll Δ0; target y={y:F1}.");
            }
            scroll.ScrollToTop(); await Layout();
            references.Last().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); await Layout();
            if (Field(view, "_equationPreview") is not Popup { IsOpen: true, Child: FrameworkElement missing } p ||
                !Descendants<TextBlock>(missing).Any(t => t.Text == "未找到引用的公式") || scroll.VerticalOffset > .5)
                throw new Exception("Unresolved equation reference lost its reading position or feedback");
            host.RaiseEvent(CreateKey(host, Key.Escape, Keyboard.PreviewKeyDownEvent));
            if (p.IsOpen) throw new Exception("Escape did not dismiss the preview");
            references[0].RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent)); await Layout();
            if (Field(view, "_equationPreview") is not Popup { IsOpen: true } reopened) throw new Exception("Preview did not reopen");
            scroll.ScrollToVerticalOffset(80); await Layout();
            if (reopened.IsOpen) throw new Exception("Scrolling the anchor away did not dismiss the preview");
            scroll.ScrollToTop(); await Layout();
            var keyState = new byte[256];
            if (!GetKeyboardState(keyState)) throw new Exception("Could not read the test thread's keyboard state");
            var ctrl = (byte[])keyState.Clone();
            ctrl[0x11] = ctrl[0xA2] = 0x80; // VK_CONTROL, VK_LCONTROL
            try
            {
                if (!SetKeyboardState(ctrl)) throw new Exception("Could not hold Ctrl on the test thread");
                var enter = CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent);
                references[1].RaiseEvent(enter);
                if (!enter.Handled) throw new Exception("The equation reference did not handle Ctrl+Enter");
            }
            finally { SetKeyboardState(keyState); }
            await Layout();
            var aligned = Descendants<ChatFormula>(view).Single(f => f.Source == multi);
            var top = aligned.TranslatePoint(new Point(), scroll).Y;
            if (top < -1 || top + aligned.ActualHeight > scroll.ViewportHeight + 1 || Field(view, "_equationPreview") is Popup { IsOpen: true })
                throw new Exception("Ctrl+Enter did not navigate to the referenced equation");
            Console.WriteLine("PASS: single- and two-line equations preview in place and double-click the exact target, including folded parents and numbered aliases.");
        }
        finally { host.Close(); }
    }
}
