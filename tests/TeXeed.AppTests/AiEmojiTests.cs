using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static void CheckAiEmoji(Window owner)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        const string source = "中文 😀 🗨️ 👩🏽‍💻 🇨🇳 结尾";
        var view = new MarkdownView { ChatMode = true, FontSize = 24, Markdown = source };
        var host = new Window { Owner = owner, Content = view, Width = 800, Height = 160, ShowInTaskbar = false };
        host.Show(); host.UpdateLayout();
        try
        {
            string Copied() => view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd).TrimEnd('\r', '\n');
            Check(Copied() == source, "Emoji changed copied Unicode source.");
            Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd).TrimEnd('\r', '\n') == source,
                "Emoji changed the canonical source used by bookmarks and citations.");
            var image = ((Paragraph)view.Document.Blocks.FirstBlock!).Inlines.OfType<Span>()
                .SelectMany(group => group.Inlines.OfType<InlineUIContainer>()).First();
            Check(view.CopyForClipboard(image.ElementStart, image.ElementEnd) == "😀", "Selecting an emoji lost its Unicode text.");
            var oldRun = new Run(source);
            var oldDocument = new FlowDocument(new Paragraph(oldRun));
            var oldPosition = oldRun.ContentStart.GetPositionAtOffset(source.IndexOf("结尾", StringComparison.Ordinal))!;
            var offset = oldDocument.ContentStart.GetOffsetToPosition(oldPosition);
            var anchor = new AiAnswerAnchor("turn", "segment", offset, 2, "结尾", view.Revision);
            Check(view.AnnotationPointer("old-emoji-offset", anchor)?.GetTextInRun(LogicalDirection.Forward).StartsWith("结尾") == true,
                "Emoji rendering moved an old symbol-based bookmark.");
            var canonicalAnchor = anchor with { TextOffset = source.IndexOf("结尾", StringComparison.Ordinal) };
            Check(view.AnnotationPointer("emoji-text-offset", canonicalAnchor)?.GetTextInRun(LogicalDirection.Forward).StartsWith("结尾") == true,
                "Emoji rendering moved a canonical text offset.");
            view.IsStreaming = true;
            for (var i = 0; i <= source.Length; i++)
            {
                view.Markdown = source[..i];
                Check(Copied() == source[..i], $"Stream changed Unicode at UTF-16 position {i}.");
            }
            view.IsStreaming = false;
            Check(Copied() == source, "Settling a stream changed its emoji source.");
            Check(EmojiSprite.Create("🗨️", 24)?.Source is not null && EmojiSprite.Create("👩🏽‍💻", 24)?.Source is not null,
                "Bundled emoji sprites do not contain complete graphemes.");
            var reused = EmojiSprite.BitmapFor("😀");
            Check(reused is not null && ReferenceEquals(reused, EmojiSprite.BitmapFor("😀")), "Repeated emoji did not reuse its bitmap.");
            var inserted = 0;
            for (var code = 0x1F300; code <= 0x1F9FF && inserted < 257; code++)
            {
                var candidate = char.ConvertFromUtf32(code);
                if (candidate == "😀" || EmojiSprite.BitmapFor(candidate) is null) continue;
                inserted++;
            }
            Check(inserted == 257 && !ReferenceEquals(reused, EmojiSprite.BitmapFor("😀")), "Emoji cache did not evict old entries at its bound.");
            Check(EmojiSprite.Create("©\uFE0E", 24) is null, "Text presentation was changed to emoji.");
            const string twitterOnly = "\U0010FFFD";
            var fixture = new Dictionary<string, int[]> { [twitterOnly] = [32, 47, 1] };
            var token = EmojiSprite.SplitFromIndex("A" + twitterOnly + "B", fixture).Single(item => item.Emoji);
            var fallback = EmojiSprite.CreateFromIndex(token.Text, 24, fixture);
            var crop = fallback?.Source as CroppedBitmap;
            Check(crop?.Source is BitmapImage bitmap && bitmap.UriSource.AbsoluteUri.EndsWith("/twemoji.png", StringComparison.OrdinalIgnoreCase),
                "A Twitter-only emoji did not select the Twemoji sprite sheet.");
            var pixels = new byte[32 * 32 * 4]; crop!.CopyPixels(pixels, 32 * 4, 0);
            Check(Enumerable.Range(0, 32 * 32).Any(i => pixels[i * 4 + 3] != 0), "The fallback sprite was transparent.");
            fallback!.Measure(new Size(24, 24)); fallback.Arrange(new Rect(0, 0, 24, 24));
            var rendered = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32); rendered.Render(fallback);
            var renderedPixels = new byte[24 * 24 * 4]; rendered.CopyPixels(renderedPixels, 24 * 4, 0);
            Check(Enumerable.Range(0, 24 * 24).Any(i => renderedPixels[i * 4 + 3] != 0), "Twemoji fallback did not render in WPF.");
        }
        finally { host.Content = null; host.Close(); }
    }
}
