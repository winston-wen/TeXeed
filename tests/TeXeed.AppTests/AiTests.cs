using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiAsync(MainWindow window, string output)
    {
        CheckAiAttachmentCompatibility();
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var lockedDestination = Path.Combine(output, "atomic-config.json");
        var pendingFile = lockedDestination + ".tmp";
        File.WriteAllText(lockedDestination, "old"); File.WriteAllText(pendingFile, "new");
        var held = new FileStream(lockedDestination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(70); held.Dispose(); });
        AtomicFile.Replace(pendingFile, lockedDestination); await release;
        Check(File.ReadAllText(lockedDestination) == "new" && !File.Exists(pendingFile), "Temporary Windows file lock broke atomic config persistence");
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var editor = (LatexEditor)window.FindName("LeftEditor");
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var menu = (MenuItem)window.FindName("AiMenuItem");
        Check(ReferenceEquals(((Menu)window.FindName("MainMenu")).Items.Cast<object>().Last(), menu), "AI must be the final main menu");
        Check(menu.Items.OfType<MenuItem>().Select(i=>i.Header.ToString()).SequenceEqual(new[]{"AI 对话","AI 设置","AI 历史"}), "AI submenu layout differs"); Invoke(window,"OpenAiTab"); window.UpdateLayout();
        var aiKey = TabWorkspace.AiConversationKey(ai.ConversationId);
        Check(tabs.Owner(aiKey) == TabSide.Right && ai.IsVisible, "AI button did not open right AI tab");
        Check(!tabs.Sources.Contains(aiKey), "AI tab treated as source file");
        var composer = (TextBox)ai.FindName("Composer");
        composer.Focus(); window.UpdateLayout();
        Check(composer.VerticalContentAlignment == VerticalAlignment.Top && composer.GetRectFromCharacterIndex(0).Top < 18, "Composer caret must begin near the top padding");
        using (var catalog = JsonDocument.Parse("{\"slug\":\"gpt-6.1-sol\",\"display_name\":\"Sol\",\"supported_reasoning_levels\":[{\"effort\":\"low\"},{\"effort\":\"high\"},{\"effort\":\"ultra\"}]}"))
        {
            var parsed = AiModel.FromCatalog(catalog.RootElement);
            Check(parsed.ReasoningEfforts.SequenceEqual(new[] { "low", "high" }), "Model efforts must follow the catalog and public Responses contract");
        }
        var strip = (EditorTabStrip)window.FindName("RightFileTabs");
        var aiTab = ((StackPanel)strip.FindName("Items")).Children.Cast<Border>().Single(b => (string)b.Tag == aiKey);
        var aiButtons = ((Grid)aiTab.Child).Children.Cast<Button>().ToArray();
        Check(aiButtons.Length == 2 && aiButtons[1].ToolTip?.ToString()?.Contains("关闭") == true, "AI tab should contain only title and close; settings belong in the chat header");
        Invoke(window,"OpenAiSettings"); window.UpdateLayout();
        Check(tabs.FocusedTab == TabWorkspace.AiSettingsKey && ai.Settings.IsVisible && !ai.IsVisible, "AI gear did not open separate settings tab");
        Check(!tabs.Sources.Contains(TabWorkspace.AiSettingsKey), "AI settings treated as source");
        Invoke(window, "OpenAiTab");
        Invoke(window, "MoveWorkspaceTab", aiKey, TabSide.Left, 0, false); window.UpdateLayout();
        Check(tabs.Owner(aiKey) == TabSide.Left && ai.IsVisible, "AI tab did not move left");

        editor.Select(0, 14); var text = editor.SelectedText;
        Invoke(window, "CollectSourceForAi", editor); window.UpdateLayout();
        var book = (AiNotebook)Field(ai, "_book")!;
        Check(book.Entries.Count == 1 && book.Entries[0].Sources.Single().Text == text, "Source snapshot differs from selected text");
        Check(editor.ContextMenu.Items.OfType<MenuItem>().Count() >= 12, "Editor context menu missing edit commands");
        book.Entries[0].Question = "解释这一行"; await ai.SaveAsync();
        var project = (TexProject)Field(window, "_project")!;
        var restored = AiStorage.Load(project.MainPath); Check(restored.Entries.Single().Question == "解释这一行", "Project AI notebook did not persist");
        var pasted = new AiEntry { Question = "解释图片" };
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 20, 40, 60, 255 }, 4);
        Check(AiAnnotationImages.Add(pasted, bitmap, out _) && pasted.Images.Count == 1, "Annotation clipboard image was not captured");
        var pasteBox = new TextBox(); var pasteChanges = 0; var pasteError = "";
        AiAnnotationImages.EnablePaste(pasteBox, pasted, () => pasteChanges++, message => pasteError = message, () => true, () => bitmap, () => ModifierKeys.Control);
        var pasteKey = CreateKey(window, Key.V, Keyboard.PreviewKeyDownEvent); pasteBox.RaiseEvent(pasteKey);
        Check(pasteKey.Handled && pasteChanges == 1 && pasted.Images.Count == 2 && pasteError.Length == 0, "Ctrl+V did not add the clipboard image to the annotation");
        var pastedSnapshot = pasted.Snapshot();
        pasted.Images.Clear();
        Check(pastedSnapshot.Images.Count == 2 && Convert.FromBase64String(pastedSnapshot.Images[0].ImageBase64).Take(4).SequenceEqual(new byte[] { 137, 80, 78, 71 }), "Sent annotation snapshot lost the image");
        var pastedInput = AiResponses.MakeInput([pastedSnapshot], []);
        Check(pastedInput.ToJsonString().Contains("data:image/png;base64,"), "Annotation image was not sent to AI");
        var imageRoundTrip = JsonSerializer.Deserialize<AiEntry>(JsonSerializer.Serialize(pastedSnapshot));
        Check(imageRoundTrip?.Images.Count == 2 && imageRoundTrip.Images[0].ImageBase64 == pastedSnapshot.Images[0].ImageBase64, "Annotation image did not survive serialization");
        var imageBook = new AiNotebook(); imageBook.Entries.Add(pastedSnapshot.Snapshot());
        var sentImageFixture = pastedSnapshot.Snapshot(); sentImageFixture.Id = Guid.NewGuid().ToString("N");
        imageBook.Turns.Add(new AiTurn("问题", "回复") { SentAnnotations = [sentImageFixture] });
        var imageProject = Path.Combine(output, "annotation-image-fixture.tex");
        AiStorage.Save(imageProject, imageBook.Snapshot());
        var imageReload = AiStorage.Load(imageProject);
        Check(imageReload.Entries.Single().Images.Count == 2 && imageReload.Entries.Single().Images[0].ImageBase64 == pastedSnapshot.Images[0].ImageBase64 && imageReload.Turns.Single().SentAnnotations.Single().Images.Count == 2, "Draft or sent image was lost when saving or restoring");
        var attachmentFolder = Path.Combine(output, "attachment-fixture"); Directory.CreateDirectory(attachmentFolder);
        File.WriteAllText(Path.Combine(attachmentFolder, "notes.tex"), "附件内容");
        File.WriteAllBytes(Path.Combine(attachmentFolder, "unsupported.bin"), [0, 1, 2]);
        Check(await ai.AddAttachmentsAsync([attachmentFolder]), "Folder attachment was not captured");
        var attached = book.Entries.Last();
        var folderReference = attached.Files.Single();
        Check(folderReference.ReferenceKind == "directory" && folderReference.ReferencePath == Path.GetFullPath(attachmentFolder) + Path.DirectorySeparatorChar &&
            folderReference.FolderPath == folderReference.ReferencePath && folderReference.Base64.Length == 0,
            "Folder selection must retain a directory reference rather than copying its children");
        var attachmentInput = AiResponses.MakeInput([attached.Snapshot()], []);
        var attachmentText = attachmentInput.ToJsonString();
        Check(!attachmentText.Contains("附件内容") && !attachmentText.Contains("data:application/vnd.texeed.directory-reference"),
            "Directory reference was sent as copied file content");
        var savedFolder = JsonSerializer.Deserialize<AiEntry>(JsonSerializer.Serialize(attached.Snapshot()))!;
        Check(savedFolder.Files.Single() == folderReference && savedFolder.AttachmentItems.Single().IsFolder,
            "Directory draft or its display path changed during serialization");
        var directorySnapshot = AiPanel.SnapshotSelectedParts(attached, false, true, 1);
        Check(directorySnapshot.Files.Single() == folderReference, "Sending lost the selected directory reference");
        var noDirectorySnapshot = AiPanel.SnapshotSelectedParts(attached, true, false, 1);
        Check(noDirectorySnapshot.Files.Count == 0, "An unselected directory entered the submitted snapshot");
        var liveProject = Path.Combine(output, "directory-reference-fixture.tex");
        var directoryBook = new AiNotebook(); directoryBook.Entries.Add(attached.Snapshot());
        directoryBook.Turns.Add(new AiTurn("问题", "回答") { Prompt = "问题", SentAnnotations = [directorySnapshot.Snapshot()] });
        directoryBook.Entries[0].Files[0] = folderReference with { ReferencePath = folderReference.ReferencePath + "changed" };
        AiStorage.Save(liveProject, directoryBook.Snapshot());
        var directoryReload = AiStorage.Load(liveProject);
        Check(directoryReload.Entries.Single().Files.Single().ReferencePath!.EndsWith("changed") &&
            directoryReload.Turns.Single().SentAnnotations.Single().Files.Single() == folderReference,
            "Recovery consumed an edited directory draft or altered the sent reference");
        var sentAttachment = attached.Snapshot();
        var reconciliation = new AiConversation { Entries = [attached.Snapshot()], Turns = [new AiTurn("问题", "回答") { Prompt = "问题", SentAnnotations = [sentAttachment] }] };
        reconciliation.Entries[0].Files.Add(new AiAttachedFile("new.txt", "text/plain", Convert.ToBase64String(Encoding.UTF8.GetBytes("new"))));
        reconciliation.ReconcileSentAnnotations();
        Check(reconciliation.Entries.Count == 1, "Recovery discarded a changed attachment draft");
        attached.Files.Add(new AiAttachedFile("loose.txt", "text/plain", Convert.ToBase64String(Encoding.UTF8.GetBytes("second"))));
        Invoke(ai, "Rebind");
        Check(((Button)ai.FindName("EntriesButton")).Content.ToString() == "引用 · 1" && ((Button)ai.FindName("AttachmentsButton")).Content.ToString() == "附件 · 2", "Reference and attachment counts were mixed");
        var attachmentPopup = (System.Windows.Controls.Primitives.Popup)ai.FindName("AttachmentsPopup");
        attachmentPopup.IsOpen = true; window.UpdateLayout(); await Task.Delay(100);
        var fileButtons = Descendants<Button>(attachmentPopup.Child).Where(b => Equals(b.ToolTip, "移除此附件")).ToArray();
        Check(fileButtons.Length == 2 && Descendants<TextBlock>(attachmentPopup.Child).Any(t => t.Text.EndsWith("attachment-fixture")) && Descendants<TextBlock>(attachmentPopup.Child).Any(t => t.Text == "\\") && !Descendants<TextBlock>(attachmentPopup.Child).Any(t => t.Text.Contains("notes.tex")), "Folder attachment did not collapse to its directory path");
        var popupVisual = (FrameworkElement)attachmentPopup.Child;
        var popupBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(popupVisual.ActualWidth), (int)Math.Ceiling(popupVisual.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        popupBitmap.Render(popupVisual);
        var popupPng = new System.Windows.Media.Imaging.PngBitmapEncoder(); popupPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(popupBitmap));
        using (var popupFile = File.Create(Path.Combine(output, "attachment-list.png"))) popupPng.Save(popupFile);
        fileButtons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        attachmentPopup.IsOpen = false;
        Check(attached.Files.Count == 1 && attached.Files[0].Name == "loose.txt" && book.Entries.Contains(attached), "Removing one folder removed the wrong attachment or whole group");
        var filesAfterRemoval = new AiAttachmentCatalog([], [attached.Snapshot()]).Execute("list_attachments", "{\"offset\":0,\"limit\":30}");
        Check(filesAfterRemoval.Contains("loose.txt") && !filesAfterRemoval.Contains("notes.tex"), "Removed attachment still entered the request");
        book.Entries.Remove(attached);
        var referencedPath = Path.Combine(attachmentFolder, "notes.tex");
        Check(ai.AddFileReferences([referencedPath]), "Selecting a file path reference failed");
        var referencedEntry = book.Entries.Last();
        var referencedFile = referencedEntry.Files.Single();
        var referencedItem = referencedEntry.AttachmentItems.Single();
        Check(referencedFile.ReferenceKind == "file" && referencedFile.ReferencePath == Path.GetFullPath(referencedPath) &&
            referencedFile.Base64.Length == 0 && referencedItem.DisplayPath == Path.GetFullPath(referencedPath) &&
            referencedItem.DisplayPrefix + referencedItem.DisplayMiddleSeparator + referencedItem.DisplayLeaf == referencedItem.DisplayPath,
            "File path reference did not preserve its full path for display and later reads");
        var referenceRoundTrip = JsonSerializer.Deserialize<AiEntry>(JsonSerializer.Serialize(referencedEntry.Snapshot()))!;
        Check(referenceRoundTrip.Files.Single() == referencedFile && AiPanel.SnapshotSelectedParts(referenceRoundTrip, false, true, 1).Files.Single() == referencedFile,
            "File path reference was lost from saved draft or sent snapshot");
        book.Entries.Remove(referencedEntry);
        Check(await ai.AddAttachmentsAsync([referencedPath]), "Uploading a single file failed");
        var uploadedEntry = book.Entries.Last();
        var uploadedFile = uploadedEntry.Files.Single();
        Check(uploadedFile.ReferenceKind is null && uploadedFile.BlobId is not null && uploadedFile.Base64.Length == 0 &&
            Encoding.UTF8.GetString(AiAttachmentBlobStore.ReadAllBytes(uploadedFile.BlobId!)) == "附件内容",
            "Uploading a file must preserve its byte snapshot separately from file references");
        File.WriteAllText(referencedPath, "修改后的磁盘文件");
        var uploadedBook = new AiNotebook(); uploadedBook.Entries.Add(uploadedEntry.Snapshot());
        var uploadedTurnEntry = uploadedEntry.Snapshot(); uploadedTurnEntry.Id = Guid.NewGuid().ToString("N");
        uploadedBook.Turns.Add(new AiTurn("问题", "回答") { Prompt = "问题", SentAnnotations = [uploadedTurnEntry] });
        var uploadedProject = Path.Combine(output, "uploaded-blob-fixture.tex"); AiStorage.Save(uploadedProject, uploadedBook.Snapshot());
        var reloadedUpload = AiStorage.Load(uploadedProject);
        Check(reloadedUpload.Entries.Single().Files.Single().BlobId == uploadedFile.BlobId &&
            reloadedUpload.Turns.Single().SentAnnotations.Single().Files.Single().BlobId == uploadedFile.BlobId &&
            Encoding.UTF8.GetString(AiAttachmentBlobStore.ReadAllBytes(uploadedFile.BlobId!)) == "附件内容",
            "Uploaded draft or sent snapshot changed after reload or source modification");
        File.WriteAllText(referencedPath, "附件内容");
        book.Entries.Remove(uploadedEntry);
        var repeatedPaths = Enumerable.Repeat(referencedPath, 51).ToArray();
        Check(ai.AddFileReferences(repeatedPaths) && book.Entries.Last().Files.Count == 51,
            "Referencing more than 50 files was blocked by a local count limit");
        book.Entries.Remove(book.Entries.Last());
        Check(await ai.AddAttachmentsAsync(repeatedPaths) && book.Entries.Last().Files.Count == 51 &&
            book.Entries.Last().Files.Select(f => f.BlobId).Distinct().Count() == 1,
            "Uploading more than 50 files failed or lost shared snapshot identity");
        book.Entries.Remove(book.Entries.Last());
        var largePath = Path.Combine(output, "large-upload.txt");
        using (var large = new FileStream(largePath, FileMode.Create, FileAccess.Write)) large.SetLength(9L * 1024 * 1024);
        Check(await ai.AddAttachmentsAsync([largePath, largePath]) && book.Entries.Last().Files.Count == 2,
            "Uploading a file larger than 8 MiB or a batch larger than 16 MiB was blocked locally");
        book.Entries.Remove(book.Entries.Last());
        var unsupportedPath = Path.Combine(attachmentFolder, "unsupported.bin");
        var emptyPath = Path.Combine(output, "empty-upload.txt"); File.WriteAllBytes(emptyPath, []);
        var knownTextPath = Path.Combine(output, "additional-text.ps1"); File.WriteAllText(knownTextPath, "Write-Output ok");
        Check(await ai.AddAttachmentsAsync([unsupportedPath, emptyPath, knownTextPath]) &&
            book.Entries.Last().Files.Single().MimeType == "text/plain" &&
            ((TextBlock)ai.FindName("StatusText")).Text.Contains("不支持的文件") && ((TextBlock)ai.FindName("StatusText")).Text.Contains("空文件"),
            "Supported text was lost or unsupported/empty uploads were not reported");
        book.Entries.Remove(book.Entries.Last());
        var corruptPath = Path.Combine(output, "corrupt-blob-fixture.txt"); File.WriteAllText(corruptPath, Guid.NewGuid().ToString("N"));
        var (corruptId, _) = AiAttachmentBlobStore.Store(corruptPath);
        var corruptBlobPath = Path.Combine(AiStorage.Home, "attachments", corruptId);
        File.WriteAllText(corruptBlobPath, "corrupted");
        var invalidBlob = false;
        try { using var ignored = AiAttachmentBlobStore.OpenRead(corruptId); }
        catch (InvalidDataException) { invalidBlob = true; }
        Check(invalidBlob, "A corrupted attachment snapshot was read without checking its content hash");
        File.Delete(corruptBlobPath);
        var missingBlob = false;
        try { using var ignored = AiAttachmentBlobStore.OpenRead(corruptId); }
        catch (FileNotFoundException) { missingBlob = true; }
        Check(missingBlob, "A missing attachment snapshot was silently replaced");
        var insufficientMemory = false;
        try { AiAttachmentBlobStore.EnsureBufferCapacity(1024 * 1024, 2 * 1024 * 1024); }
        catch (IOException ex) { insufficientMemory = ex.Message.Contains("附件仍已保留"); }
        Check(insufficientMemory, "Preparing a large media snapshot did not fail safely under memory pressure");
        var emptyFolder = Path.Combine(output, "empty-directory-fixture"); Directory.CreateDirectory(emptyFolder);
        Check(await ai.AddAttachmentsAsync([emptyFolder]), "An empty directory reference could not be added");
        Check(book.Entries.Last().Files.Single().ReferenceKind == "directory", "Empty directory was not stored as a reference");
        book.Entries.Remove(book.Entries.Last());
        var legacyFile = JsonSerializer.Deserialize<AiAttachedFile>("{\"Name\":\"old-folder/old.txt\",\"MimeType\":\"text/plain\",\"Base64\":\"b2xk\"}")!;
        var legacyEntry = new AiEntry { EntryKind = "attachment", Files = [legacyFile, folderReference] };
        var legacyBook = new AiNotebook(); legacyBook.Entries.Add(legacyEntry);
        var legacyDirectoryProject = Path.Combine(output, "directory-reference-legacy.tex");
        AiStorage.Save(legacyDirectoryProject, legacyBook.Snapshot());
        var legacyReload = AiStorage.Load(legacyDirectoryProject).Entries.Single();
        var legacySent = AiPanel.SnapshotSelectedParts(legacyReload, false, true, 1);
        Check(legacyReload.Files[0].ReferenceKind is null && legacyReload.Files[0].ReferencePath is null &&
            Encoding.UTF8.GetString(Convert.FromBase64String(legacyReload.Files[0].Base64)) == "old" &&
            legacySent.Files[0] == legacyFile && legacySent.Files[1] == folderReference && legacyReload.AttachmentItems.Count == 2,
            "Mixing old folder snapshots with new directory references changed the legacy bytes or sending state");
        var mixedDirectory = new AiEntry { Question = "保留目录", AnswerAnchor = new("t", "s", 0, 2, "原文", "rev"), Files = [folderReference] };
        var sentTextOnly = AiPanel.SnapshotSelectedParts(mixedDirectory, true, false, 1);
        var mixedDirectoryBook = new AiNotebook(); mixedDirectoryBook.Entries.Add(mixedDirectory);
        mixedDirectoryBook.Turns.Add(new AiTurn("问题", "回答") { Prompt = "问题", SentAnnotations = [sentTextOnly] });
        var mixedDirectoryProject = Path.Combine(output, "directory-reference-mixed.tex");
        AiStorage.Save(mixedDirectoryProject, mixedDirectoryBook.Snapshot());
        var mixedDirectoryReload = AiStorage.Load(mixedDirectoryProject).Entries.Single();
        Check(!mixedDirectoryReload.HasReference && mixedDirectoryReload.Files.Single() == folderReference,
            "Recovery removed an unselected directory when accepting a separate text reference");
        Invoke(ai, "Rebind");
        var mixed = new AiEntry { Question = "引用评论", AnswerAnchor = new("turn", "segment", 0, 2, "原文", "rev"), Files = [new AiAttachedFile("sample.txt", "text/plain", Convert.ToBase64String(Encoding.UTF8.GetBytes("file")))] };
        mixed.Images.Add(new AiPastedImage("comment-image"));
        mixed.ReferenceSelected = true; mixed.AttachmentSelected = false;
        var referenceOnly = AiPanel.SnapshotSelectedParts(mixed, true, false, 1);
        Check(referenceOnly.AnswerAnchor is not null && referenceOnly.Images.Count == 1 && referenceOnly.Files.Count == 0, "Reference-only submission included attachments or lost comment images");
        var mixedBook = new AiNotebook(); mixedBook.Entries.Add(mixed);
        ai.ConsumeSelectedParts(mixedBook, mixed, true, false);
        Check(mixedBook.Entries.Count == 1 && mixed.HasAttachments && !mixed.HasReference && mixed.Files.Count == 1, "Submitting reference consumed the unselected attachment");
        mixedBook.Turns.Add(new AiTurn("问题", "回答") { Prompt = "问题", SentAnnotations = [referenceOnly] });
        var mixedPath = Path.Combine(output, "split-selection.tex"); AiStorage.Save(mixedPath, mixedBook.Snapshot());
        Check(AiStorage.Load(mixedPath).Entries.Single().Files.Count == 1, "Recovery discarded the unselected attachment");
        var mixedReverse = new AiEntry { Question = "保留的评论", AnswerAnchor = new("turn", "segment", 0, 2, "原文", "rev"), Files = [new AiAttachedFile("sample.txt", "text/plain", "ZmlsZQ==")] };
        mixedReverse.Images.Add(new AiPastedImage("comment-image"));
        var attachmentOnly = AiPanel.SnapshotSelectedParts(mixedReverse, false, true, 1);
        Check(attachmentOnly.AnswerAnchor is null && attachmentOnly.Images.Count == 0 && attachmentOnly.Files.Count == 1 && attachmentOnly.Question == "", "Attachment-only submission included reference or its comment image");
        var reverseBook = new AiNotebook(); reverseBook.Entries.Add(mixedReverse); ai.ConsumeSelectedParts(reverseBook, mixedReverse, false, true);
        Check(reverseBook.Entries.Count == 1 && mixedReverse.AnswerAnchor is not null && mixedReverse.Question == "保留的评论" && mixedReverse.Images.Count == 1 && mixedReverse.Files.Count == 0, "Submitting attachment consumed the unselected reference");
        foreach (var (sendReference, changedSent) in new[] { (true, false), (false, false), (true, true), (false, true) })
        {
            var original = new AiEntry { Question = "原引用", AnswerAnchor = new("t", "s", 0, 2, "原文", "rev"), Files = [new AiAttachedFile("file.txt", "text/plain", "Zg==")] };
            original.ReferenceSelected = sendReference; original.AttachmentSelected = !sendReference;
            var checkpoint = AiPanel.SnapshotSelectedParts(original, sendReference, !sendReference, 1);
            if (changedSent) { if (sendReference) original.Question = "用户修改的引用"; else original.Files.Add(new AiAttachedFile("new.txt", "text/plain", "bg==")); }
            else { if (sendReference) original.Files.Add(new AiAttachedFile("new.txt", "text/plain", "bg==")); else original.Question = "用户修改的引用"; }
            var crashBook = new AiNotebook(); crashBook.Entries.Add(original); crashBook.Turns.Add(new AiTurn("处理", "回答") { Prompt = "处理", SentAnnotations = [checkpoint] });
            var crashPath = Path.Combine(output, $"partial-checkpoint-{sendReference}-{changedSent}.tex"); AiStorage.Save(crashPath, crashBook);
            var recovered = AiStorage.Load(crashPath).Entries.Single();
            Check(changedSent ? recovered.HasReference && recovered.HasAttachments : sendReference ? !recovered.HasReference && recovered.Files.Count == 2 : recovered.HasReference && !recovered.HasAttachments && recovered.Question == "用户修改的引用", "Partial checkpoint recovery lost an edit or left an accepted part pending");
        }
        var mixedUi = new AiEntry { Question = "混合引用", AnswerAnchor = new("t", "s", 0, 2, "原文", "rev"), Files = [new AiAttachedFile("file.txt", "text/plain", "Zg==")] };
        book.Entries.Add(mixedUi); Invoke(ai, "Rebind");
        Check(((ItemsControl)ai.FindName("Entries")).Items.Contains(mixedUi) && ((ItemsControl)ai.FindName("Attachments")).Items.Contains(mixedUi), "Legacy mixed entry did not appear in both separate lists");
        mixedUi.ReferenceSelected = false; mixedUi.AttachmentSelected = true;
        Check(!mixedUi.ReferenceSelected && mixedUi.AttachmentSelected, "Reference and attachment checkboxes are not independent");
        Invoke(ai, "RemoveReference", mixedUi);
        Check(book.Entries.Contains(mixedUi) && mixedUi.Files.Count == 1 && mixedUi.AnswerAnchor is null, "Removing a reference deleted its attachment");
        book.Entries.Remove(mixedUi); Invoke(ai, "Rebind");
        using var extraFixture = JsonDocument.Parse("{\"extension\":\"keep\"}");
        var oldImage = new AiEntry { Title = "粘贴的图片", Question = "图片说明", Images = new(pastedSnapshot.Images), Files = [new AiAttachedFile("legacy.txt", "text/plain", "Zg==")], AdditionalData = new() { ["future"] = extraFixture.RootElement.Clone() } };
        oldImage.ReferenceSelected = true; oldImage.AttachmentSelected = false;
        book.Entries.Add(oldImage); Invoke(ai, "Rebind");
        var referencePopup = (System.Windows.Controls.Primitives.Popup)ai.FindName("EntriesPopup"); referencePopup.IsOpen = true; window.UpdateLayout(); await Task.Delay(100);
        var oldImageReference = Descendants<ContentControl>(referencePopup.Child).Single(c => ReferenceEquals(c.DataContext, oldImage) && Equals(c.Tag, "ReferenceImages"));
        Check(oldImageReference.Visibility == Visibility.Collapsed && oldImageReference.Content is null, "Legacy composer image was repeated in the reference list");
        referencePopup.IsOpen = false;
        Check(ai.AddReferenceImage(oldImage, bitmap, out var pasteSplitError, out var separatedImage) && separatedImage && pasteSplitError.Length == 0, "Pasting a comment image did not separate legacy attachment images");
        var oldAttachment = book.Entries[book.Entries.IndexOf(oldImage) + 1];
        Check(oldImage.HasReference && !oldImage.HasAttachments && oldImage.Images.Count == 1 && oldImage.Question == "图片说明" &&
            oldAttachment.HasAttachments && !oldAttachment.HasReference && oldAttachment.Images.Count == 2 && oldAttachment.Files.Single().Name == "legacy.txt" &&
            !oldAttachment.AttachmentSelected && oldImage.ReferenceSelected && oldImage.AdditionalData?.ContainsKey("future") == true && oldAttachment.AdditionalData?.ContainsKey("future") == true,
            "Legacy image separation lost order, selection, comment or extension data");
        var oldImageProject = Path.Combine(output, "old-image-split.tex"); AiStorage.Save(oldImageProject, book.Snapshot());
        var reloadedOldImages = AiStorage.Load(oldImageProject).Entries.Where(e => e.Id == oldImage.Id || e.Id == oldAttachment.Id).ToArray();
        Check(reloadedOldImages.Length == 2 && reloadedOldImages.Single(e => e.Id == oldImage.Id).Images.Count == 1 && reloadedOldImages.Single(e => e.Id == oldAttachment.Id).Images.Count == 2, "Separated legacy image draft did not survive reload");
        book.Entries.Remove(oldImage); book.Entries.Remove(oldAttachment); Invoke(ai, "Rebind");
        Check(AiStorage.NotebookPath(project.MainPath) != AiStorage.NotebookPath(project.MainPath + "x"), "Project notebook isolation failed");
        var credentials = new AiCredentials { ApiKey = "test-key-never-send", UseApiKey = true }; credentials.Save();
        Check(AiCredentials.Load().ApiKey == credentials.ApiKey && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AiStorage.Home, "credentials.dpapi"))).Contains(credentials.ApiKey), "DPAPI secret storage failed");
        var identity = MakeTestIdentity();
        Check(ChatGptLogin.ValidateIdentity(identity.Jwt, "test-client", "test-nonce", identity.Keys).Subject == "test-user", "Valid OIDC identity rejected");
        foreach (var (client, nonce) in new[] { ("bad-client", "test-nonce"), ("test-client", "bad-nonce") })
        { var rejected = false; try { ChatGptLogin.ValidateIdentity(identity.Jwt, client, nonce, identity.Keys); } catch (CryptographicException) { rejected = true; } Check(rejected, "OIDC binding not checked"); }
        var sigRejected = false; try { ChatGptLogin.ValidateIdentity(identity.Jwt[..^10] + "AAAAAAAAAA", "test-client", "test-nonce", identity.Keys); } catch (CryptographicException) { sigRejected = true; } Check(sigRejected, "Invalid signature accepted");
        var files = new[] { new ProjectFile("C:/project/main.tex", "main.tex", ProjectFileKind.Tex, "first\nA compact proof.\nlast") };
        Check(AiResponses.ExecuteTool("search_source", "{\"query\":\"compact proof\"}", files).Contains("main.tex:2"), "Source search lacks location");
        Check(AiResponses.ExecuteTool("read_source", "{\"file\":\"../../secrets\",\"start_line\":1,\"line_count\":20}", files).Contains("不在"), "Tool escaped project snapshot");
        using var handler = new AiFakeHandler(); using var connection = new AiConnection(handler);
        var answer = new StringBuilder();
        await new AiResponses(connection).RunAsync("test-model", restored.Entries, [], files, x => answer.Append(x), _ => { }, CancellationToken.None);
        Check(answer.ToString() == "定位到 main.tex:2。" && handler.Requests == 2, "Streaming tool roundtrip failed");
        Check(handler.SawToolOutput && handler.SawContract, "Responses request broke OAuth-compatible streaming contract");
        using (var branchHandler = new AiFakeHandler())
        using (var branchConnection = new AiConnection(branchHandler))
        {
            await new AiResponses(branchConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, CancellationToken.None, isBranch: true);
            Check(branchHandler.LastInstructions?.Contains("不要假定用户已经阅读") == true, "Branch request omitted the mid-reading instruction");
        }
        using (var eventHandler = new AiFakeHandler { ToolsInEvents = true })
        using (var eventConnection = new AiConnection(eventHandler))
        {
            var streamed = new StringBuilder();
            await new AiResponses(eventConnection).RunAsync("test-model", restored.Entries, [], files, x => streamed.Append(x), _ => { }, CancellationToken.None);
            Check(eventHandler.Requests == 2 && eventHandler.SawToolOutput && streamed.ToString().Contains("main.tex:2"), "Terminal output omission lost item.done tool call");
        }
        using (var emptyConnection = new AiConnection(new AiFakeHandler { Empty = true }))
        {
            var rejected = false;
            try { await new AiResponses(emptyConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Empty completed response must not mark a question answered");
        }
        using var brokenConnection = new AiConnection(new AiFakeHandler { Broken = true }); var failed = false;
        using (var cancelBroken = new CancellationTokenSource(200))
            try { await new AiResponses(brokenConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, cancelBroken.Token); } catch (OperationCanceledException) { failed = true; }
        Check(failed, "Interrupted stream treated as success");
        await CheckAiStreamRecoveryAsync(Check);
        var batchHandler = new AiFakeHandler();
        using (var batchPanel = new AiPanel(new AiConnection(batchHandler)))
        {
            await batchPanel.SetProjectAsync(Path.Combine(output, "batch-fixture.tex")); batchPanel.ProjectFiles = () => files;
            var modelBox = (ComboBox)batchPanel.Settings.FindName("Models");
            var testModels = new[] { new AiModel("test-model", "测试模型") { ReasoningEfforts = ["low", "high"] }, new AiModel("second-model", "第二个模型") { ReasoningEfforts = ["low", "high"] } };
            modelBox.ItemsSource = testModels; modelBox.SelectedIndex = 0;
            await batchPanel.Settings.SaveSelectionAsync(CancellationToken.None);
            var chatModel = (ComboBox)batchPanel.FindName("ModelPicker");
            Check(chatModel.Items.Count == 2 && (chatModel.SelectedItem as AiModel)?.Id == "test-model", "Chat model picker did not mirror settings");
            chatModel.SelectedItem = testModels[1];
            await batchPanel.Settings.SaveSelectionAsync(CancellationToken.None);
            var chatEffort = (ComboBox)batchPanel.FindName("EffortPicker");
            chatEffort.SelectedItem = chatEffort.Items.Cast<AiEffort>().Single(e => e.Value == "high");
            await batchPanel.Settings.SaveSelectionAsync(CancellationToken.None);
            Check(batchPanel.Settings.SelectedModel?.Id == "second-model" && batchPanel.Settings.SelectedEffort == "high", "Chat choices did not update settings");
            Check(AiCredentials.Load().Model == "second-model" && AiCredentials.Load().ReasoningEffort == "high", "Selected model/effort did not persist");
            var first = new AiEntry { Question = "第一问" }; var second = new AiEntry { Question = "第二问" }; var skip = new AiEntry { Question = "不发送", Selected = false };
            batchPanel.AddEntry(first); batchPanel.AddEntry(second); batchPanel.AddEntry(skip); await batchPanel.SubmitAsync();
            Check(first.State == "已完成" && second.State == "已完成" && skip.State == "待提交", "Batch submission did not honor selected entries");
            var batchBook = (AiNotebook)Field(batchPanel, "_book")!;
            Check(batchBook.Turns.Count == 1 && batchBook.Turns[0].Question.Contains("第一问") && batchBook.Turns[0].Question.Contains("第二问") && !batchBook.Turns[0].Question.Contains("不发送"), "Batch history mixed unselected questions");
            Check(batchHandler.LastModel == "second-model" && batchHandler.LastEffort == "high", "Actual Responses request ignored model or reasoning selection");
            var firstId = batchBook.Active.Id;
            var firstTurnId = batchBook.Turns[0].Id;
            ((TextBox)batchPanel.FindName("Composer")).Text = "草稿 A";
            await batchPanel.NewConversationAsync();
            var secondId = batchBook.Active.Id;
            Check(secondId != firstId && batchBook.Turns.Count == 0 && batchBook.Entries.Count == 0, "New conversation inherited messages/context");
            ((TextBox)batchPanel.FindName("Composer")).Text = "草稿 B";
            batchPanel.AddEntry(new AiEntry { Title = "B 的圈点" });
            await batchPanel.SelectConversationAsync(firstId);
            Check(((TextBox)batchPanel.FindName("Composer")).Text == "草稿 A" && batchBook.Entries.Count == 1, "Switching conversation lost its draft/context");
            Check(AiHistoryPage.Search(batchBook, "第一问").Single().Id == firstId, "History search did not find question text");
            batchBook.Turns.Add(new("问题 C", "可搜索的回答关键词"));
            Check(AiHistoryPage.Search(batchBook, "回答关键词").Single().Id == firstId, "History search did not find answer text");
            await batchPanel.DeleteTurnAsync(firstTurnId);
            Check(!batchBook.Turns.Any(t => t.Id == firstTurnId) && batchBook.Turns.Count == 1, "Deleting a message removed the wrong turns");
            await batchPanel.DeleteConversationAsync(firstId);
            Check(batchBook.Active.Id == secondId && batchBook.Conversations.Count == 1 && ((TextBox)batchPanel.FindName("Composer")).Text == "草稿 B", "Deleting active conversation did not restore another conversation");
            var savedHistory = AiStorage.Load(Path.Combine(output, "batch-fixture.tex"));
            Check(savedHistory.Conversations.Count == 1 && savedHistory.Active.Id == secondId && savedHistory.Draft == "草稿 B", "Conversation deletion did not persist");
            await batchPanel.DeleteConversationAsync(secondId);
            Check(batchBook.Conversations.Count == 1 && batchBook.Turns.Count == 0 && batchBook.Active.Id != secondId, "Deleting last conversation must yield a fresh empty chat");
        }
        foreach (var (sendReference, fail) in new[] { (true, false), (false, true) })
        {
            var mixedProject = Path.Combine(output, fail ? "partial-failure.tex" : "partial-success.tex");
            using var partialPanel = new AiPanel(new AiConnection(new AiFakeHandler { ProgramFailure = fail }));
            await partialPanel.SetProjectAsync(mixedProject); partialPanel.ProjectFiles = () => files;
            var models = (ComboBox)partialPanel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("test-model", "测试模型") }; models.SelectedIndex = 0;
            var partial = new AiEntry { Question = "引用的说明", AnswerAnchor = new("t", "s", 0, 2, "原文", "rev"), Files = [new AiAttachedFile("partial.txt", "text/plain", "ZmlsZQ==")] };
            partial.ReferenceSelected = sendReference; partial.AttachmentSelected = !sendReference;
            partialPanel.AddEntry(partial); ((TextBox)partialPanel.FindName("Composer")).Text = "处理所选内容";
            await partialPanel.SubmitAsync();
            var reloaded = AiStorage.Load(mixedProject); var sent = reloaded.Turns.Single().SentAnnotations.Single(); var pending = reloaded.Entries.Single();
            Check(sendReference ? sent.AnswerAnchor is not null && sent.Files.Count == 0 && pending.AnswerAnchor is null && pending.Files.Count == 1 : sent.AnswerAnchor is null && sent.Files.Count == 1 && pending.AnswerAnchor is not null && pending.Files.Count == 0, "Partial submission or reload consumed the unselected part");
            Check(reloaded.Turns.Single().IsIncomplete == fail, "Partial submission failure state was not preserved");
        }
        var legacyProject = Path.Combine(output, "legacy-ai.tex");
        var legacyPath = AiStorage.NotebookPath(legacyProject); Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { Entries = restored.Entries, Turns = new[] { new { Question = "[a6eb2178] 原来的问题", Answer = "原来的回答" } }, Draft = "原来的草稿" }));
        var migrated = AiStorage.Load(legacyProject);
        Check(migrated.Conversations.Count == 1 && migrated.Turns.Single().Answer == "原来的回答" && migrated.Draft == "原来的草稿" && migrated.Entries.Count == 1, "Legacy conversation migration lost data");
        Check(migrated.Turns.Single().DisplayQuestion == "原来的问题", "Legacy internal ID prefix leaked into displayed message");
        var emptyLegacy = new AiTurn("旧的空回答问题", "");
        Check(emptyLegacy.DisplayAnswer.Contains("未保存回答"), "Legacy empty answer is not explained to the user");
        var nextInput = AiResponses.MakeInput([new AiEntry { Question = "新的问题" }], [emptyLegacy]);
        Check(nextInput.Count == 1 && !nextInput.ToJsonString().Contains("旧的空回答问题"), "Legacy empty replies polluted the next request context");
        CheckAiContextBudget(Check);
        AiStorage.Save(legacyProject, migrated.Snapshot());
        using (var migratedJson = JsonDocument.Parse(File.ReadAllText(legacyPath)))
            Check(migratedJson.RootElement.TryGetProperty("Conversations", out _) && !migratedJson.RootElement.TryGetProperty("Entries", out _), "History schema duplicates media at notebook root");
        using (var failedPanel = new AiPanel(new AiConnection(new AiFakeHandler { ProgramFailure = true })))
        {
            await failedPanel.SetProjectAsync(Path.Combine(output, "failure-fixture.tex")); failedPanel.ProjectFiles = () => files;
            var models = (ComboBox)failedPanel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("test-model", "测试模型") }; models.SelectedIndex = 0;
            var entry = new AiEntry { Question = "保留待重试" }; failedPanel.AddEntry(entry); await failedPanel.SubmitAsync();
            Check(entry.Selected && entry.State == "未完成，可重试", "Failed batch lost selection or marked successful");
            var failedBook = AiStorage.Load(Path.Combine(output, "failure-fixture.tex"));
            Check(failedPanel.AnnotationEntries.Count == 0 && failedBook.Entries.Count == 0 && failedBook.Turns.Single().SentAnnotations.Single().Id == entry.Id, "Failed response left an already submitted circle in the composer or lost its snapshot");
        }
        var switched = false;
        using (var changedConnection = new AiConnection(new AiFakeHandler { SwitchAccount = true }))
        {
            try { await new AiResponses(changedConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, CancellationToken.None); }
            catch (InvalidOperationException) { switched = true; }
        }
        Check(switched, "Mid-request credential switch was not rejected"); credentials.Save();
        var crop = new PdfPageCrop(20, 400, [new(50, 100), new(300, 350)]);
        var bands = PdfPreview.SelectionBands(new Rect(0, 20, 200, 60), new Size(400, 100), new Size(600, 800), crop);
        Check(bands.Count == 2 && bands[0].Top == 70 && bands[0].Height == 30 && bands[1].Top == 300 && bands[1].Height == 30, "Compressed PDF gap incorrectly included in selection");
        var preview = (PdfPreview)window.FindName("Preview"); Invoke(window, "SelectWorkspaceTab", tabs.Owner(TabWorkspace.PreviewKey)!.Value, TabWorkspace.PreviewKey);
        var pdf = Path.Combine(output, "ai-preview.pdf"); WriteThemePdf(pdf); preview.SetCropOptions(false, false); await preview.LoadAsync(pdf); window.UpdateLayout();
        var pdfMark = preview.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "AI 圈点"));
        pdfMark.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(preview.IsAiSelecting && !editor.IsAiMarking, "PDF menu did not start preview-only AI marking");
        var layouts = ((System.Collections.IEnumerable)Field(preview, "_layouts")!).Cast<object>().ToArray();
        var top0 = (double)layouts[0].GetType().GetProperty("Top")!.GetValue(layouts[0])!; var top1 = (double)layouts[1].GetType().GetProperty("Top")!.GetValue(layouts[1])!;
        var left0 = (double)layouts[0].GetType().GetProperty("Left")!.GetValue(layouts[0])!;
        var selection = new Rect(left0 + 30, top0 + 50, 150, top1 - top0 + 30);
        typeof(PdfPreview).GetField("_aiSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(preview, selection);
        await preview.ConfirmAiSelectionAsync(); window.UpdateLayout();
        var pdfEntry = book.Entries.Last();
        Check(pdfEntry.PdfBlocks.Select(b => b.Page).Distinct().SequenceEqual(new[] { 1, 2 }) && pdfEntry.PdfRevision.Length == 64, "Cross-page PDF capture did not preserve pages/revision");
        Check(pdfEntry.PdfBlocks.All(b => b.Width > 0 && b.Height > 0 && Convert.FromBase64String(b.ImageBase64).Take(4).SequenceEqual(new byte[] { 137, 80, 78, 71 })), "PDF screenshots invalid");
        Check(tabs.FocusedTab == TabWorkspace.PreviewKey, "PDF marking must keep the original preview visible");
        Check(preview.IsAiSelecting && !editor.IsAiMarking, "PDF marking must remain active without enabling the source editor mode");
        var point = editor.CreateAiMark(0, 0, false);
        Check(point.IsPoint && point.Offset == 0 && point.Length == 0, "Single click must create a source point");
        var sentence = editor.CreateAiMark(editor.Text.IndexOf("Hello", StringComparison.Ordinal), editor.Text.IndexOf("Hello", StringComparison.Ordinal) + 3, true);
        Check(!sentence.IsPoint && editor.Document.GetText(sentence.Offset, sentence.Length).Trim() == "Hello from TeXeed.", "Source hold/drag must select the complete sentence");
        Invoke(window, "SetAiMarking", false);
        Check(!editor.IsAiMarking && !preview.IsAiSelecting, "PDF marking did not exit");
        Invoke(window, "SetAiMarking", true);
        var cursorEvent = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.QueryCursorEvent };
        editor.TextArea.TextView.RaiseEvent(cursorEvent);
        Check(cursorEvent.Cursor != Cursors.Help, "PDF circle mode must not affect the source editor cursor");
        var rightDouble = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(rightDouble, 2);
        preview.RaiseEvent(rightDouble);
        Check(!preview.IsAiSelecting && rightDouble.Handled, "Right double-click did not leave PDF circle mode");
        Invoke(window, "SetAiMarking", true);
        var escape = CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent);
        Invoke(window, "Window_KeyDown", window, escape);
        Check(!preview.IsAiSelecting && escape.Handled, "Escape did not leave PDF marking mode");
        var beforePoint = book.Entries.Count;
        Invoke(window, "CollectSourceMark", editor, point);
        Check(book.Entries.Count == beforePoint + 1 && book.Entries.Last().IsPoint, "Source point was not immediately collected in chat composer");
        book.Active.Title = "群的单位元";
        book.Turns.Add(new("群的单位元为什么唯一？", "若 e 和 f 都是单位元，则 e = ef = f，因此单位元唯一。"));
        Invoke(ai, "RefreshTranscript");
        var mainModels = (ComboBox)ai.Settings.FindName("Models");
        mainModels.ItemsSource = new[] { new AiModel("gpt-6.1-sol", "GPT-6.1 Sol") { ReasoningEfforts = ["low", "medium", "high", "xhigh", "max"] } };
        mainModels.SelectedIndex = 0;
        await ai.Settings.SelectEffortAsync("high");
        var search = (TextBox)ai.FindName("MessageSearchBox"); search.Text = "唯一";
        Check(((TextBlock)ai.FindName("MessageMatchStatus")).Text == "1/1", "In-conversation search failed to find the answer");
        Capture(window, Path.Combine(output, "ai-tab.png"));
        Invoke(window,"OpenAiHistory"); window.UpdateLayout();
        var historyView = (AiHistoryView)Field(window,"_aiHistory")!;
        ((TextBox)historyView.FindName("SearchBox")).Text = "单位元"; window.UpdateLayout();
        Check(((ItemsControl)historyView.FindName("ConversationList")).Items.Count == 1,"History tab search failed");
        Capture(window,Path.Combine(output,"ai-history-tab.png"));
        Invoke(window, "OpenAiSettings"); window.UpdateLayout(); Capture(window, Path.Combine(output, "ai-settings.png")); Invoke(window, "OpenAiTab");
        var context = editor.ContextMenu; context.IsOpen = true; window.UpdateLayout();
        Check(((System.Windows.Media.SolidColorBrush)context.Background).Color != ((System.Windows.Media.SolidColorBrush)context.Foreground).Color, "Context menu foreground/background are indistinguishable"); context.IsOpen = false;
        await CallAsync(window, "HandleShortcutAsync", CreateKey(window, Key.F4, Keyboard.PreviewKeyDownEvent), ModifierKeys.Control);
        Check(tabs.Owner(aiKey) is null && tabs.Owner(TabWorkspace.PreviewKey) is not null, "Ctrl+F4 AI closing affected protected preview");
        Invoke(window, "OpenTitleSubmenu", menu); Check(menu.IsSubmenuOpen || menu.HasItems, "Alt+A failed to expose AI commands");
        await ai.SaveAsync();
        Console.WriteLine($"AI: {count} checks passed (OAuth identity, encrypted credentials, streaming/tool calls, source/PDF context, tabs, persistence).");
    }
    private static (string Jwt, JsonElement Keys) MakeTestIdentity()
    {
        using var rsa = RSA.Create(2048); var key = rsa.ExportParameters(false); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var h = ChatGptLogin.Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"kid\":\"test\"}"));
        var p = ChatGptLogin.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = ChatGptLogin.Issuer, aud = "test-client", sub = "test-user", email = "test@example.com", iat = now, exp = now + 300, nonce = "test-nonce" }));
        var signature = ChatGptLogin.Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var keys = JsonSerializer.SerializeToElement(new { keys = new[] { new { kid = "test", kty = "RSA", n = ChatGptLogin.Base64Url(key.Modulus!), e = ChatGptLogin.Base64Url(key.Exponent!) } } });
        return (h + "." + p + "." + signature, keys);
    }
    private sealed class AiFakeHandler : HttpMessageHandler
    {
        public int Requests, ResponseRequests; public bool Broken, ProgramFailure, SawToolOutput, SawContract, SwitchAccount, ToolsInEvents, Empty;
        public string? LastModel, LastEffort, LastInstructions;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath != "/v1/responses") throw new Exception("Unexpected network endpoint in AI test");
            ResponseRequests++;
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            LastModel = json["model"]?.ToString(); LastEffort = json["reasoning"]?["effort"]?.ToString();
            LastInstructions = json["instructions"]?.ToString();
            SawContract = json["store"]!.GetValue<bool>() == false && json["stream"]!.GetValue<bool>() && json["tools"]![0]!["type"]!.GetValue<string>() == "namespace";
            if (Requests > 1) SawToolOutput = json["input"]!.AsArray().Any(n => n?["type"]?.GetValue<string>() == "function_call_output" && n["output"]!.GetValue<string>().Contains("main.tex:2"));
            var data = Broken ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" : Requests == 1
                ? "data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"function_call\",\"name\":\"search_source\",\"call_id\":\"c1\",\"arguments\":\"{\\\"query\\\":\\\"compact proof\\\"}\"}]}}\n\n"
                : "data: {\"type\":\"response.output_text.delta\",\"delta\":\"定位到 main.tex:2。\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            if (ToolsInEvents && Requests == 1)
                data = "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"name\":\"search_source\",\"call_id\":\"c1\",\"arguments\":\"{\\\"query\\\":\\\"compact proof\\\"}\"}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            if (Empty) data = "data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            if (ProgramFailure) data = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\ndata: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"fixture program failure\"}}}\n\n";
            if (SwitchAccount && Requests == 1) { var changed = AiCredentials.Load(); changed.ApiKey = "different-test-key"; changed.Save(); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }
    }
}
