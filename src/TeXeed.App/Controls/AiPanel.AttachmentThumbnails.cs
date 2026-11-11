using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private void AttachmentItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DockPanel row || row.DataContext is not AiAttachmentItem { IsFolder: false } item ||
            item.Files.Count != 1 || item.Files[0].ReferenceKind is not null ||
            !item.Files[0].MimeType.StartsWith("image/", StringComparison.Ordinal) ||
            row.Children.OfType<AttachmentThumbnail>().Any()) return;
        var thumbnail = new AttachmentThumbnail(item.Files[0]);
        DockPanel.SetDock(thumbnail, Dock.Left);
        row.Children.Insert(0, thumbnail);
    }
}

// Decode uploaded image snapshots only while the attachment list is open.
internal sealed class AttachmentThumbnail : Image
{
    private static readonly SemaphoreSlim DecodeGate = new(2);
    private readonly AiAttachedFile _file;
    private CancellationTokenSource? _loading;
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    internal AttachmentThumbnail(AiAttachedFile file)
    {
        _file = file;
        Width = 36; Height = 36;
        Margin = new Thickness(0, 0, 8, 0);
        Stretch = Stretch.Uniform; StretchDirection = StretchDirection.DownOnly;
        VerticalAlignment = VerticalAlignment.Center; Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, "图片附件：" + file.Name);
        Loaded += (_, _) =>
        {
            if (_loading is not null || Source is not null) return;
            var cancellation = new CancellationTokenSource(); _loading = cancellation;
            LoadTask = LoadAsync(cancellation);
        };
        Unloaded += (_, _) =>
        {
            var cancellation = _loading; _loading = null; cancellation?.Cancel();
            Source = null; Visibility = Visibility.Collapsed;
        };
    }

    private async Task LoadAsync(CancellationTokenSource cancellation)
    {
        var entered = false;
        try
        {
            await DecodeGate.WaitAsync(cancellation.Token); entered = true;
            var bitmap = await Task.Run(() => Decode(_file, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsLoaded || !ReferenceEquals(_loading, cancellation)) return;
            Source = bitmap; Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or NotSupportedException or ArgumentException or COMException or OutOfMemoryException)
        { if (ReferenceEquals(_loading, cancellation)) { Source = null; Visibility = Visibility.Collapsed; } }
        finally
        {
            if (entered) DecodeGate.Release();
            if (ReferenceEquals(_loading, cancellation)) _loading = null;
            cancellation.Dispose();
        }
    }

    private static BitmapSource Decode(AiAttachedFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Stream stream = file.BlobId is { Length: > 0 } id
            ? AiAttachmentBlobStore.OpenRead(id, cancellationToken)
            : new MemoryStream(Convert.FromBase64String(file.Base64));
        var bitmap = new BitmapImage(); bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 72;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }
}
