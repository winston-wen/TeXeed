using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckDockingAsync(MainWindow window, string output)
    {
        var folder = Path.Combine(output, "dock-project"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex"); var one = Path.Combine(folder, "one.tex");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\n\\input{one}\n\\end{document}\n");
        File.WriteAllText(one, string.Join('\n', Enumerable.Range(1, 100).Select(n => "Line " + n)));
        await CallAsync(window, "OpenAsync", main);
        var model = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var left = (LatexEditor)window.FindName("LeftEditor"); var right = (LatexEditor)window.FindName("RightEditor");
        var leftStrip = (EditorTabStrip)window.FindName("FileTabs"); var rightStrip = (EditorTabStrip)window.FindName("RightFileTabs");
        var preview = (PdfPreview)window.FindName("Preview");
        async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        if (model.Owner(main) != TabSide.Left || model.Owner(TabWorkspace.PreviewKey) != TabSide.Right) throw new Exception("Default source/preview layout is incorrect");
        var previewRow = ((StackPanel)rightStrip.FindName("Items")).Children.Cast<Border>().Single();
        var gear = (Button)((Grid)previewRow.Child).Children[1];
        var sawSettings = false;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<PreviewSettingsWindow>().Single(); sawSettings = true;
            Capture(dialog, Path.Combine(output, "preview-settings.png")); dialog.Close();
        }));
        gear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!sawSettings || model.Owner(TabWorkspace.PreviewKey) is null) throw new Exception("Preview gear did not open settings without closing preview");
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKey);
        await CallAsync(window, "HandleShortcutAsync", CreateKey(window, Key.F4, Keyboard.PreviewKeyDownEvent), ModifierKeys.Control);
        if (model.Owner(TabWorkspace.PreviewKey) is null) throw new Exception("Ctrl+F4 closed preview");
        Invoke(window, "ActivateFile", one); await Settle();
        left.CaretOffset = 15; left.Select(10, 5); left.ScrollToVerticalOffset(180); await Settle();
        left.Document.Insert(left.Document.TextLength, "\nunsaved"); var document = left.Document; var text = left.Text;
        var caret = left.CaretOffset; var offset = left.VerticalOffset;
        var selectedText = left.SelectedText;
        Invoke(window, "MoveWorkspaceTab", one, TabSide.Right, 0, false); await Settle();
        if (!ReferenceEquals(document, right.Document) || right.Text != text || !right.Document.UndoStack.CanUndo || right.CaretOffset != caret || right.SelectedText != selectedText)
            throw new Exception("Cross-pane source move lost buffer, undo or caret state");
        if (Math.Abs(right.VerticalOffset - offset) > 3) throw new Exception("Cross-pane source move lost scroll position");
        if (!left.IsVisible || !right.IsVisible || left.CurrentFilePath != main || right.CurrentFilePath != one)
            throw new Exception("Two source tabs are not independently visible");
        var leftColumn = ((Grid)window.FindName("Workspace")).ColumnDefinitions[2]; leftColumn.Width = new GridLength(420);
        Invoke(window, "RefreshTabs"); if (!leftColumn.Width.IsAbsolute || leftColumn.Width.Value != 420) throw new Exception("Tab refresh reset a resized group width");
        right.Document.Insert(right.Document.TextLength, "\nright edit");
        if (!((Dictionary<string, string>)Field(window, "_changes")!)[one].Contains("right edit")) throw new Exception("Right-pane edits did not enter shared save state");
        right.Undo(); Invoke(window, "SelectWorkspaceTab", TabSide.Right, TabWorkspace.PreviewKey); await Settle();
        var pdf = Path.Combine(output, "preview.pdf"); WriteThemePdf(pdf); await preview.LoadAsync(pdf); await Settle();
        var pdfCount = preview.PageCount;
        Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, TabSide.Left, int.MaxValue, false); await Settle();
        if (preview.PageCount != pdfCount || preview.Parent != window.FindName("PreviewContent") || !right.IsVisible)
            throw new Exception("Preview move reloaded PDF or hid the other source pane");
        await WaitForPreviewRenderedAsync(window);
        Capture(window, Path.Combine(output, "preview-left-source-right.png"));
        Invoke(window, "MoveWorkspaceTab", one, TabSide.Left, 1, false); await Settle();
        if (model.Has(TabSide.Right) || ((FrameworkElement)window.FindName("PreviewSplitter")).IsVisible)
            throw new Exception("Empty right pane did not collapse");
        var area = (Grid)window.FindName("Workspace");
        if (Math.Abs(((FrameworkElement)window.FindName("EditorPane")).ActualWidth - (area.ActualWidth - area.ColumnDefinitions[0].ActualWidth - area.ColumnDefinitions[1].ActualWidth)) > 2)
            throw new Exception("Single pane did not occupy all available document width");
        var overlay = (FrameworkElement)window.FindName("DockDropOverlay");
        var edge = overlay.PointToScreen(new Point(overlay.ActualWidth - 4, 100));
        if (!(bool)Invoke(window, "UpdateDockDrag", TabWorkspace.PreviewKey, edge)! || !((FrameworkElement)window.FindName("DockDropCue")).IsVisible)
            throw new Exception("Single-pane edge drop has no split cue");
        Invoke(window, "DropWorkspaceTab", TabWorkspace.PreviewKey, edge); await Settle();
        if (!model.Has(TabSide.Left) || !model.Has(TabSide.Right) || model.Owner(TabWorkspace.PreviewKey) != TabSide.Right)
            throw new Exception("Right-edge drop did not create a right pane");
        Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, TabSide.Left, int.MaxValue, false); await Settle();
        edge = overlay.PointToScreen(new Point(4, 100)); Invoke(window, "DropWorkspaceTab", TabWorkspace.PreviewKey, edge); await Settle();
        if (model.Owner(TabWorkspace.PreviewKey) != TabSide.Left || model.Owner(main) != TabSide.Right || model.Owner(one) != TabSide.Right)
            throw new Exception("Left-edge split lost or misplaced existing source tabs");
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, one); await Settle();
        if (!ReferenceEquals(document, right.Document) || right.Text != text) throw new Exception("Repeated group splits discarded the unsaved document");
        var settings = (PreviewSettingsWindow)Invoke(window, "CreatePreviewSettings")!; settings.Show(); await Settle();
        ((RadioButton)settings.FindName("ManualZoom")).IsChecked = true;
        ((TextBox)settings.FindName("ZoomInput")).Text = "137.62%";
        ((ComboBox)settings.FindName("FollowModeBox")).SelectedValue = PreviewFollowMode.None;
        ((CheckBox)settings.FindName("InvertPdfBox")).IsChecked = false; ((CheckBox)settings.FindName("AutoCropPdfBox")).IsChecked = false;
        ((Button)settings.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Math.Abs(preview.Zoom - 1.3762) > .00001 || preview.IsAutoWidth || preview.InvertColors || preview.AutoCrop
            || ((AppSettings)Field(window, "_settings")!).FollowMode != PreviewFollowMode.None) throw new Exception("Preview settings did not apply all options");
        ((TextBox)settings.FindName("ZoomInput")).Text = "NaN"; ((Button)settings.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Math.Abs(preview.Zoom - 1.3762) > .00001) throw new Exception("Preview settings accepted invalid zoom");
        settings.Close();
        await WaitForPreviewRenderedAsync(window);
        await CallAsync(window, "SaveAsync");
        if (!File.ReadAllText(one).Contains("unsaved")) throw new Exception("Moved source buffer was omitted from save-all");
        Capture(window, Path.Combine(output, "docking-dark.png")); ThemeManager.Apply("github-light-default"); await Settle(); Capture(window, Path.Combine(output, "docking-light.png"));
        ThemeManager.Apply("ristretto");
        Invoke(window, "CloseTabs", one, TabCloseScope.All); await Settle();
        if (model.Sources.Any() || model.Owner(TabWorkspace.PreviewKey) is null || model.Has(TabSide.Right)) throw new Exception("Close-all removed preview or retained empty source pane");
        Invoke(window, "ActivateFile", main); await Settle();
        if (model.Owner(main) != TabSide.Left) throw new Exception("New source opening no longer defaults to left");
        Console.WriteLine("PASS: preview gear/protection, two independent source panes, buffer/undo/caret/scroll retention, shared save, both edge split cues, empty-pane collapse, PDF retention, settings validation and dark/light layouts");
    }
}
