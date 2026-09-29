using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckThemesAsync(MainWindow window, string output)
    {
        var editor = (LatexEditor)window.FindName("Editor");
        var preview = (PdfPreview)window.FindName("Preview");
        var previous = ((TexProject)Field(window, "_project")!).MainPath;
        var folder = Path.Combine(output, "theme-project"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        File.WriteAllText(main, "\\section{主题与公式 $\\sum_{k=0}^{114} k^2$}\n% TODO: 检查主题\n"
            + string.Concat(Enumerable.Range(0, 150).Select(i => $"\\subsection{{测试章节 {i}}}\n正文 example $x_{i}^2$，引用 \\ref{{sec:intro}}。\n")));
        var pdf = Path.Combine(folder, "preview.pdf");
        WriteThemePdf(pdf);
        await CallAsync(window, "OpenAsync", main);
        preview.AutoCrop = false; await preview.LoadAsync(pdf);
        editor.Document.Insert(editor.Document.TextLength, "% unsaved theme test " + new string('x', 300));
        editor.NavigateTo(new SourceLocation(main, 7));
        var document = editor.Document; var caret = editor.CaretOffset; var font = editor.FontFamily;
        var themeMenu = (MenuItem)window.FindName("ThemeMenu");
        try
        {
            foreach (var id in new[] { "github-light-default", "ristretto", "github-light-default" })
            {
                var item = themeMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, id));
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await WaitForPreviewRenderedAsync(window);
                var theme = ThemeManager.Current;
                if (theme.Id != id || ((SolidColorBrush)editor.Background).Color != ThemeManager.Brush("EditorBackground").Color
                    || ((SolidColorBrush)editor.Foreground).Color != ThemeManager.Brush("EditorForeground").Color)
                    throw new Exception("Editor did not switch its active theme");
                if (preview.InvertColors != theme.IsDark) throw new Exception("PDF appearance did not follow theme selection");
                if (!ReferenceEquals(document, editor.Document) || editor.CaretOffset != caret || editor.FontFamily != font || !editor.Document.UndoStack.CanUndo)
                    throw new Exception("Theme switch disturbed editor state");
                var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("TEXEED_HOME")!, "settings.json")))!;
                if (saved.ThemeId != id) throw new Exception("Theme selection was not saved");
                foreach (var owner in new FrameworkElement[] { editor, preview })
                {
                    var bar = Descendants<ScrollBar>(owner).First(bar => bar.Orientation == Orientation.Vertical && bar.IsVisible);
                    if (Math.Abs(bar.ActualWidth - 8) > .1) throw new Exception($"{owner.GetType().Name} scrollbar width is {bar.ActualWidth}, expected 8 DIP");
                    var grip = Descendants<Border>(bar).FirstOrDefault(border => border.Name == "Grip");
                    if (grip is null || grip.CornerRadius.TopLeft < 3 || Math.Abs(grip.ActualWidth - 6) > .1
                        || ((SolidColorBrush)grip.Background).Color != ThemeManager.Brush("ScrollThumb").Color)
                        throw new Exception($"{owner.GetType().Name} scrollbar is not a themed capsule");
                    if (((SolidColorBrush)bar.Background).Color != ThemeManager.Brush("ScrollTrack").Color)
                        throw new Exception("Scrollbar track lost its distinct background");
                }
                await CheckPaneChromeAsync(window);
                Capture(window, Path.Combine(output, id + ".png"));
            }
            var reopened = new MainWindow { Width = 1100, Height = 700 };
            reopened.Show(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (ThemeManager.Current.Id != "github-light-default") throw new Exception("Reopened window forgot selected theme");
            await CloseAndObserveAsync(reopened, "theme persistence");
            window.Activate();
            var settings = new SettingsWindow((AppSettings)Field(window, "_settings")!) { Owner = window };
            settings.Show(); window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(settings, Path.Combine(output, "github-light-settings.png")); settings.Close();
            // JSON themes are data only. An invalid edit must leave the active palette intact.
            var custom = Path.Combine(ThemeManager.DirectoryPath, "test-custom.json");
            try
            {
                File.WriteAllText(custom, """{"id":"test-custom","name":"Custom","isDark":false,"colors":{"Accent":"#123456"}}""");
                ThemeManager.Apply("test-custom");
                if (ThemeManager.Brush("Accent").Color != Color.FromRgb(18, 52, 86)) throw new Exception("Custom JSON colors not applied");
                File.WriteAllText(custom, """{"id":"test-custom","name":"Invalid","isDark":false,"colors":{"Accent":"bad"}}""");
                try { ThemeManager.Apply("test-custom"); throw new Exception("Invalid theme was accepted"); }
                catch (InvalidDataException) { }
                if (ThemeManager.Brush("Accent").Color != Color.FromRgb(18, 52, 86)) throw new Exception("Invalid theme partially replaced the palette");
            }
            finally { File.Delete(custom); }
            Console.WriteLine("PASS: pane footers, sidebar title alignment, momentary outline actions, log toggle and vertical/horizontal end caps");
            Console.WriteLine("PASS: live dark/light/custom themes, persistent selection, unchanged editor state, PDF appearance and capsule scrollbars");
        }
        finally
        {
            Invoke(window, "SelectTheme", "ristretto", true);
            await CallAsync(window, "SaveAsync"); await CallAsync(window, "OpenAsync", previous);
        }
    }

    private static async Task CheckPaneChromeAsync(MainWindow window)
    {
        var workspace = (FrameworkElement)window.FindName("Workspace");
        var editor = (LatexEditor)window.FindName("Editor");
        var preview = (PdfPreview)window.FindName("Preview");
        var footer = (FrameworkElement)window.FindName("EditorStatusBar");
        var pdfFooter = (FrameworkElement)window.FindName("PreviewStatusBar");
        if (Math.Abs(footer.TranslatePoint(new Point(), workspace).Y - pdfFooter.TranslatePoint(new Point(), workspace).Y) > .1
            || Math.Abs(footer.TranslatePoint(new Point(0, footer.ActualHeight), workspace).Y - workspace.ActualHeight) > .1)
            throw new Exception("Pane status bars must share the workspace bottom edge");
        if (window.FindName("FileCaption") != null || window.FindName("ProjectSummary") != null)
            throw new Exception("Redundant caption/sidebar footer still present");
        var tabs = (FrameworkElement)window.FindName("FileTabs");
        if (Math.Abs(editor.TranslatePoint(new Point(), workspace).Y - tabs.TranslatePoint(new Point(0, tabs.ActualHeight), workspace).Y - 1) > 2)
            throw new Exception("Editor still has a redundant header gap");
        var title = (FrameworkElement)window.FindName("SidebarTitle");
        double? y = null;
        foreach (var icon in ((Panel)window.FindName("WorkspaceIconBar")).Children.OfType<Button>().Where(button => button.Tag is string))
        {
            icon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            var next = title.TranslatePoint(new Point(), workspace).Y;
            if (y is { } before && Math.Abs(next - before) > .1) throw new Exception("Sidebar mode titles change vertical position");
            y = next;
        }
        ((Panel)window.FindName("WorkspaceIconBar")).Children.OfType<Button>().Single(button => Equals(button.Tag, "outline")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        foreach (var name in new[] { "CollapseOutlineButton", "ExpandOutlineButton" })
        {
            var button = (Button)window.FindName(name);
            editor.Focus(); button.Focus(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (button.IsKeyboardFocused) throw new Exception("Outline action retained focus decoration");
        }
        var log = (Expander)window.FindName("LogExpander");
        ((ToggleButton)window.FindName("LogToggle")).IsChecked = true; window.UpdateLayout();
        if (!log.IsExpanded || log.ActualHeight < 100) throw new Exception("Status bar cannot open compiler output");
        ((ToggleButton)window.FindName("LogToggle")).IsChecked = false; window.UpdateLayout();
        if (log.ActualHeight > 1) throw new Exception("Closed compiler output still consumes editor space");
        foreach (var owner in new FrameworkElement[] { editor, preview })
        {
            var bar = Descendants<ScrollBar>(owner).First(bar => bar.IsVisible && bar.Orientation == Orientation.Vertical);
            var cap = (RepeatButton)bar.Template.FindName("EndCap", bar);
            if (cap.ActualHeight < 10 || cap.ActualWidth > 8.1) throw new Exception("Scrollbar end cap has wrong size");
            var before = bar.Value;
            ((System.Windows.Input.RoutedCommand)cap.Command).Execute(cap.CommandParameter, cap);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (bar.Value <= before) throw new Exception("Scrollbar end cap did not scroll content");
        }
        var oldY = editor.VerticalOffset;
        editor.ScrollToEnd(); window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var horizontal = Descendants<ScrollBar>(editor).First(bar => bar.IsVisible && bar.Orientation == Orientation.Horizontal);
        var rightCap = (RepeatButton)horizontal.Template.FindName("EndCap", horizontal);
        var oldX = horizontal.Value;
        ((System.Windows.Input.RoutedCommand)rightCap.Command).Execute(rightCap.CommandParameter, rightCap);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (horizontal.Value <= oldX || Math.Abs(rightCap.ActualWidth - 12) > .1 || rightCap.ActualHeight > 8.1)
            throw new Exception("Horizontal end cap did not scroll content");
        editor.ScrollToHorizontalOffset(0); editor.ScrollToVerticalOffset(oldY);
        Invoke(window, "ToggleWorkspace"); window.UpdateLayout();
        if (Math.Abs(footer.TranslatePoint(new Point(), workspace).Y - pdfFooter.TranslatePoint(new Point(), workspace).Y) > .1)
            throw new Exception("Collapsing sidebar moved pane footers");
        Invoke(window, "ToggleWorkspace"); window.UpdateLayout();
    }

    private static void WriteThemePdf(string path)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>" };
        for (var i = 0; i < 3; i++) objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 6 0 R >> >> /Contents 7 0 R >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var content = "BT /F1 22 Tf 50 730 Td (TeXeed - Theme preview) Tj /F1 12 Tf " + string.Concat(Enumerable.Range(0, 24).Select(i => "0 -24 Td (A native workspace for your next paper.) Tj ")) + "ET";
        objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        foreach (var obj in objects) { offsets.Add(text.Length); text.Append($"{offsets.Count} 0 obj\n{obj}\nendobj\n"); }
        var xref = text.Length; text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    }
}
