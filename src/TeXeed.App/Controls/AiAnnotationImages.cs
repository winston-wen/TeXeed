using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

internal static class AiAnnotationImages
{
    internal static void EnablePaste(TextBox input, AiEntry entry, Action changed, Action<string> report,
        Func<bool>? hasImage = null, Func<BitmapSource?>? readImage = null, Func<ModifierKeys>? modifiers = null,
        Func<BitmapSource, (bool Success, string Error)>? addImage = null)
    {
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.V || (modifiers?.Invoke() ?? Keyboard.Modifiers) != ModifierKeys.Control) return;
            try
            {
                if (!(hasImage?.Invoke() ?? Clipboard.ContainsImage())) return;
                e.Handled = true;
                if ((readImage is null ? Clipboard.GetImage() : readImage()) is not { } image) { report("剪贴板图片无法读取。"); return; }
                var added = addImage is null ? (Add(entry, image, out var error), error) : addImage(image);
                if (!added.Item1) { report(added.Item2); return; }
                changed();
            }
            catch (Exception ex) when (ex is ExternalException or UnauthorizedAccessException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
            { e.Handled = true; report("无法粘贴图片：" + ex.Message); }
        };
    }

    internal static bool Add(AiEntry entry, BitmapSource image, out string error)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream);
        if (stream.Length > 8 * 1024 * 1024 || entry.Images.Sum(x => (long)x.ImageBase64.Length) + stream.Length * 4 / 3 > 16 * 1024 * 1024)
        { error = "单张图片最多 8 MB，一条批注的图片合计最多约 12 MB。"; return false; }
        entry.Images.Add(new(Convert.ToBase64String(stream.ToArray()))); error = ""; return true;
    }

    internal static FrameworkElement Create(AiEntry entry, Action changed)
    {
        var list = new StackPanel();
        DockPanel CreateRow(AiPastedImage item)
        {
            var row = new DockPanel { Tag = item, Margin = new Thickness(0, 6, 0, 0) };
            var remove = new Button { Content = "×", ToolTip = "移除此图片", Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
            remove.Click += (_, _) =>
            {
                var index = list.Children.IndexOf(row);
                if (index >= 0 && index < entry.Images.Count && ReferenceEquals(entry.Images[index], item))
                { entry.Images.RemoveAt(index); changed(); }
            };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(item.ImageBase64));
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                row.Children.Add(new Image { Source = bitmap, MaxHeight = 140, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
            }
            catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException or NotSupportedException)
            { row.Children.Add(new TextBlock { Text = "图片无法显示" }); }
            return row;
        }
        void Reconcile()
        {
            for (var index = 0; index < entry.Images.Count; index++)
            {
                var item = entry.Images[index];
                if (index < list.Children.Count && list.Children[index] is FrameworkElement row && ReferenceEquals(row.Tag, item)) continue;
                var match = -1;
                for (var i = index + 1; i < list.Children.Count; i++)
                    if (list.Children[i] is FrameworkElement candidate && ReferenceEquals(candidate.Tag, item)) { match = i; break; }
                if (match < 0) list.Children.Insert(index, CreateRow(item));
                else { var existing = list.Children[match]; list.Children.RemoveAt(match); list.Children.Insert(index, existing); }
            }
            while (list.Children.Count > entry.Images.Count) list.Children.RemoveAt(list.Children.Count - 1);
        }
        NotifyCollectionChangedEventHandler handler = (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null && e.NewStartingIndex >= 0)
                for (var i = 0; i < e.NewItems.Count; i++) list.Children.Insert(e.NewStartingIndex + i, CreateRow((AiPastedImage)e.NewItems[i]!));
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null && e.OldStartingIndex >= 0)
                for (var i = 0; i < e.OldItems.Count; i++) list.Children.RemoveAt(e.OldStartingIndex);
            else Reconcile();
        };
        list.Loaded += (_, _) => { entry.Images.CollectionChanged -= handler; entry.Images.CollectionChanged += handler; Reconcile(); };
        list.Unloaded += (_, _) => entry.Images.CollectionChanged -= handler;
        return list;
    }
}
