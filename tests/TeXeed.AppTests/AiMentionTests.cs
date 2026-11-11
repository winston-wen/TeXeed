using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiMentionsAsync(MainWindow window, string output)
    {
        window.Left = 10; window.Top = 10; window.Width = 1000; window.Height = 650;
        window.UpdateLayout();
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static async Task Until(Func<bool> ready, string message)
        {
            for (var i = 0; i < 150 && !ready(); i++) await Task.Delay(20);
            Check(ready(), message);
        }
        static void SetPrivate(object owner, string field, object? value) => owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
        Invoke(window, "OpenAiTab");
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var project = (TexProject)Field(window, "_project")!;
        var root = project.Root;
        var rootNow = root;
        ai.ProjectRoot = () => rootNow;
        await ai.SetProjectAsync(project.MainPath);
        var composer = (TextBox)ai.FindName("Composer");
        var spaceFile = Path.Combine(root, "space name.tex"); File.WriteAllText(spaceFile, "example");
        window.Activate(); composer.Focus();
        Popup? Popup() => (Popup?)Field(ai, "_mentionPopup");
        ListBox? List() => (ListBox?)Field(ai, "_mentionList");
        void Type(string text) { composer.Text = text; composer.CaretIndex = text.Length; }
        Check(!AiPanel.TryMentionQuery("mail user@example", 17, out _, out _), "Email address opened path completion");
        Type("mail user@example"); await Task.Delay(100);
        Check(Popup()?.IsOpen != true, "Typing an email opened path suggestions");
        Type("@space"); await Until(() => Popup()?.IsOpen == true && List()?.Items.Count > 0, "File suggestions did not open");
        var enter = CreateKey(window, Key.Enter, Keyboard.PreviewKeyDownEvent); composer.RaiseEvent(enter);
        var book = (AiNotebook)Field(ai, "_book")!;
        Check(enter.Handled && composer.Text.Contains("@\"space name.tex\"") && book.Entries.Single().Files.Single().ReferencePath == spaceFile,
            "Enter did not insert a quoted path and its file reference");
        var inserted = book.Entries.Single();
        Check(AiPanel.SnapshotSelectedParts(inserted, false, false, 1).Files.Count == 0,
            "An unselected mention was included in a submitted snapshot");
        await ai.SaveAsync();
        var saved = AiStorage.Load(project.MainPath);
        Check(saved.Draft == composer.Text && saved.Entries.Single().Files.Single().ReferencePath == spaceFile,
            "Mention text and its selected path did not survive a draft save");
        var originalMention = composer.Text;
        inserted.AttachmentSelected = false;
        composer.BeginChange(); composer.SelectAll(); composer.SelectedText = ""; composer.EndChange();
        Check(book.Entries.Count == 0 && composer.CanUndo, "Deleting an inserted mention did not remove the path or create undo history");
        composer.Undo();
        Check(composer.Text == originalMention && book.Entries.Count == 1 && ReferenceEquals(book.Entries.Single(), inserted) &&
            !book.Entries.Single().AttachmentSelected && book.Entries.Single().Files.Single().ReferencePath == spaceFile,
            "TextBox.Undo did not restore the same unchecked path reference");
        var saveTimer = (System.Windows.Threading.DispatcherTimer)Field(ai, "_saveTimer")!;
        saveTimer.Stop(); inserted.AttachmentSelected = true;
        Check(saveTimer.IsEnabled, "Restored reference did not resubscribe its change handler");
        await ai.SaveAsync();
        Check(AiStorage.Load(project.MainPath).Entries.Single().AttachmentSelected, "Restored reference selection was not persisted");
        Check(composer.CanRedo, "TextBox redo was not available after undoing the deletion");
        composer.Redo();
        Check(composer.Text.Length == 0 && book.Entries.Count == 0, "TextBox.Redo did not remove the restored reference");
        composer.BeginChange(); composer.SelectedText = originalMention; composer.EndChange();
        Check(book.Entries.Count == 0, "Manually retyping a removed token silently authorized a path");
        if (composer.CanUndo) { composer.Undo(); if (composer.CanRedo) composer.Redo(); }
        Check(book.Entries.Count == 0, "Undo/redo of manually retyped text silently restored an old path");
        Type("");
        Check(book.Entries.Count == 0, "Deleting mention text retained its path reference");

        composer.Focus(); Type("@space"); await Until(() => Popup()?.IsOpen == true, "Unselected mention candidate did not open");
        var unsentEnter = CreateKey(window, Key.Enter, Keyboard.PreviewKeyDownEvent); composer.RaiseEvent(unsentEnter);
        var unsent = book.Entries.Single(); unsent.AttachmentSelected = false;
        ai.DetachUnsentMentions([]);
        Type(""); Type("发送后继续写下一条草稿");
        Check(book.Entries.Contains(unsent) && !unsent.AttachmentSelected && unsent.Files.Single().ReferencePath == spaceFile,
            "Clearing a sent draft or later typing deleted an unchecked path reference");
        book.Entries.Remove(unsent); Type("");

        composer.Focus(); Type("@method"); await Until(() => Popup()?.IsOpen == true, "Tab candidate did not open");
        var tab = CreateKey(window, Key.Tab, Keyboard.PreviewKeyDownEvent); composer.RaiseEvent(tab);
        Check(tab.Handled && book.Entries.Single().Files.Single().ReferencePath!.EndsWith("method.tex"), "Tab did not select a file");
        Type(""); composer.Focus(); Type("@sections");
        await Until(() => Popup()?.IsOpen == true, "Directory suggestion did not open");
        var directoryEnter = CreateKey(window, Key.Enter, Keyboard.PreviewKeyDownEvent); composer.RaiseEvent(directoryEnter);
        Check(directoryEnter.Handled && book.Entries.Single().Files.Single().ReferenceKind == "directory" && composer.Text.Contains("@sections/"),
            "Selecting a directory did not create a directory reference");
        Type(""); composer.Focus(); Type("@method"); await Until(() => Popup()?.IsOpen == true, "Escape candidate did not open");
        var escape = CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent); composer.RaiseEvent(escape);
        Check(escape.Handled && Popup()?.IsOpen == false && composer.Text == "@method", "Escape changed the draft or failed to close suggestions");

        Type(""); composer.Focus(); Type("@method"); await Until(() => Popup()?.IsOpen == true, "Mouse candidate did not open");
        var list = List()!; list.UpdateLayout(); list.ScrollIntoView(list.Items[0]); list.UpdateLayout();
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Check(!item.Focusable && composer.IsKeyboardFocusWithin, "Suggestion item would steal focus before mouse selection");
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = item };
        list.RaiseEvent(click);
        Check(click.Handled && book.Entries.Count == 1 && Popup()?.IsOpen == false, "Routed mouse event did not select mention candidate");
        Type(""); composer.Focus(); Type("@method"); await Until(() => Popup()?.IsOpen == true, "Focus-loss candidate did not open");
        ((Button)ai.FindName("AddButton")).Focus(); await Task.Delay(50);
        Check(Popup()?.IsOpen == false, "Losing input focus left suggestions open");

        rootNow = Path.Combine(output, "does-not-exist"); composer.Focus(); Type("@method"); await Task.Delay(200);
        Check(Popup()?.IsOpen != true, "Failed discovery displayed stale candidates");
        rootNow = root; Type(""); Type("@method"); await Until(() => Popup()?.IsOpen == true, "Discovery did not recover after an invalid project root");
        Type("");
        var pending = new TaskCompletionSource<AiWorkspaceFiles.Catalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivate(ai, "_mentionDiscovery", pending.Task); SetPrivate(ai, "_mentionDiscoveryRoot", root);
        composer.Focus(); Type("@method"); await Task.Delay(40);
        await ai.NewConversationAsync();
        pending.SetResult(AiWorkspaceFiles.Discover(root, ".", 2, CancellationToken.None)); await Task.Delay(100);
        Check(Popup()?.IsOpen != true && composer.Text.Length == 0, "An old query reopened suggestions after switching conversations");

        var secondRoot = Path.Combine(output, "second-project"); Directory.CreateDirectory(secondRoot);
        var secondMain = Path.Combine(secondRoot, "main.tex"); File.WriteAllText(secondMain, "test");
        var pendingProject = new TaskCompletionSource<AiWorkspaceFiles.Catalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivate(ai, "_mentionDiscovery", pendingProject.Task); SetPrivate(ai, "_mentionDiscoveryRoot", root);
        composer.Focus(); Type("@method"); await Task.Delay(40);
        rootNow = secondRoot; await ai.SetProjectAsync(secondMain);
        pendingProject.SetResult(AiWorkspaceFiles.Discover(root, ".", 2, CancellationToken.None)); await Task.Delay(100);
        Check(Popup()?.IsOpen != true && composer.Text.Length == 0, "An old query reopened suggestions after switching projects");
        Console.WriteLine("PASS: @ mention selection, persistence, selection boundaries, focus, switching and discovery recovery.");
        await CheckAiComposerDropAsync(window, output);
    }
}
