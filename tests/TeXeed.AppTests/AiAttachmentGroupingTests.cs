using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiAttachmentGroupingAsync(Window owner, string output)
    {
        var checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var root = Path.Combine(output, "grouped-fixture");
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        Directory.CreateDirectory(Path.Combine(first, "sub")); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "one.txt"), "one");
        File.WriteAllText(Path.Combine(first, "sub", "two.txt"), "two");
        File.WriteAllText(Path.Combine(second, "three.txt"), "three");
        var standalone = Path.Combine(root, "loose.txt"); File.WriteAllText(standalone, "loose");
        var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var window = new Window { Owner = owner, Content = panel, Width = 850, Height = 600 }; window.Show();
        try
        {
            var project = Path.Combine(output, "grouped.tex"); await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            Check(await panel.AddAttachmentsAsync([first, standalone, second]), "Could not add mixed files and folders");
            var entry = book.Entries.Single(); var groups = entry.AttachmentItems;
            Check(entry.Files.Count == 3 && groups.Count == 3, "Folder references were not grouped");
            Check(groups.Count(g => g.IsFolder) == 2 && groups.Any(g => !g.IsFolder && g.DisplayPath == "loose.txt"), "Mixed folder and file grouping failed");
            Check(groups[0].DisplayPath == first + "\\" && groups[0].Files.Count == 1 && groups[2].DisplayPath == second + "\\", "Folder path is not displayed with trailing separator");
            Check(entry.Files.Count(file => file.ReferenceKind == "directory") == 2 && entry.Files.Single(file => file.Name == "loose.txt").BlobId is not null, "Directory reference or uploaded snapshot was lost");
            await panel.SaveAsync();
            var restored = AiStorage.Load(project).Entries.Single();
            Check(restored.AttachmentItems.Select(g => g.DisplayPath).SequenceEqual(groups.Select(g => g.DisplayPath)) && restored.Files.Count == 3, "Folder paths or payloads did not survive reload");
            var legacy = new AiEntry { Files = [new("old/a.txt", "text/plain", "YQ=="), new("old/sub/b.txt", "text/plain", "Yg=="), new("standalone.txt", "text/plain", "Yw==")] };
            Check(legacy.AttachmentItems.Count == 2 && legacy.AttachmentItems[0].DisplayPath == "old/" && legacy.AttachmentItems[0].Files.Count == 2, "Legacy relative paths did not group conservatively");
            var mixed = new AiEntry { Question = "保留评论", AnswerAnchor = new("turn", "segment", 0, 1, "文", "revision"), Files = [new("old/a.txt", "text/plain", "YQ==")] };
            Check(mixed.HasReference && mixed.HasAttachments && mixed.ReferenceSelected && mixed.AttachmentSelected, "Legacy mixed entry did not preserve both categories");
            mixed.ReferenceSelected = false; var copy = mixed.Snapshot();
            Check(!copy.ReferenceSelected && copy.AttachmentSelected && copy.HasReference && copy.HasAttachments, "Independent choices were not preserved by snapshot");
            panel.RemoveAttachmentItem(entry, groups[0]);
            Check(entry.Files.Count == 2 && entry.AttachmentItems.All(g => g.DisplayPath != first + "\\"), "Removing a folder removed incorrect files");
            Check(!entry.Title.Contains("one.txt", StringComparison.Ordinal) && entry.Title == "附件：2 项", "Removing a folder left an obsolete title");
            Check(entry.Files.Any(file => file.Name == "loose.txt") && entry.Files.Any(file => file.FolderPath == second + "\\"), "Removing folder lost unrelated payloads");
            await panel.SaveAsync();
            Check(AiStorage.Load(project).Entries.Single().Files.Count == 2, "Removed folder returned after reload");
            var visualEntry = new AiEntry { EntryKind = "attachment", Files = [
                new AiAttachedFile("short/a.txt", "text/plain", "YQ==") { FolderPath = @"C:\目录" },
                new AiAttachedFile("long/b.txt", "text/plain", "Yg==") { FolderPath = @"C:\" + new string('长', 120) + "\\目标😀e\u0301文件夹" },
                new AiAttachedFile("huge/c.txt", "text/plain", "Yw==") { FolderPath = @"C:\" + "开头START" + new string('中', 80) + "末尾END😀e\u0301" } ] };
            Check(panel.AddEntry(visualEntry, false), "Could not add path layout fixtures");
            var popup = (Popup)panel.FindName("AttachmentsPopup"); popup.IsOpen = true;
            window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
            var surface = (FrameworkElement)popup.Child;
            void CheckPaths(string label)
            {
                var shot = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                shot.Render(surface); var initialPng = new PngBitmapEncoder(); initialPng.Frames.Add(BitmapFrame.Create(shot));
                using (var initialFile = File.Create(Path.Combine(output, "attachment-before-" + label + ".png"))) initialPng.Save(initialFile);
                var rows = Descendants<DockPanel>(surface).Where(row => row.DataContext is AiAttachmentItem).ToArray();
                foreach (var folder in visualEntry.AttachmentItems)
                {
                    var row = rows.Single(r => r.DataContext is AiAttachmentItem item && item.DisplayPath == folder.DisplayPath);
                    var pathPanel = Descendants<AttachmentPathPanel>(row).Single();
                    var prefix = (TextBlock)pathPanel.Children[0];
                    var middle = (TextBlock)pathPanel.Children[1];
                    var tailGrid = (Grid)pathPanel.Children[2];
                    var tail = (TextBlock)tailGrid.Children[0];
                    var slash = (TextBlock)pathPanel.Children[3];
                    var prefixLeft = prefix.TranslatePoint(new Point(), surface).X;
                    var middleLeft = middle.TranslatePoint(new Point(), surface).X;
                    var tailLeft = tailGrid.TranslatePoint(new Point(), surface).X;
                    var slashLeft = slash.TranslatePoint(new Point(), surface).X;
                    Check(Math.Abs(prefixLeft + prefix.ActualWidth - middleLeft) < 0.5 && Math.Abs(middleLeft + middle.ActualWidth - tailLeft) < 0.5, $"{label}: end of directory is detached");
                    Check(Math.Abs(tailLeft + tailGrid.ActualWidth - slashLeft) < 0.5 && slash.IsVisible && slash.ActualWidth > 0 && slashLeft + slash.ActualWidth <= surface.ActualWidth + 0.5, $"{label}: separator is not visible at the end");
                    var natural = new FormattedText(prefix.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(prefix.FontFamily, prefix.FontStyle, prefix.FontWeight, prefix.FontStretch), prefix.FontSize, Brushes.Black,
                        VisualTreeHelper.GetDpi(prefix).PixelsPerDip).WidthIncludingTrailingWhitespace;
                    if (folder.DisplayPath.Length < 20) Check(Math.Abs(prefix.ActualWidth - natural) < 2, $"{label}: short path is stretched");
                    else if (folder.DisplayPath.Contains("目标", StringComparison.Ordinal)) Check(prefix.ActualWidth < natural && tail.Text == "目标😀e\u0301文件夹", $"{label}: long path did not preserve the last folder");
                    else
                    {
                        Check(tailGrid.ActualWidth > 0 && slash.ActualWidth > 0 && pathPanel.IsLeafClipped, $"{label}: long last directory did not retain its trailing separator");
                        var tailRight = tail.TranslatePoint(new Point(tail.ActualWidth, 0), surface).X;
                        Check(tail.Text.EndsWith("末尾END😀e\u0301", StringComparison.Ordinal) && tailGrid.ClipToBounds &&
                            Math.Abs(tailRight - slashLeft) < 0.5 && ((TextBlock)tailGrid.Children[1]).IsVisible,
                            $"{label}: long last directory did not show its actual Unicode end");
                    }
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "attachment-path-" + label + ".png")); png.Save(file);
            }
            CheckPaths("wide");
            window.Width = 370; window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout(); CheckPaths("narrow");
            var longRow = Descendants<DockPanel>(surface).Single(r => r.DataContext is AiAttachmentItem item && item.IsFolder && item.DisplayPath.Contains("目标", StringComparison.Ordinal));
            var extreme = Descendants<AttachmentPathPanel>(longRow).Single();
            extreme.Width = 22; window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
            Check(((TextBlock)extreme.Children[3]).ActualWidth > 0, "Extreme narrow path lost the trailing separator");
            var fileRow = Descendants<DockPanel>(surface).Single(r => r.DataContext is AiAttachmentItem item && !item.IsFolder);
            var filePanel = Descendants<AttachmentPathPanel>(fileRow).Single();
            Check(((TextBlock)filePanel.Children[3]).Text == "", "A file gained a directory separator");
            popup.IsOpen = false;

            var sourceBitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 40, 100, 180, 255, 180, 100, 40, 255, 40, 180, 100, 255, 180, 40, 100, 255 }, 8);
            var sourceEncoder = new PngBitmapEncoder(); sourceEncoder.Frames.Add(BitmapFrame.Create(sourceBitmap));
            using var sourceBytes = new MemoryStream(); sourceEncoder.Save(sourceBytes);
            var imageBase64 = Convert.ToBase64String(sourceBytes.ToArray());
            var imagePath = Path.Combine(root, "blob.png"); File.WriteAllBytes(imagePath, sourceBytes.ToArray());
            Check(await panel.AddAttachmentsAsync([imagePath]), "Could not add image snapshot");
            var images = new AiEntry { EntryKind = "attachment", Files = [
                new("legacy.png", "image/png", imageBase64),
                new("broken.png", "image/png", "invalid base64"),
                new("reference.png", "application/vnd.texeed.file-reference", "") { ReferenceKind = "file", ReferencePath = imagePath } ] };
            Check(panel.AddEntry(images, false), "Could not add thumbnail fixtures");
            window.Width = 850; popup.IsOpen = true; window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
            var thumbnailRows = Descendants<DockPanel>((FrameworkElement)popup.Child).Where(row => row.DataContext is AiAttachmentItem).ToArray();
            DockPanel ImageRow(string name) => thumbnailRows.Single(row => row.DataContext is AiAttachmentItem item && item.DisplayPath == name);
            var blobThumb = Descendants<AttachmentThumbnail>(ImageRow("blob.png")).Single();
            var legacyThumb = Descendants<AttachmentThumbnail>(ImageRow("legacy.png")).Single();
            var brokenThumb = Descendants<AttachmentThumbnail>(ImageRow("broken.png")).Single();
            await Task.WhenAll(blobThumb.LoadTask, legacyThumb.LoadTask, brokenThumb.LoadTask); window.UpdateLayout();
            Check(blobThumb.IsVisible && blobThumb.Source is BitmapSource { IsFrozen: true }, "Blob thumbnail failed to load on background thread");
            Check(legacyThumb.IsVisible && legacyThumb.Source is BitmapSource { IsFrozen: true }, "Legacy Base64 thumbnail failed to load");
            Check(!brokenThumb.IsVisible && brokenThumb.Source is null && Descendants<AttachmentPathPanel>(ImageRow("broken.png")).Single().IsVisible, "Damaged image failed to fall back to filename");
            Check(!Descendants<AttachmentThumbnail>(ImageRow("reference.png")).Any() && !thumbnailRows.Where(row => ((AiAttachmentItem)row.DataContext).IsFolder).Any(row => Descendants<AttachmentThumbnail>(row).Any()), "Folder or path reference created an upload thumbnail");
            var imageScroll = (ScrollViewer)panel.FindName("AttachmentsScroll"); imageScroll.ScrollToEnd(); window.UpdateLayout(); await Task.Delay(100);
            var imageSurface = (FrameworkElement)popup.Child;
            var imageShot = new RenderTargetBitmap((int)Math.Ceiling(imageSurface.ActualWidth), (int)Math.Ceiling(imageSurface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            imageShot.Render(imageSurface); var imagePng = new PngBitmapEncoder(); imagePng.Frames.Add(BitmapFrame.Create(imageShot));
            using (var imageFile = File.Create(Path.Combine(output, "attachment-thumbnails.png"))) imagePng.Save(imageFile);
            popup.IsOpen = false; await Task.Delay(100);
            Check(blobThumb.Source is null && legacyThumb.Source is null, "Closing attachment popup retained decoded bitmaps");
            Console.WriteLine($"AI attachment grouping: {checks} checks passed.");
        }
        finally { window.Content = null; panel.Dispose(); window.Close(); }
    }
}
