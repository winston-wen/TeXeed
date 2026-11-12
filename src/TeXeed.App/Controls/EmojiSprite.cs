using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TeXeed.App.Controls;

// Images are indexed by complete Unicode grapheme, never by individual code unit.
internal static class EmojiSprite
{
    internal static readonly object Marker = new();
    private static readonly Lazy<Dictionary<string, int[]>> Index = new(LoadIndex);
    private static readonly Lazy<BitmapSource> Noto = new(() => LoadSheet("noto.png"));
    private static readonly Lazy<BitmapSource> Twemoji = new(() => LoadSheet("twemoji.png"));
    private const int CacheLimit = 256;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();

    internal static IEnumerable<(string Text, bool Emoji)> Split(string text) => SplitFromIndex(text, Index.Value);

    internal static IEnumerable<(string Text, bool Emoji)> SplitFromIndex(string text, IReadOnlyDictionary<string, int[]> index)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var value = enumerator.GetTextElement();
            yield return (value, !value.Contains('\uFE0E') && index.ContainsKey(value));
        }
    }

    internal static Image? Create(string text, double size)
    {
        return ImageFor(BitmapFor(text), size);
    }

    internal static Image? CreateFromIndex(string text, double size, IReadOnlyDictionary<string, int[]> index)
    {
        var bitmap = BitmapFromIndex(text, index);
        return ImageFor(bitmap, size);
    }

    private static Image? ImageFor(BitmapSource? bitmap, double size) =>
        bitmap is null ? null : new Image { Source = bitmap, Width = size, Height = size, Stretch = Stretch.Uniform, Tag = Marker,
            SnapsToDevicePixels = false, Focusable = false, IsHitTestVisible = false };

    internal static BitmapSource? BitmapFor(string text)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(text, out var cached)) return cached;
            var bitmap = BitmapFromIndex(text, Index.Value);
            if (bitmap is null) return null;
            if (Cache.Count >= CacheLimit) Cache.Remove(CacheOrder.Dequeue());
            Cache.Add(text, bitmap); CacheOrder.Enqueue(text);
            return bitmap;
        }
    }

    private static BitmapSource? BitmapFromIndex(string text, IReadOnlyDictionary<string, int[]> index)
    {
        if (text.Contains('\uFE0E') || !index.TryGetValue(text, out var item)) return null;
        // Every 32px image has a transparent 1px border in the upstream sheet.
        var bitmap = new CroppedBitmap(item[2] == 0 ? Noto.Value : Twemoji.Value,
            new Int32Rect(item[0] * 34 + 1, item[1] * 34 + 1, 32, 32));
        bitmap.Freeze();
        return bitmap;
    }

    private static Dictionary<string, int[]> LoadIndex()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TeXeed.App.Assets.Emoji.index.json")
            ?? throw new InvalidOperationException("Missing emoji index");
        return JsonSerializer.Deserialize<Dictionary<string, int[]>>(stream) ?? new();
    }

    private static BitmapSource LoadSheet(string name)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri($"pack://application:,,,/{typeof(EmojiSprite).Assembly.GetName().Name};component/Assets/Emoji/{name}", UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
