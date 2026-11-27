using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostUnicodeInput(nint window, uint message, nint character, nint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendUnicodeInput(nint window, uint message, nint character, nint flags);
    [DllImport("user32.dll")] private static extern nint GetCapture();

    private static async Task CheckAnnotationFocusAsync(MainWindow window, string output)
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var project = Path.Combine(output, "annotation-focus.tex");
        File.WriteAllText(project, @"\documentclass{article}\begin{document}draft test\end{document}");
        var book = new AiNotebook();
        var turn = new AiTurn("需要批注哪一处？", "我们选中这句话，写下批注。");
        book.Turns.Add(turn);
        book.Draft = "主输入框未提交的内容";
        AiStorage.Save(project, book);
        await CallAsync(window, "OpenAsync", project);
        var panel = (AiPanel)Field(window, "_aiPanel")!;
        await CallAsync(window, "OpenAiConversationAsync", panel.ConversationId);
        window.Activate();
        async Task Settle()
        {
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        await Settle();
        var layers = (Dictionary<MarkdownView, AiAnnotationLayer>)Field(panel, "_answerLayers")!;
        Popup popup = null!;
        var ready = false;
        var typeOnOpen = true;
        int openCount = 0, closeCount = 0;
        void CheckAtOpen(object? sender, EventArgs args)
        {
            openCount++;
            var visiblePopup = (Popup)sender!;
            var input = Descendants<TextBox>(visiblePopup.Child).Single();
            var text = input.Text;
            ready = input.IsVisible && ReferenceEquals(Keyboard.FocusedElement, input) && input.CaretIndex == text.Length;
            if (!typeOnOpen) return;
            // An IME may commit text as soon as Windows exposes the popup.
            // Deliver the character to the actual keyboard HWND, without refocusing or clicking the input.
            SendUnicodeInput(GetFocus(), 0x0102, '即', 1);
            ready &= input.Text == text + "即";
        }
        void Observe(Popup current)
        {
            if (ReferenceEquals(current, popup)) return;
            if (popup is not null) { popup.Opened -= CheckAtOpen; popup.Closed -= OnClosed; }
            popup = current;
            popup.Opened += CheckAtOpen;
            popup.Closed += OnClosed;
        }
        void OnClosed(object? sender, EventArgs args) => closeCount++;
        var composer = (TextBox)panel.FindName("Composer");
        var samples = new List<object>();
        var failures = new List<string>();

        async Task NativeClick(FrameworkElement target, Point at, bool right)
        {
            var source = (HwndSource)PresentationSource.FromVisual(target)!;
            var handle = source.Handle;
            Check(handle != 0, "Mouse input target has no live HWND.");
            var point = target.TranslatePoint(at, (UIElement)source.RootVisual);
            var coordinates = CaptionPoint(source.CompositionTarget.TransformToDevice.Transform(point));
            var keys = new byte[256];
            Check(GetKeyboardState(keys), "Could not inspect the isolated desktop keyboard.");
            var pressed = (byte[])keys.Clone();
            pressed[right ? 2 : 1] = 0x80;
            // WPF transfers capture from the answer to its context-menu HWND on right-up.
            // Sending input on a private desktop must preserve capture acquired by the menu.
            var acquired = GetCapture() == 0;
            if (acquired) SetCapture(handle);
            Check(GetCapture() == handle, $"Mouse capture belongs to another window before clicking {target.GetType().Name}.");
            try
            {
                Check(PostMessage(handle, 0x0200, 0, coordinates), "Mouse move was not queued.");
                await Settle();
                SetKeyboardState(pressed);
                Check(PostMessage(handle, right ? 0x0204u : 0x0201u, right ? 2 : 1, coordinates), "Mouse down was not queued.");
                await Settle();
                SetKeyboardState(keys);
                Check(PostMessage(handle, right ? 0x0205u : 0x0202u, 0, coordinates), "Mouse up was not queued.");
                await Settle();
            }
            finally
            {
                SetKeyboardState(keys);
                if (acquired && GetCapture() == handle) ReleaseCapture();
            }
        }
        async Task TypeInPopup(string operation, char character, long begin, long before)
        {
            await WaitUntilAsync(() => popup.IsOpen && popup.Child.IsVisible, "Annotation popup did not open on " + operation);
            await Settle();
            var input = Descendants<TextBox>(popup.Child).Single();
            var elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            var focused = Keyboard.FocusedElement;
            var hwnd = GetFocus();
            var popupHwnd = ((HwndSource)PresentationSource.FromVisual(input)!).Handle;
            var previous = input.Text;
            var caret = input.CaretIndex;
            Check(hwnd != 0, "The isolated desktop has no active keyboard window.");
            Check(PostUnicodeInput(hwnd, 0x0102, character, 1), "Native text input could not be queued.");
            await Settle();
            var correct = ready && ReferenceEquals(focused, input) && previous.Length == caret &&
                input.Text == previous + character && ((AiEntry)input.Tag).Question == input.Text &&
                composer.Text == book.Draft;
            Console.WriteLine($"{operation}: ready on open={ready}, focused after layout={ReferenceEquals(focused, input)}, hwnd={hwnd:X}/{popupHwnd:X}, caret={caret}.");
            if (!typeOnOpen) samples.Add(new { operation, milliseconds = elapsed, bytes, controls = Descendants<Control>(popup.Child).Count(c => c.IsVisible), focused = correct });
            if (!correct) failures.Add(operation);
            if (composer.Text != book.Draft) composer.Text = book.Draft;
        }
        for (var i = 0; i < 6; i++)
        {
            var answer = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == turn.Id).Answers.Single();
            await answer.RenderTask;
            await Settle();
            var layer = layers[answer];
            Observe((Popup)Field(layer, "_popup")!);
            var run = ((Paragraph)answer.Document.Blocks.FirstBlock!).Inlines.OfType<Run>().First();
            typeOnOpen = i == 0;
            answer.Focus();
            var start = run.ContentStart;
            answer.Selection.Select(start, start.GetPositionAtOffset(4)!);
            var rect = start.GetPositionAtOffset(2)!.GetCharacterRect(LogicalDirection.Forward);
            var menu = answer.ContextMenu!;
            await NativeClick(answer, new Point(rect.X + 1, rect.Y + rect.Height / 2), right: true);
            Check(menu.IsOpen && answer.HasBookmarkSelection, $"Round {i + 1}: native right click lost the answer menu or selected text.");
            var annotate = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "批注到当前会话"));
            Check(annotate.IsEnabled, "The answer annotation menu item is disabled.");
            var clicks = 0;
            void OnClick(object sender, RoutedEventArgs args) => clicks++;
            annotate.Click += OnClick;
            var began = Stopwatch.GetTimestamp();
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            ready = false;
            try
            {
                await NativeClick(annotate, new Point(annotate.ActualWidth / 2, annotate.ActualHeight / 2), right: false);
                Check(clicks == 1 && panel.AnnotationEntries.Count == 1, "The menu item did not create exactly one annotation.");
                await TypeInPopup("create", '甲', began, bytes);
            }
            catch
            {
                Console.WriteLine($"Create {i + 1}: menu={menu.IsOpen}, clicks={clicks}, selected={answer.HasBookmarkSelection}, " +
                    $"answerLoaded={answer.IsLoaded}, currentLayer={layers.TryGetValue(answer, out var live) && live == layer}, " +
                    $"entries={panel.AnnotationEntries.Count}, pending={Field(layer, "_pendingEdit") is not null}, " +
                    $"markers={((System.Collections.ICollection)Field(layer, "_positions")!).Count}, opened={openCount}, closed={closeCount}.");
                throw;
            }
            finally { annotate.Click -= OnClick; }
            var entry = panel.AnnotationEntries.Single();
            Descendants<Button>(popup.Child).Single(b => b.Name == "AnnotationConfirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var markers = (List<(AiEntry Entry, Rect Bounds)>)Field(layer, "_positions")!;
            var bounds = markers.Single(x => x.Entry == entry).Bounds;
            composer.Focus();
            began = Stopwatch.GetTimestamp();
            bytes = GC.GetAllocatedBytesForCurrentThread();
            ready = false;
            await NativeClick(layer, new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), right: false);
            await TypeInPopup("reopen", '乙', began, bytes);
            if (i == 0)
            {
                var surface = (FrameworkElement)popup.Child;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * 2), (int)Math.Ceiling(surface.ActualHeight * 2),
                    192, 192, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(output, "annotation-focus.png")); encoder.Save(image);
            }
            layer.CloseEditor();
            panel.RemoveAnnotation(entry);
            await Settle();
        }
        typeOnOpen = true;
        var editor = (LatexEditor)window.FindName("LeftEditor");
        var sourceLayer = ((Dictionary<LatexEditor, AiAnnotationLayer>)Field(window, "_sourceAnnotations")!)[editor];
        Observe((Popup)Field(sourceLayer, "_popup")!);
        ready = false;
        // A source mark enters the same editor through its document-local layer.
        editor.Focus();
        var startTime = Stopwatch.GetTimestamp();
        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        Invoke(window, "CollectSourceMark", editor, new SourceAiMark(0, 12, false));
        await TypeInPopup("source", '源', startTime, startBytes);
        sourceLayer.CloseEditor();
        await Settle();
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        Invoke(window, "SelectWorkspaceTab", tabs.Owner(TabWorkspace.PreviewKey)!.Value, TabWorkspace.PreviewKey);
        var preview = (PdfPreview)window.FindName("Preview");
        var pdf = Path.Combine(output, "focus.pdf"); WriteThemePdf(pdf);
        preview.SetCropOptions(false, false);
        await preview.LoadAsync(pdf);
        await Settle();
        var page = ((System.Collections.IEnumerable)Field(preview, "_layouts")!).Cast<object>().First();
        var left = (double)page.GetType().GetProperty("Left")!.GetValue(page)!;
        var top = (double)page.GetType().GetProperty("Top")!.GetValue(page)!;
        var pdfLayer = AdornerLayer.GetAdornerLayer(preview)!.GetAdorners(preview)!.OfType<AiAnnotationLayer>().Single();
        Observe((Popup)Field(pdfLayer, "_popup")!);
        ready = false;
        typeof(PdfPreview).GetField("_aiSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(preview, new Rect(left + 30, top + 50, 100, 50));
        startTime = Stopwatch.GetTimestamp();
        startBytes = GC.GetAllocatedBytesForCurrentThread();
        await preview.ConfirmAiSelectionAsync();
        await TypeInPopup("pdf", '页', startTime, startBytes);
        pdfLayer.CloseEditor();
        File.WriteAllText(Path.Combine(output, "annotation-focus.json"), JsonSerializer.Serialize(samples));
        Check(failures.Count == 0, "The first character missed its annotation: " + string.Join(", ", failures));
        Console.WriteLine("PASS: the focused native window takes the first typed character for new and reopened annotations, while the composer draft is preserved.");
    }
}
