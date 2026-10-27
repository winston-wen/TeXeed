using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private static async Task CheckAiReadingMediaAsync(MainWindow owner, string output, bool inputReview = false)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
        {
            foreach (var block in blocks)
                if (block is Paragraph paragraph) yield return paragraph;
                else if (block is Section section)
                    foreach (var child in Paragraphs(section.Blocks)) yield return child;
        }
        var source = string.Join("\n\n", Enumerable.Range(1, 6).Select(n => new string('#', n) + $" Heading {n}\n\nBody {n}."));
        var plain = new MarkdownView { Markdown = source };
        var view = new MarkdownView { ChatMode = true, TurnNumber = 3, ImageBaseDirectory = output };
        var folds = new Dictionary<string, bool>();
        view.ConfigureFolding(folds, "segment", () => { }); view.Markdown = source;
        var host = new Window { Owner = owner, Content = new ScrollViewer { Content = view }, Width = 650, Height = 760, Title = "AI reading and media · synthetic" };
        host.Show();
        try
        {
            host.UpdateLayout();
            var headings = Paragraphs(view.Document.Blocks).Where(p => p.Inlines.FirstInline is Hyperlink).ToArray();
            Check(headings.Length == 6, "Six Markdown heading levels were not preserved.");
            for (var i = 0; i < headings.Length; i++)
            {
                Check(new TextRange(headings[i].ContentStart, headings[i].ContentEnd).Text.StartsWith($"[3:h{i + 1}]"), "Heading marker has the wrong level or turn.");
                Check(headings[i].FontSize == view.FontSize && headings[i].FontWeight == FontWeights.Normal && headings[i].LineHeight == 25,
                    "Answer heading changed body typography.");
            }
            Check(plain.Document.Blocks.OfType<Paragraph>().First().FontSize > plain.FontSize, "Question/tutorial heading style changed.");
            var canonical = plain.CopySource(plain.Document.ContentStart, plain.Document.ContentEnd);
            Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) == canonical, "Heading markers shifted canonical annotation offsets.");
            Check(!view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd).Contains("[3:h"), "Clipboard included heading decoration.");
            var document = view.Document; view.TurnNumber = 4;
            Check(ReferenceEquals(document, view.Document) && new TextRange(headings[5].ContentStart, headings[5].ContentEnd).Text.StartsWith("[4:h6]"), "Changing the ordinal rebuilt the answer or missed h6.");
            var h3 = (Hyperlink)headings[2].Inlines.FirstInline!; h3.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Check(folds.Values.Single(), "Heading fold was not saved.");
            h3.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Check(!folds.Values.Single(), "Heading fold did not reopen.");
            var titleRun = ((Hyperlink)headings[5].Inlines.FirstInline!).Inlines.OfType<Run>().Last();
            view.Selection.Select(titleRun.ContentStart, titleRun.ContentEnd);
            Check(view.GetAnnotationSelection().Quote == "Heading 6", "Heading annotation contains decoration.");
            var legacy = new MarkdownView(); legacy.ConfigureFolding(new(), "old", () => { }); legacy.Markdown = source;
            var oldTitle = ((Hyperlink)Paragraphs(legacy.Document.Blocks).Where(p => p.Inlines.FirstInline is Hyperlink).Last().Inlines.FirstInline!).Inlines.OfType<Run>().Last();
            var oldAnchor = new AiAnswerAnchor("turn", "segment", legacy.Document.ContentStart.GetOffsetToPosition(oldTitle.ContentStart), 9, "Heading 6", legacy.Revision);
            legacy.ChatMode = true; legacy.TurnNumber = 3;
            Check(legacy.AnnotationPointer("old-title", oldAnchor)?.GetTextInRun(LogicalDirection.Forward).StartsWith("Heading 6") == true, "Legacy heading symbol offset moved after adding the marker.");

            var tableSource = "Before table.\n\n| " + string.Join(" | ", Enumerable.Range(1, 12).Select(i => "Column " + i)) + " |\n| "
                + string.Join(" | ", Enumerable.Repeat("---", 12)) + " |\n| "
                + string.Join(" | ", Enumerable.Range(1, 12).Select(i => i == 2 ? "`Cell 2`" : "Cell " + i)) + " |\n\nAfter table.";
            plain.Markdown = tableSource; canonical = plain.CopySource(plain.Document.ContentStart, plain.Document.ContentEnd);
            view.Markdown = tableSource; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var nested = Descendants<MarkdownView>(view).Single();
            var tableScroll = Descendants<ScrollViewer>(view).Single(s => ReferenceEquals(s.Content, nested));
            Check(tableScroll.ScrollableWidth > 0 && tableScroll.ViewportWidth < nested.ActualWidth, "Wide table did not scroll inside the answer.");
            var nativeTable = (Table)nested.Document.Blocks.FirstBlock!;
            var firstCell = nativeTable.RowGroups[0].Rows[0].Cells[0];
            var secondCell = nativeTable.RowGroups[0].Rows[0].Cells[1];
            Check(secondCell.ContentStart.GetCharacterRect(LogicalDirection.Forward).Left - firstCell.ContentStart.GetCharacterRect(LogicalDirection.Forward).Left < 200,
                "Table columns expanded to viewport-sized cells.");
            Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) == canonical, "Moving the table to its viewport changed canonical text.");
            Check(view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd).Contains("Cell 12"), "Whole-answer copy lost the table.");
            var cellOffset = canonical.IndexOf("Cell 10", StringComparison.Ordinal);
            var anchor = new AiAnswerAnchor("turn", "segment", 0, 7, "Cell 10", view.Revision) { TextOffset = cellOffset };
            var pointer = view.AnnotationPointer("cell", anchor)!;
            Check(pointer.IsInSameDocument(nested.Document.ContentStart) && pointer.GetTextInRun(LogicalDirection.Forward).StartsWith("Cell 10"), "Existing table TextOffset did not resolve into the child document.");
            nested.Focus(); nested.Selection.Select(pointer, pointer.GetPositionAtOffset(7)!);
            var selected = view.GetAnnotationSelection();
            Check(selected.Quote == "Cell 10" && selected.TextOffset == cellOffset, "Table annotation selection was read from the parent document.");
            var pointBefore = view.AnnotationPoint("cell", anchor)!.Value;
            tableScroll.ScrollToHorizontalOffset(200); host.UpdateLayout();
            var pointAfter = view.AnnotationPoint("cell", anchor)!.Value;
            Check(pointAfter.X < pointBefore.X - 100, "Table annotation position did not follow horizontal scrolling.");
            var tail = new AiAnswerAnchor("turn", "segment", 0, 5, "After table", view.Revision) { TextOffset = canonical.IndexOf("After table", StringComparison.Ordinal) };
            Check(view.AnnotationPointer("tail", tail)?.GetTextInRun(LogicalDirection.Forward).StartsWith("After table") == true, "Existing annotation after the table shifted.");
            var oldTable = plain.Document.Blocks.OfType<Table>().Single();
            var oldCodeCell = oldTable.RowGroups[0].Rows[1].Cells[1].Blocks.OfType<Paragraph>().Single();
            var decoratedCode = oldCodeCell.Inlines.OfType<Span>().Single();
            oldCodeCell.Inlines.InsertBefore(decoratedCode, new Run("Cell 2")); oldCodeCell.Inlines.Remove(decoratedCode);
            var oldCell = oldTable.RowGroups[0].Rows[1].Cells[9].Blocks.OfType<Paragraph>().Single().Inlines.FirstInline!.ContentStart;
            var legacyCell = new AiAnswerAnchor("turn", "segment", plain.Document.ContentStart.GetOffsetToPosition(oldCell), 7, "Cell 10", view.Revision);
            Check(view.AnnotationPointer("legacy-cell", legacyCell)?.GetTextInRun(LogicalDirection.Forward).StartsWith("Cell 10") == true, "Legacy table symbol offset did not enter the child table.");
            var oldTail = ((Paragraph)plain.Document.Blocks.LastBlock!).Inlines.FirstInline!.ContentStart;
            var legacyTail = new AiAnswerAnchor("turn", "segment", plain.Document.ContentStart.GetOffsetToPosition(oldTail), 5, "After", view.Revision);
            Check(view.AnnotationPointer("legacy-tail", legacyTail)?.GetTextInRun(LogicalDirection.Forward).StartsWith("After table") == true, "Legacy symbol offset after the table shifted.");
            string? copied = null;
            DataObjectCopyingEventHandler capture = (_, e) => { copied = e.DataObject.GetData(DataFormats.UnicodeText) as string; e.CancelCommand(); };
            DataObject.AddCopyingHandler(view, capture); ApplicationCommands.Copy.Execute(null, view); DataObject.RemoveCopyingHandler(view, capture);
            Check(copied == "Cell 10", "Parent copy command ignored the table selection.");
            var tailParagraph = (Paragraph)view.Document.Blocks.LastBlock!;
            view.Focus(); view.Selection.Select(tailParagraph.ContentStart, tailParagraph.ContentStart);
            var context = new ContextMenu { PlacementTarget = view }; var annotate = new MenuItem { Header = "批注", Focusable = true }; context.Items.Add(annotate); view.ContextMenu = context;
            try
            {
                context.IsOpen = true; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Keyboard.ClearFocus();
                Check(!view.IsKeyboardFocused && !nested.IsKeyboardFocusWithin, "The open menu still left an active text focus.");
                Check(view.GetAnnotationSelection().Quote == "After table.", "A stale child selection replaced the current parent paragraph when the context menu took focus.");
                context.IsOpen = false; nested.Focus();
                context.IsOpen = true; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Keyboard.ClearFocus();
                Check(view.GetAnnotationSelection().Quote == "Cell 10", "The context menu lost the active table selection.");
            }
            finally { context.IsOpen = false; view.ContextMenu = null; }

            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            { dc.DrawRectangle(Brushes.LightSlateGray, null, new Rect(0, 0, 1600, 900)); dc.DrawEllipse(Brushes.Gold, null, new Point(800, 450), 260, 260); }
            var bitmap = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var imagePath = Path.Combine(output, "synthetic-reading-image.png");
            using (var file = File.Create(imagePath)) encoder.Save(file);
            view.ImageBaseDirectory = null;
            view.Markdown = "[![Synthetic `chart` $x$](synthetic-reading-image.png)](https://example.invalid/)\n\nImage tail.";
            var unresolvedImageText = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            view.ImageBaseDirectory = output;
            Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) == unresolvedImageText, "Resolving an image changed code/math alt text and subsequent annotation offsets.");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await view.MediaTask.WaitAsync(TimeSpan.FromSeconds(5)); host.UpdateLayout();
            var image = Descendants<Image>(view).SingleOrDefault() ?? throw new Exception("Image missing: " + view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) + " | " + string.Join(";", Descendants<TextBlock>(view).Select(t => t.Text)));
            Check(image.Source is BitmapSource { PixelWidth: 1600, PixelHeight: 900 } && image.ActualWidth < 1600, "Relative Markdown image did not load and fit its answer.");
            Exception? previewFailure = null;
            _ = host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var preview = Application.Current.Windows.OfType<ImagePreview>().Single();
                try
                {
                    preview.UpdateLayout(); preview.Fit();
                    Check(preview.Scale < 1, "Image fit did not reduce a large picture.");
                    preview.SetScale(1); Check(preview.Scale == 1, "100% lost native size.");
                    preview.SetScale(1.25); Check(preview.Scale == 1.25, "Image zoom was not applied.");
                    preview.Fit(); Capture(preview, Path.Combine(output, "native-image-preview.png"));
                    var escape = CreateKey(preview, Key.Escape, Keyboard.PreviewKeyDownEvent); preview.RaiseEvent(escape);
                    Check(escape.Handled, "Escape did not close image preview.");
                }
                catch (Exception ex) { previewFailure = ex; }
                finally { if (preview.IsVisible) preview.Close(); }
            }));
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
            image.RaiseEvent(click);
            if (previewFailure is not null) throw previewFailure;
            Check(click.Handled && image.IsKeyboardFocused && !Application.Current.Windows.OfType<ImagePreview>().Any(), "Image activation escaped its link, leaked a dialog, or lost return focus.");
            var base64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
            Check(ImagePreview.LoadBase64(base64).PixelWidth == 1600, "PDF preview factory decoded a thumbnail instead of native pixels.");
            File.WriteAllText(Path.Combine(output, "unsupported.svg"), "<svg xmlns='http://www.w3.org/2000/svg'/>");
            view.Markdown = "![Unsupported SVG](unsupported.svg)"; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await view.MediaTask.WaitAsync(TimeSpan.FromSeconds(5)); host.UpdateLayout();
            Check(Descendants<TextBlock>(view).Any(t => t.Text.Contains("无法读取此图片格式或地址")), "Unsupported image did not retain an explanatory placeholder.");
            view.Markdown = "![Directory](.)"; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await view.MediaTask.WaitAsync(TimeSpan.FromSeconds(5)); host.UpdateLayout();
            Check(Descendants<TextBlock>(view).Any(t => t.Text.Contains("无法读取此图片格式或地址")), "A directory image did not become a readable failure placeholder.");
            view.Markdown = source + "\n\n" + tableSource + "\n\n![Synthetic chart](synthetic-reading-image.png)";
            host.UpdateLayout(); await view.RenderTask; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await view.MediaTask; host.UpdateLayout();
            Capture(host, Path.Combine(output, "native-reading-media.png"));
            await CheckAiMarkingInputAsync(host, output, imagePath, inputReview);
            Console.WriteLine("PASS: six inline answer heading levels, stable folds/copy/annotation text; local table scrolling with child selection and old anchors; local image, native modal zoom/fit/100%, Escape/focus, unsupported-format placeholder.");
        }
        finally { host.Close(); }
    }

    private static async Task CheckAiMarkingInputAsync(Window host, string output, string imagePath, bool inputReview)
    {
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        await panel.SetProjectAsync(Path.Combine(output, "marking-input.tex"));
        var book = (AiNotebook)Field(panel, "_book")!; book.Entries.Clear(); book.Turns.Clear();
        const string sentence = "Synthetic drag selection.";
        var answerText = sentence + "\n\n![Synthetic image](" + new Uri(imagePath).AbsoluteUri + ")";
        book.Turns.Add(new AiTurn("Synthetic question", answerText));
        host.Content = panel; Invoke(panel, "RefreshTranscript"); host.Activate(); host.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var answer = Descendants<MarkdownView>(panel).Single(v => v.Markdown == answerText);
        await answer.MediaTask; host.UpdateLayout(); panel.IsMarking = true;
        answer.Document.Blocks.FirstBlock!.BringIntoView();
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
        var text = ((Paragraph)answer.Document.Blocks.FirstBlock!).Inlines.OfType<Run>().First(r => r.Text.StartsWith(sentence));
        Capture(host, Path.Combine(output, "native-marking-before-input.png"));
        var image = Descendants<Image>(answer).Single();
        if (inputReview)
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var imageClicks = 0;
            MouseButtonEventHandler imageClick = (_, _) => imageClicks++;
            image.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, imageClick, true);
            KeyEventHandler finish = (_, e) => { if (e.Key == Key.F12) { e.Handled = true; finished.TrySetResult(); } };
            host.PreviewKeyDown += finish;
            host.Title = "TeXeed synthetic marking input · F12 to finish";
            Console.WriteLine("READY: drag-select answer text, confirm the annotation, click/close the image preview, then press F12.");
            try
            {
                await finished.Task.WaitAsync(TimeSpan.FromMinutes(5));
                if (book.Entries.Count != 1 || string.IsNullOrWhiteSpace(book.Entries[0].AnswerAnchor?.Quote) || imageClicks != 1)
                    throw new Exception($"Interactive marking review mismatch: annotations={book.Entries.Count}, image clicks={imageClicks}.");
                Capture(host, Path.Combine(output, "native-marking-after-input.png"));
                Console.WriteLine("PASS: CUA text selection produced one annotation; image activation produced one preview without another annotation.");
            }
            finally { host.PreviewKeyDown -= finish; image.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, imageClick); }
            return;
        }
        answer.Selection.Select(text.ContentStart, text.ContentStart.GetPositionAtOffset(9)!);
        var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
        answer.RaiseEvent(release); release.Handled = true;
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (book.Entries.Count != 1 || string.IsNullOrWhiteSpace(book.Entries[0].AnswerAnchor?.Quote))
            throw new Exception("Text release was filtered after its routed event became handled.");
        var layers = (Dictionary<MarkdownView, AiAnnotationLayer>)Field(panel, "_answerLayers")!;
        ((System.Windows.Controls.Primitives.Popup)Field(layers[answer], "_popup")!).IsOpen = false;
        async Task ClosePreview()
        {
            await WaitUntilAsync(() => Application.Current.Windows.OfType<ImagePreview>().Any(), "Routed image click did not open its preview.");
            Application.Current.Windows.OfType<ImagePreview>().Single().Close();
        }
        var close = ClosePreview();
        image.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        await close; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (book.Entries.Count != 1) throw new Exception("Image activation also created an annotation from stale text selection.");
        Console.WriteLine("PASS: routed text release survives later Handled changes; image activation opens only the preview in marking mode.");
    }
}
