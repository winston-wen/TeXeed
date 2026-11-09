using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAutoScrollAsync(MainWindow window, string output)
    {
        var preview = (PdfPreview)window.FindName("Preview");
        var pdf = Path.Combine(output, "autoscroll.pdf"); WriteThemePdf(pdf);
        await preview.LoadAsync(pdf); window.UpdateLayout(); await WaitForPreviewRenderedAsync(window);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var scroll = (ScrollViewer)Field(preview, "_scroll")!;
        var auto = (AutoScrollController)Field(preview, "_autoScroll")!;
        if (scroll.ScrollableHeight <= 5) throw new Exception("PDF fixture is not vertically scrollable");
        var anchor = new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2);
        var before = scroll.VerticalOffset;
        window.Activate();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var nativeCapture = auto.Start(anchor);
        if (nativeCapture)
        {
            if (!ReferenceEquals(Mouse.Captured, scroll)) throw new Exception("Native autoscroll did not capture the mouse");
            auto.Stop();
            if (ReferenceEquals(Mouse.Captured, scroll)) throw new Exception("Native autoscroll did not release the mouse");
        }
        else if (Environment.GetEnvironmentVariable("TEXEED_REQUIRE_POINTER_CAPTURE") == "1")
            throw new Exception("Native pointer capture is unavailable in this desktop session");
        auto.CapturePointer = _ => false;
        if (auto.Start(anchor) || auto.IsActive || (AdornerLayer.GetAdornerLayer(scroll)?.GetAdorners(scroll) ?? []).Any(a => a.GetType().Name.Contains("AutoScrollIndicator")))
            throw new Exception("A failed pointer capture left autoscroll or its marker active");
        auto.CapturePointer = _ => true;
        if (!auto.Start(anchor) || !auto.IsActive) throw new Exception("PDF middle-scroll marker did not start");
        window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var adorners = AdornerLayer.GetAdornerLayer(scroll)?.GetAdorners(scroll);
        if (adorners is null || !adorners.Any(a => a.GetType().Name.Contains("AutoScrollIndicator") && !a.IsHitTestVisible))
            throw new Exception("PDF marker was absent or intercepted input");
        CaptureWithAdorners(window, scroll, Path.Combine(output, "autoscroll-pdf.png"));
        auto.UpdatePointer(anchor + new Vector(0, 8)); auto.Advance(TimeSpan.FromMilliseconds(100)); window.UpdateLayout();
        if (Math.Abs(scroll.VerticalOffset - before) > .1) throw new Exception("PDF moved inside the middle-scroll dead zone");
        auto.UpdatePointer(anchor + new Vector(0, 100)); auto.Advance(TimeSpan.FromMilliseconds(100)); window.UpdateLayout();
        if (scroll.VerticalOffset <= before + 1 || !(bool)Field(window, "_previewUserOwnsView")!)
            throw new Exception("PDF autoscroll did not move or retain manual reading priority");
        var advanced = scroll.VerticalOffset;
        auto.UpdatePointer(anchor - new Vector(0, 100)); auto.Advance(TimeSpan.FromMilliseconds(100)); window.UpdateLayout();
        if (scroll.VerticalOffset >= advanced - 1) throw new Exception("PDF reverse-direction autoscroll did not move up");
        auto.Stop(); if (auto.IsActive) throw new Exception("PDF autoscroll failed to stop");
        if ((AdornerLayer.GetAdornerLayer(scroll)?.GetAdorners(scroll) ?? []).Any(a => a.GetType().Name.Contains("AutoScrollIndicator")))
            throw new Exception("PDF marker remained after stopping");
        foreach (var (edge, point) in new[]
        {
            ("top-left", new Point(2, 2)), ("top-right", new Point(scroll.ActualWidth - 2, 2)),
            ("bottom-left", new Point(2, scroll.ActualHeight - 2)), ("bottom-right", new Point(scroll.ActualWidth - 2, scroll.ActualHeight - 2))
        })
        {
            if (!auto.Start(point)) throw new Exception("Edge marker did not start: " + edge);
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var indicator = Field(auto, "_indicator")!;
            var display = (Point)indicator.GetType().GetProperty("DisplayPoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(indicator)!;
            if (display.X < 24.5 || display.Y < 24.5 || display.X > scroll.ActualWidth - 24.5 || display.Y > scroll.ActualHeight - 24.5)
                throw new Exception("Marker is clipped at the " + edge + " viewport edge");
            if ((Point)Field(auto, "_anchor")! != point) throw new Exception("Clamping the marker changed the scroll anchor at " + edge);
            var edgeBefore = scroll.VerticalOffset;
            auto.UpdatePointer(point + new Vector(0, 8)); auto.Advance(TimeSpan.FromMilliseconds(100)); window.UpdateLayout();
            if (Math.Abs(scroll.VerticalOffset - edgeBefore) > .1) throw new Exception("Marker clamping changed the dead zone at " + edge);
            CaptureWithAdorners(window, scroll, Path.Combine(output, "autoscroll-pdf-" + edge + ".png"));
            auto.Stop();
        }
        MouseButtonEventArgs Button(MouseButton button, RoutedEvent routed) => new(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = routed };
        var middle = Button(MouseButton.Middle, Mouse.PreviewMouseDownEvent); scroll.RaiseEvent(middle);
        if (!middle.Handled || !auto.IsActive) throw new Exception("Middle press did not start PDF autoscroll");
        scroll.RaiseEvent(Button(MouseButton.Middle, Mouse.PreviewMouseUpEvent));
        if (!auto.IsActive) throw new Exception("The initial middle-button release ended click-to-toggle autoscroll");
        var click = Button(MouseButton.Left, Mouse.PreviewMouseDownEvent); scroll.RaiseEvent(click);
        if (!click.Handled || auto.IsActive) throw new Exception("A subsequent click did not end PDF autoscroll");
        scroll.RaiseEvent(Button(MouseButton.Middle, Mouse.PreviewMouseDownEvent));
        var currentAnchor = (Point)Field(auto, "_anchor")!;
        auto.UpdatePointer(currentAnchor + new Vector(0, 50));
        scroll.RaiseEvent(Button(MouseButton.Middle, Mouse.PreviewMouseUpEvent));
        if (auto.IsActive) throw new Exception("Releasing a middle-button drag did not end PDF autoscroll");
        if (!auto.Start(anchor)) throw new Exception("PDF autoscroll did not restart");
        window.RaiseEvent(CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent));
        if (auto.IsActive) throw new Exception("Escape did not stop PDF autoscroll");
        if (!auto.Start(anchor)) throw new Exception("PDF autoscroll did not start for handled key test");
        var handledKey = CreateKey(window, Key.B, Keyboard.PreviewKeyDownEvent); handledKey.Handled = true;
        window.RaiseEvent(handledKey);
        if (auto.IsActive) throw new Exception("A key already handled by the window did not stop autoscroll");
        if (!auto.Start(anchor)) throw new Exception("PDF autoscroll did not start for wheel cancellation");
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        if (auto.IsActive) throw new Exception("Mouse wheel did not stop PDF autoscroll");
        scroll.ScrollToBottom(); window.UpdateLayout();
        if (!auto.Start(anchor)) throw new Exception("PDF autoscroll could not start at bottom");
        var timer = (DispatcherTimer)Field(auto, "_timer")!;
        auto.UpdatePointer(anchor + new Vector(0, 100));
        if (timer.IsEnabled) throw new Exception("Autoscroll timer kept running at the document boundary");
        auto.UpdatePointer(anchor - new Vector(0, 100));
        if (!timer.IsEnabled) throw new Exception("Autoscroll did not resume after reversing from the boundary");
        auto.Stop();

        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = window, Width = 850, Height = 700, Content = panel, Title = "Auto scroll · fixture" };
        host.Show();
        try
        {
            await panel.SetProjectAsync(Path.Combine(output, "chat.tex"));
            var book = (AiNotebook)Field(panel, "_book")!;
            for (var i = 0; i < 30; i++)
                book.Turns.Add(new AiTurn("Question " + i, string.Join("\n\n", Enumerable.Repeat("Readable synthetic text for scrolling.", 4))) { SentAt = DateTimeOffset.UnixEpoch });
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var reader = (ScrollViewer)panel.FindName("MessagesScroll");
            var chatAuto = (AutoScrollController)Field(panel, "_autoScroll")!;
            chatAuto.CapturePointer = _ => true;
            if (reader.ScrollableHeight <= 5) throw new Exception("AI fixture is not vertically scrollable");
            var composer = (TextBox)panel.FindName("Composer"); composer.Text = "unsent draft";
            var center = new Point(reader.ViewportWidth / 2, reader.ViewportHeight / 2);
            if (!chatAuto.Start(center)) throw new Exception("AI autoscroll did not start");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            CaptureWithAdorners(host, reader, Path.Combine(output, "autoscroll-ai.png"));
            var old = reader.VerticalOffset;
            chatAuto.UpdatePointer(center + new Vector(0, 110)); chatAuto.Advance(TimeSpan.FromMilliseconds(100)); host.UpdateLayout();
            if (reader.VerticalOffset <= old + 1 || composer.Text != "unsent draft")
                throw new Exception("AI autoscroll did not move or changed the unsent draft");
            chatAuto.Stop();
            var corner = new Point(2, 2);
            if (!chatAuto.Start(corner)) throw new Exception("AI edge marker did not start");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var edgeIndicator = Field(chatAuto, "_indicator")!;
            var edgeDisplay = (Point)edgeIndicator.GetType().GetProperty("DisplayPoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(edgeIndicator)!;
            if (edgeDisplay.X < 24.5 || edgeDisplay.Y < 24.5 || (Point)Field(chatAuto, "_anchor")! != corner)
                throw new Exception("AI edge marker was clipped or changed its input anchor");
            CaptureWithAdorners(host, reader, Path.Combine(output, "autoscroll-ai-top-left.png"));
            chatAuto.Stop();
            if ((bool)Invoke(chatAuto, "CanStart", composer)! || (bool)Invoke(chatAuto, "CanStart", new Button())!
                || (bool)Invoke(chatAuto, "CanStart", new ScrollBar())!)
                throw new Exception("Autoscroll can start from an editor or button");
            var readonlyText = new TextBox { IsReadOnly = true };
            if (!(bool)Invoke(chatAuto, "CanStart", readonlyText)!) throw new Exception("Read-only conversation text was excluded");
            var answer = Descendants<MarkdownView>(panel).First(v => v.IsVisible);
            var textMiddle = Button(MouseButton.Middle, Mouse.PreviewMouseDownEvent); answer.RaiseEvent(textMiddle);
            if (!textMiddle.Handled || !chatAuto.IsActive) throw new Exception("Middle press on AI reading text did not start autoscroll");
            answer.RaiseEvent(Button(MouseButton.Middle, Mouse.PreviewMouseUpEvent)); chatAuto.Stop();
            if (!chatAuto.Start(center)) throw new Exception("AI autoscroll could not restart");
            chatAuto.UpdatePointer(center + new Vector(0, 90));
            host.Hide(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (chatAuto.IsActive) throw new Exception("Hidden AI autoscroll was not stopped");
        }
        finally { host.Close(); }
        Console.WriteLine("PASS: PDF and AI autoscroll dead zone, movement, priority, marker, input isolation and lifecycle");
    }

    private static void CaptureWithAdorners(Window window, FrameworkElement target, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var layer = AdornerLayer.GetAdornerLayer(target) ?? throw new Exception("Autoscroll has no adorner layer");
        image.Render(layer);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
}
