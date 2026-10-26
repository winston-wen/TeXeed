using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckWordWrapAsync(MainWindow window, string output)
    {
        var editor = (LatexEditor)window.FindName("Editor");
        var item = (MenuItem)window.FindName("WordWrapMenuItem");
        if (!editor.WordWrap || !item.IsChecked) throw new Exception("Automatic wrapping must start enabled");
        var directory = Path.Combine(output, "wrap-project"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "main.tex");
        var paragraph = string.Concat(Enumerable.Repeat("这是用于自动换行的测试文字，包含 LaTeX $a^2+b^2=c^2$ 和英文文字。", 40));
        var text = "\\documentclass{article}\n\\begin{document}\n\\section{自动换行}\n" + paragraph + "\n\\end{document}\n";
        File.WriteAllText(path, text);
        await CallAsync(window, "OpenAsync", path);
        async Task Settle() { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        await Settle();
        if (!editor.TextArea.TextView.VisualLines.Any(l => l.TextLines.Count > 1)) throw new Exception("Long source line was not wrapped");
        var document = editor.Document;
        var revision = Field(window, "_revision");
        Capture(window, Path.Combine(output, "word-wrap-enabled.png"));
        var shortcut = CreateKey(window, Key.Z, Keyboard.PreviewKeyDownEvent);
        await CallAsync(window, "HandleShortcutAsync", shortcut, ModifierKeys.Alt); await Settle();
        if (!shortcut.Handled || editor.WordWrap || item.IsChecked) throw new Exception("Alt+Z did not disable wrapping");
        var reloaded = new MainWindow { ShowInTaskbar = false };
        try
        {
            if (((LatexEditor)reloaded.FindName("Editor")).WordWrap || ((MenuItem)reloaded.FindName("WordWrapMenuItem")).IsChecked)
                throw new Exception("New window did not restore the saved wrap-off preference");
        }
        finally
        {
            ((PdfPreview)reloaded.FindName("Preview")).Dispose();
            reloaded.GetType().GetField("_allowClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(reloaded, true);
            reloaded.Close();
        }
        Invoke(window, "ShowMainMenu", (MenuItem)((Menu)window.FindName("MainMenu")).Items[2]);
        Invoke(window, "OpenTitleSubmenu", (MenuItem)((Menu)window.FindName("MainMenu")).Items[2]);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Settle();
        if (!editor.WordWrap || !item.IsChecked) throw new Exception("View menu did not re-enable wrapping");
        window.Width = window.MinWidth; await Settle();
        ((Button)window.FindName("CompactMenuButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var popup = (Popup)Field(window, "_compactMenu")!;
        var panel = (CompactMenuPanel)popup.Child;
        panel.NavigateTo((MenuItem)((Menu)window.FindName("MainMenu")).Items[2]); await Settle();
        Descendants<Button>(panel).Single(b => ReferenceEquals(b.Tag, item)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        if (editor.WordWrap || item.IsChecked || popup.IsOpen) throw new Exception("Compact View menu did not toggle wrapping and close");
        await CallAsync(window, "HandleShortcutAsync", CreateKey(window, Key.Z, Keyboard.PreviewKeyDownEvent), ModifierKeys.Alt); await Settle();
        if (!editor.WordWrap || !ReferenceEquals(editor.Document, document) || editor.Text != text || !Equals(Field(window, "_revision"), revision))
            throw new Exception("Wrapping modified document content, revision or session");
        window.Width = 1440; await Settle();
        editor.NavigateTo(new SourceLocation(path, 4, 500), center: true); await Settle();
        if (editor.CurrentLocation?.Line != 4 || editor.CurrentLocation.Column != 500 || editor.ViewportLocation?.Line != 4)
            throw new Exception("Wrapped source navigation changed logical line/column");
        Capture(window, Path.Combine(output, "word-wrap-source-location.png"));
        Console.WriteLine("PASS: automatic soft wrap, Alt+Z/View/compact menu, settings restore, untouched document and wrapped source navigation");
    }
}
