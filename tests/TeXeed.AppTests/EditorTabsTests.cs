using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckEditorTabsAsync(MainWindow window, string output)
    {
        var folder = Path.Combine(output, "tabs-project"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        var files = Enumerable.Range(1, 16).Select(n => Path.Combine(folder, $"chapter-{n:00}-标题.tex")).ToArray();
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\n" + string.Join('\n', files.Select(f => "\\input{" + Path.GetFileName(f) + "}")) + "\n\\end{document}\n");
        foreach (var file in files) File.WriteAllText(file, "\\section{Test}\nBody of " + Path.GetFileName(file) + "\n");
        await CallAsync(window, "OpenAsync", main);
        var editor = (LatexEditor)window.FindName("Editor");
        var strip = (EditorTabStrip)window.FindName("FileTabs");
        var panel = (StackPanel)strip.FindName("Items");
        var scroller = (ScrollViewer)strip.FindName("Scroller");
        var left = (Button)strip.FindName("LeftButton"); var right = (Button)strip.FindName("RightButton");
        var tabs = (List<string>)Field(window, "_tabs")!;
        async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        Border View(string path) => panel.Children.Cast<Border>().Single(b => Equals(b.Tag, path));
        void OpenAll() { Invoke(window, "ActivateFile", main); foreach (var file in files) Invoke(window, "ActivateFile", file); }
        OpenAll(); await Settle();
        if (strip.ScrollableWidth <= 1 || !left.IsVisible || right.IsVisible) throw new Exception("Active last tab was not revealed with correct overflow indicators");
        if (Descendants<System.Windows.Controls.Primitives.ScrollBar>(strip).Any(b => b.IsVisible)) throw new Exception("Tab strip introduced a scrollbar");
        strip.ScrollBy(-100000); await Settle();
        if (left.IsVisible || !right.IsVisible || scroller.OpacityMask is not LinearGradientBrush) throw new Exception("Tab strip start does not show right arrow and clipped-edge gradient");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        scroller.RaiseEvent(wheel); await Settle();
        if (!wheel.Handled || strip.HorizontalOffset <= 1) throw new Exception("Mouse wheel did not scroll the tabs horizontally");
        if (!left.IsVisible || !right.IsVisible) throw new Exception("Middle tab viewport must show both overflow arrows");
        Capture(window, Path.Combine(output, "tabs-overflow-dark.png"));
        ThemeManager.Apply("github-light-default"); await Settle();
        Capture(window, Path.Combine(output, "tabs-overflow-light.png"));
        ThemeManager.Apply("ristretto"); await Settle();

        // Hovering a title must not paint an inner rounded highlight, in either theme.
        GetCursorPos(out var previousPointer);
        try
        {
            strip.ScrollBy(-100000); await Settle();
            window.Activate();
            foreach (var theme in new[] { "ristretto", "github-light-default" })
            {
                ThemeManager.Apply(theme); await Settle();
                foreach (var path in new[] { main, files[0] })
                {
                    Invoke(window, "ActivateFile", path); await Settle();
                    var row = View(path);
                    var title = (Button)((Grid)row.Child).Children[0];
                    var point = title.PointToScreen(new Point(title.ActualWidth / 2, title.ActualHeight / 2));
                    SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(120);
                    var chrome = (Border)title.Template.FindName("ButtonChrome", title);
                    if (!title.IsMouseOver || row.Background is not SolidColorBrush { Color.A: 0 }
                        || chrome.Background is not SolidColorBrush { Color.A: 0 } || row.BorderBrush is not LinearGradientBrush)
                        throw new Exception("Hovered selected tab still has a highlight fill or lost its selected outline: " + theme);
                    var other = View(path == main ? files[0] : main);
                    other.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                    if (other.Background is not SolidColorBrush { Color.A: 0 })
                        throw new Exception("Inactive tab hover still paints a highlight fill");
                }
                Capture(window, Path.Combine(output, "tabs-no-highlight-" + theme + ".png"));
            }
        }
        finally { SetCursorPos(previousPointer.X, previousPointer.Y); ThemeManager.Apply("ristretto"); }
        Invoke(window, "ActivateFile", files[^1]); await Settle();

        // Move a real tab using the same coordinates used by drag handling.
        strip.ScrollBy(-100000); await Settle();
        var orderBefore = tabs.ToArray();
        var dragged = files[0];
        strip.GetType().GetField("_dragPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(strip, dragged);
        if (!(bool)Invoke(strip, "BeginDrag")! || QueryCursorFor(strip) != Cursors.Hand)
            throw new Exception("Tab drag did not capture the pointer with a hand cursor");
        var target = View(files[2]);
        var x = target.TranslatePoint(new Point(target.ActualWidth * .8, 0), strip).X;
        Invoke(strip, "DragTo", x);
        Invoke(strip, "EndDrag"); await Settle();
        if (tabs.IndexOf(dragged) != 3 || editor.CurrentFilePath != files[^1]) throw new Exception("Dragging did not reorder without switching the editor");
        if (strip.IsMouseCaptured || QueryCursorFor(strip) == Cursors.Hand)
            throw new Exception("Tab drag left capture/hand cursor behind after release");
        Invoke(strip, "BeginDrag"); Mouse.Capture(null);
        if ((bool)Field(strip, "_dragging")! || QueryCursorFor(strip) == Cursors.Hand)
            throw new Exception("Tab drag left a hand cursor behind after losing capture");
        if (tabs.Except(orderBefore).Any() || tabs.Count != orderBefore.Length) throw new Exception("Drag lost/duplicated a document");

        Invoke(window, "ActivateFile", files[0]); await Settle();
        editor.Document.Insert(editor.Document.TextLength, "% unsaved\n");
        var document = editor.Document;
        var text = editor.Text;
        var close = (Button)((Grid)View(files[0]).Child).Children[1];
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        if (tabs.Contains(files[0]) || editor.CurrentFilePath == files[0]) throw new Exception("Close cross did not close its file tab");
        Invoke(window, "ActivateFile", files[0]); await Settle();
        if (!ReferenceEquals(document, editor.Document) || editor.Text != text || !editor.Document.UndoStack.CanUndo)
            throw new Exception("Closing/reopening a modified tab discarded draft or undo history");
        var key = CreateKey(window, Key.F4, Keyboard.PreviewKeyDownEvent);
        editor.TextArea.Focus(); await CallAsync(window, "HandleShortcutAsync", key, ModifierKeys.Control); await Settle();
        if (!key.Handled || tabs.Contains(files[0])) throw new Exception("Ctrl+F4 did not close the active editing tab");
        await CallAsync(window, "SaveAsync");
        if (tabs.Contains(files[0]) || !File.ReadAllText(files[0]).Contains("unsaved")) throw new Exception("Saving closed drafts reopened tabs or lost changes");

        OpenAll(); await Settle();
        var owner = View(files[7]);
        owner.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
        if (owner.ContextMenu is not { IsOpen: true } menu || menu.Items.Count != 4) throw new Exception("Right click did not show all four bulk close options");
        menu.IsOpen = false;
        foreach (var scope in new[] { TabCloseScope.Left, TabCloseScope.Right, TabCloseScope.Others })
        {
            OpenAll(); await Settle();
            var before = tabs.ToArray(); var index = Array.IndexOf(before, files[7]);
            Invoke(window, "CloseTabs", files[7], scope); await Settle();
            var expected = scope == TabCloseScope.Left ? before.Skip(index) : scope == TabCloseScope.Right ? before.Take(index + 1) : new[] { files[7] };
            if (!tabs.SequenceEqual(expected)) throw new Exception("Incorrect batch close scope: " + scope);
        }
        Invoke(window, "CloseTabs", files[7], TabCloseScope.All); await Settle();
        if (tabs.Count != 0 || editor.CurrentFilePath != null || editor.IsVisible || !((FrameworkElement)window.FindName("NoOpenFile")).IsVisible)
            throw new Exception("Closing all tabs did not leave an empty editing pane");
        await CallAsync(window, "SaveAsync");
        if (tabs.Count != 0) throw new Exception("Saving after close-all reopened the main file");
        Invoke(window, "ActivateFile", files[0]); await Settle();
        if (!editor.IsVisible || editor.Text != text || ((FrameworkElement)window.FindName("NoOpenFile")).IsVisible)
            throw new Exception("Sidebar reopening after close-all did not restore the file");
        Capture(window, Path.Combine(output, "tabs-content-fit.png"));
        Console.WriteLine("PASS: no tab highlight in dark/light themes, drag hand cursor and release/capture-loss restoration, close cross/Ctrl+F4, draft and undo retention, bulk scopes, drag order, overflow and empty-pane reopening");
    }
}
