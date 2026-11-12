using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiInlineBranchAsync(Window owner, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch
        { Paragraph paragraph => [paragraph], Section section => Paragraphs(section.Blocks), System.Windows.Documents.List list => list.ListItems.SelectMany(item => Paragraphs(item.Blocks)), _ => [] });
        static IEnumerable<InlineUIContainer> InlineObjects(InlineCollection inlines) => inlines.SelectMany(inline => inline switch
        { InlineUIContainer container => [container], Span span => InlineObjects(span.Inlines), _ => [] });
        var markdown = "# 样例\n\n这是 **加粗子句** 后面的正文，继续排版。\n\n跨段起始甲。\n\n跨段结尾乙，后续文字。\n\n前一处重名在这里。\n\n后一处重名在这里。";
        var view = new MarkdownView { ChatMode = true, Markdown = markdown, FontSize = 16, Width = 570 };
        var host = new Window { Owner = owner, Width = 620, Height = 600, Content = new ScrollViewer { Content = view }, ShowInTaskbar = false };
        host.Show();
        try
        {
            await view.RenderTask; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var original = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            var originalClipboard = view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd);
            var initialParagraph = Paragraphs(view.Document.Blocks).First(paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains("加粗子句"));
            var initialLine = initialParagraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            AiAnswerAnchor Anchor(string quote, int occurrence = 0)
            {
                var index = -1;
                for (var i = 0; i <= occurrence; i++) index = original.IndexOf(quote, index + 1, StringComparison.Ordinal);
                if (index < 0) throw new Exception("Missing fixture quote: " + quote);
                return new("turn", "answer", index, quote.Length, quote, view.Revision) { TextOffset = index };
            }
            (string before, string after) Context(AiAnswerAnchor anchor)
            {
                var start = anchor.TextOffset; var end = start + anchor.Quote.Length;
                return (original[Math.Max(0, start - 32)..start], original[end..Math.Min(original.Length, end + 32)]);
            }
            var called = new List<string>();
            var nested = Anchor("粗子"); var nestedContext = Context(nested);
            Check(view.AddBranchLink("nested", "解释", nested, nestedContext.before, nestedContext.after, () => called.Add("nested")), "Could not insert into formatted text");
            var first = view.BranchLinkBlocks["nested"];
            Check(first.ElementStart.Paragraph is { } firstParagraph && new TextRange(firstParagraph.ContentStart, first.ElementStart).Text.EndsWith("加粗子", StringComparison.Ordinal), "Link did not follow the selected text inside formatted content");
            Check(view.AddBranchLink("nested-2", "第二个", nested, nestedContext.before, nestedContext.after, () => called.Add("nested-2")), "Could not insert a second link at the same location");
            Check(first.ElementStart.CompareTo(view.BranchLinkBlocks["nested-2"].ElementStart) < 0, "Links at the same position changed their order");
            var start = original.IndexOf("起始甲", StringComparison.Ordinal);
            var stop = original.IndexOf("结尾乙", start, StringComparison.Ordinal) + "结尾乙".Length;
            var spanning = Anchor(original[start..stop]); var spanningContext = Context(spanning);
            Check(view.AddBranchLink("spanning", "跨段", spanning, spanningContext.before, spanningContext.after, () => called.Add("spanning")), "Could not insert a link at a cross-paragraph selection");
            var spanLink = view.BranchLinkBlocks["spanning"];
            Check(spanLink.ElementStart.Paragraph is { } lastParagraph && new TextRange(lastParagraph.ContentStart, spanLink.ElementStart).Text.EndsWith("结尾乙", StringComparison.Ordinal), "Cross-paragraph link was not inserted at the end of the selection");
            var repeated = Anchor("重名", 1); var repeatedContext = Context(repeated);
            Check(view.AddBranchLink("repeat", "第二处", repeated, repeatedContext.before, repeatedContext.after, () => called.Add("repeat")), "Could not disambiguate a repeated selection");
            Check(view.BranchLinkBlocks["repeat"].ElementStart.Paragraph is { } repeatedParagraph && new TextRange(repeatedParagraph.ContentStart, view.BranchLinkBlocks["repeat"].ElementStart).Text.Contains("后一处重名"), "Repeated selection linked to the wrong occurrence");
            Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) == original, "Inline links changed stored source offsets");
            Check(view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd) == originalClipboard, "Inline links appeared in copied source text");
            var nestedButton = (Button)first.Child;
            Check(nestedButton.FontFamily.Source == view.FontFamily.Source && Math.Abs(nestedButton.FontSize - view.FontSize) < .01 && nestedButton.FontWeight == FontWeights.Normal, "Link typography does not match the body");
            nestedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(called.SequenceEqual(["nested"]), "Clicking a link opened the wrong conversation");
            Check(view.AddBranchLink("nested", "已改名", nested, nestedContext.before, nestedContext.after, () => { }), "Updating a link failed");
            Check(Equals(nestedButton.Content, "🗨️ 已改名") && view.BranchLinkBlocks.Count == 4, "Renaming recreated or duplicated the link");
            host.UpdateLayout();
            var sourceRect = first.ElementStart.GetCharacterRect(LogicalDirection.Backward);
            var buttonRect = nestedButton.TransformToAncestor(view).TransformBounds(new Rect(0, 0, nestedButton.ActualWidth, nestedButton.ActualHeight));
            var label = Descendants<TextBlock>(nestedButton).Single(text => text.Text == "🗨️ 已改名");
            var labelRect = label.TransformToAncestor(view).TransformBounds(new Rect(0, 0, label.ActualWidth, label.ActualHeight));
            Console.WriteLine($"Inline baseline: source={sourceRect} button={buttonRect} label={labelRect} labelLineHeight={label.LineHeight:F2} buttonMinHeight={nestedButton.MinHeight:F2} initialHeight={initialLine.Height:F2}");
            SaveLiteralVisual(view, Path.Combine(output, "inline-branch-wide.png"));
            Check(!sourceRect.IsEmpty && Math.Abs(sourceRect.Height - initialLine.Height) < 1 && Math.Abs(sourceRect.Top + sourceRect.Height / 2 - labelRect.Top - labelRect.Height / 2) < 3, "Inline link changed the reading line height or visibly shifted its baseline");
            host.Width = 390; view.Width = 340; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var longTitle = string.Concat(Enumerable.Repeat("超长会话标题", 25));
            var longAnchor = Anchor("继续排版"); var longContext = Context(longAnchor);
            var paragraphBeforeLong = Paragraphs(view.Document.Blocks).First(paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains("继续排版"));
            var originalLineMode = paragraphBeforeLong.LineStackingStrategy;
            var earlyLineBefore = first.ElementStart.GetCharacterRect(LogicalDirection.Backward);
            var lateLineBefore = view.ResolveBranchAnchor(longAnchor, longContext.before, longContext.after, reveal: false)!.GetCharacterRect(LogicalDirection.Forward);
            Check(view.AddBranchLink("long", longTitle, longAnchor, longContext.before, longContext.after, () => called.Add("long")), "Long branch title could not be inserted");
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            var longButton = (Button)view.BranchLinkBlocks["long"].Child;
            var longText = Descendants<TextBlock>(longButton).Single(text => text.Text == "🗨️ " + longTitle);
            Check(longButton.ActualWidth <= view.ActualWidth - 19 && longText.ActualHeight > longText.FontSize * 1.5, "Long branch title did not wrap inside the available reading width");
            var longRect = longButton.TransformToAncestor(view).TransformBounds(new Rect(0, 0, longButton.ActualWidth, longButton.ActualHeight));
            var beforeRect = view.BranchLinkBlocks["long"].ElementStart.GetCharacterRect(LogicalDirection.Backward);
            var afterRect = view.BranchLinkBlocks["long"].ElementEnd.GetCharacterRect(LogicalDirection.Forward);
            var nextParagraph = Paragraphs(view.Document.Blocks).First(paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains("跨段起始甲"));
            var nextRect = nextParagraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            static bool OverlapsCaret(Rect link, Rect caret) => !caret.IsEmpty && caret.Top < link.Bottom - 1 && caret.Bottom > link.Top + 1 && caret.Left > link.Left && caret.Left < link.Right;
            Console.WriteLine($"Wrapped link geometry: link={longRect} before={beforeRect} after={afterRect} next={nextRect} mode={paragraphBeforeLong.LineStackingStrategy}");
            Check(!OverlapsCaret(longRect, beforeRect) && !OverlapsCaret(longRect, afterRect) && !OverlapsCaret(longRect, nextRect), "Wrapped branch title overlaps adjacent source text");
            var earlyLineAfter = first.ElementStart.GetCharacterRect(LogicalDirection.Backward);
            var lateLineAfter = view.ResolveBranchAnchor(longAnchor, longContext.before, longContext.after, reveal: false)!.GetCharacterRect(LogicalDirection.Forward);
            Check(Math.Abs((lateLineBefore.Top - earlyLineBefore.Top) - (lateLineAfter.Top - earlyLineAfter.Top)) < 1, "Wrapping a branch title changed preceding line spacing");
            longButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(called.Last() == "long", "A wrapped branch title did not open its conversation");
            Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) == original && view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd) == originalClipboard, "Wrapped branch title changed copied source text");
            SaveLiteralVisual(view, Path.Combine(output, "inline-branch-narrow.png"));
            var resizableTitle = string.Concat(Enumerable.Repeat("超长会话标题", 5));
            Check(view.AddBranchLink("long", resizableTitle, longAnchor, longContext.before, longContext.after, () => { }), "Renaming the wrapped link failed");
            host.Width = 900; view.Width = 850; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(paragraphBeforeLong.LineStackingStrategy == originalLineMode, "Widening a single-line title did not restore paragraph line stacking");
            host.Width = 390; view.Width = 340; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(paragraphBeforeLong.LineStackingStrategy == LineStackingStrategy.MaxHeight, "Narrowing the wrapped title did not preserve its layout height");
            Check(view.RemoveBranchLink("long"), "Removing the wrapped link failed");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(paragraphBeforeLong.LineStackingStrategy == originalLineMode, "Removing the wrapped link did not restore paragraph line stacking");
            Check(view.RemoveBranchLink("nested") && !view.BranchLinkBlocks.ContainsKey("nested"), "Deleting a branch did not remove its link");
            Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) == original && view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd) == originalClipboard, "Removing a link damaged the original text");
            view.Markdown = "# 隐藏章节\n\n前面的重复定义隐藏在此。\n\n# 可见章节\n\n这里有独一无二的解释，后面还有重复定义。";
            var folds = new Dictionary<string, bool>();
            view.ConfigureFolding(folds, "answer", () => { }); await view.RenderTask; host.UpdateLayout();
            Check(view.BranchLinkBlocks.Count == 0, "Re-rendering retained stale links");
            var fullText = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            var foldHeading = Paragraphs(view.Document.Blocks).First(p => p.Inlines.OfType<Hyperlink>().Any());
            foldHeading.Inlines.OfType<Hyperlink>().First().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Check(folds.Values.Count(value => value) == 1, "The unrelated fixture section did not fold");
            var uniqueOffset = fullText.IndexOf("独一无二", StringComparison.Ordinal);
            var legacy = new AiAnswerAnchor("old", "answer", 0, 4, "独一无二", view.Revision) { TextOffset = uniqueOffset };
            Check(view.AddBranchLink("legacy", "旧锚点", legacy, null, null, () => { }), "A unique legacy anchor was lost when an unrelated section was folded");
            var duplicateOffset = fullText.LastIndexOf("重复定义", StringComparison.Ordinal);
            var duplicate = new AiAnswerAnchor("old", "answer", 0, 4, "重复定义", view.Revision) { TextOffset = duplicateOffset };
            Check(!view.AddBranchLink("ambiguous", "有歧义", duplicate, null, null, () => { }), "A hidden duplicate was linked without enough context");
            var beforeDuplicate = fullText[Math.Max(0, duplicateOffset - 32)..duplicateOffset];
            var afterDuplicate = fullText[(duplicateOffset + duplicate.Quote.Length)..Math.Min(fullText.Length, duplicateOffset + duplicate.Quote.Length + 32)];
            Check(view.AddBranchLink("duplicate", "可见重复项", duplicate, beforeDuplicate, afterDuplicate, () => { }), "A repeated visible anchor with context could not be located");
            Check(folds.Values.Count(value => value) == 1, "Inserting a link opened an unrelated folded section");
            view.Markdown = "# 可见章节\n\n可见起点甲。\n\n## 折叠子节\n\n折叠中间乙。\n\n折叠末尾丙。\n\n# 后续章节\n\n后续可见目标丁。" + new string('长', 500);
            var spanningFolds = new Dictionary<string, bool>();
            view.ConfigureFolding(spanningFolds, "answer", () => { }); await view.RenderTask; host.UpdateLayout();
            var unfoldedText = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            var hiddenHeading = Paragraphs(view.Document.Blocks).First(paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains("折叠子节"));
            hiddenHeading.Inlines.OfType<Hyperlink>().First().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Check(spanningFolds.Values.Count(value => value) == 1, "The spanning fixture section did not fold");
            var foldedText = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            void CheckHiddenSelection(string id, string ending)
            {
                var begin = unfoldedText.IndexOf("可见起点甲", StringComparison.Ordinal);
                var finish = unfoldedText.IndexOf(ending, begin, StringComparison.Ordinal) + ending.Length;
                Check(begin >= 0 && finish >= begin, "The spanning fixture was not constructed correctly");
                var quote = unfoldedText[begin..finish];
                var anchor = new AiAnswerAnchor("folded", "answer", begin, quote.Length, quote, view.Revision) { TextOffset = begin };
                var left = unfoldedText[Math.Max(0, begin - 32)..begin];
                var right = unfoldedText[finish..Math.Min(unfoldedText.Length, finish + 32)];
                Check(!view.AddBranchLink(id, "折叠选区", anchor, left, right, () => { }), "A selection with folded text was linked to unrelated visible text");
            }
            CheckHiddenSelection("hidden-end", "折叠末尾丙");
            CheckHiddenSelection("hidden-middle", "后续可见目标丁");
            Check(view.BranchLinkBlocks.Count == 0 && spanningFolds.Values.Count(value => value) == 1 && view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) == foldedText,
                "A hidden selection inserted a link or opened a folded section");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAYUBgSaRwG8AAAAASUVORK5CYII=");
            File.WriteAllBytes(Path.Combine(output, "inline-branch-image.png"), png);
            view.ImageBaseDirectory = output;
            view.Markdown = "行间公式：\n\n$$x^2+1$$\n\n图片说明 ![算法图](inline-branch-image.png) 图片后文。";
            await view.RenderTask; await view.MediaTask; host.UpdateLayout();
            var allObjects = Paragraphs(view.Document.Blocks).SelectMany(paragraph => InlineObjects(paragraph.Inlines)).Where(inline => inline.Tag is string).ToArray();
            var formula = allObjects.Single(inline => ((string)inline.Tag).StartsWith("$$", StringComparison.Ordinal));
            var image = allObjects.Single(inline => ((string)inline.Tag).StartsWith("[图片] ", StringComparison.Ordinal));
            var beforeObjects = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            var beforeObjectClipboard = view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd);
            foreach (var (objectId, embedded) in new[] { ("formula", formula), ("image", image) })
            {
                view.Selection.Select(embedded.ElementStart, embedded.ElementEnd);
                Check(view.SelectedReferenceObject().Kind == objectId, $"Right-click selection no longer identifies {objectId}");
                var selection = view.GetAnnotationSelection(); var context = view.BookmarkSelectionContext();
                var objectAnchor = new AiAnswerAnchor("objects", "answer", selection.Offset, selection.Length, selection.Quote, view.Revision) { TextOffset = selection.TextOffset };
                Check(view.AddBranchLink(objectId, "对象追问", objectAnchor, context.Before, context.After, () => { }), $"Cannot insert a branch after the selected {objectId}");
                Check(embedded.ElementEnd.CompareTo(view.BranchLinkBlocks[objectId].ElementStart) <= 0 && embedded.ElementStart.Paragraph == view.BranchLinkBlocks[objectId].ElementStart.Paragraph, $"The branch was not placed immediately after the {objectId}");
            }
            var imageParagraph = image.ElementStart.Paragraph!;
            view.Selection.Select(imageParagraph.ContentStart, image.ElementEnd);
            var mixedSelection = view.GetAnnotationSelection(); var mixedContext = view.BookmarkSelectionContext();
            var mixedAnchor = new AiAnswerAnchor("objects", "answer", mixedSelection.Offset, mixedSelection.Length, mixedSelection.Quote, view.Revision) { TextOffset = mixedSelection.TextOffset };
            Check(view.AddBranchLink("text-image", "图前文字", mixedAnchor, mixedContext.Before, mixedContext.After, () => { }), "Cannot insert a branch after a selection ending in an image");
            Check(image.ElementEnd.CompareTo(view.BranchLinkBlocks["text-image"].ElementStart) <= 0, "The image-ending selection linked before the image");
            Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd) == beforeObjects && view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd) == beforeObjectClipboard, "Media branch links changed copied source text");
            Console.WriteLine("PASS: inline branch positions, nested and cross-paragraph selections, repeated anchors, ordering, copying, click, rename and cleanup.");
        }
        finally { host.Content = null; host.Close(); }
    }
}
