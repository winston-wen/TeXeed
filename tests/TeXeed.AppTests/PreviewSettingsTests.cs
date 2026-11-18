using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static void ClickSettingsButton(SettingsWindow dialog, string text)
    {
        var button = Descendants<Button>(dialog).Single(item => Equals(item.Content, text));
        typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, null);
    }

    private static async Task WithLayoutSettingsAsync(MainWindow window, Action<SettingsWindow> inspect)
    {
        var menu = (Menu)window.FindName("MainMenu");
        var typeset = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "排版(_B)"));
        var settings = typeset.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "设置…"));
        Exception? error = null; var opened = false;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<SettingsWindow>().SingleOrDefault(item => item.Owner == window && item.IsVisible);
            if (dialog is null) { error = new Exception("The layout menu did not open the settings dialog"); return; }
            opened = true;
            try { dialog.UpdateLayout(); inspect(dialog); }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        settings.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (error is not null) throw new Exception("Layout settings interaction failed", error);
        if (!opened) throw new Exception("The layout settings menu did not open its dialog");
    }

    private static async Task CheckPreviewSettingsAsync(MainWindow window, string output)
    {
        var preview = (PdfPreview)window.FindName("Preview");
        var pdf = Path.Combine(output, "preview.pdf"); WriteThemePdf(pdf); await preview.LoadAsync(pdf);
        async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        PreviewOptions Options() => (PreviewOptions)typeof(MainWindow).GetProperty("CurrentPreviewOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var original = Options();
        if (original.MaximumWidthCm != 30 || !original.CompressWhitespace) throw new Exception("Fresh preview defaults are not 30 cm and compressed whitespace");
        await WithLayoutSettingsAsync(window, dialog =>
        {
            ((FrameworkElement)dialog.FindName("PreviewManualZoom")).BringIntoView(); dialog.UpdateLayout();
            Capture(dialog, Path.Combine(output, "settings-dark.png"));
            var width = (TextBox)dialog.FindName("PreviewMaximumWidth"); width.Text = "12.5";
            if (preview.AutoWidthMaximumCm != 12.5 || ((AppSettings)Field(window, "_settings")!).AutoWidthMaximumCm != 12.5)
                throw new Exception("Changing the centimeter cap did not apply immediately");
            var zoomInput = (TextBox)dialog.FindName("PreviewZoomInput");
            var displayed = zoomInput.Text.Trim().TrimEnd('%', '％');
            if (!double.TryParse(displayed, NumberStyles.Float, CultureInfo.CurrentCulture, out var actual) || Math.Abs(actual - preview.Zoom * 100) > .011)
                throw new Exception("Automatic zoom input does not display the constrained scale");
            ((ComboBox)dialog.FindName("FollowModeBox")).SelectedValue = PreviewFollowMode.None;
            ((CheckBox)dialog.FindName("InvertPdfBox")).IsChecked = false;
            var compress = (CheckBox)dialog.FindName("CompressWhitespaceBox"); compress.IsChecked = false;
            if (preview.InvertColors || preview.CompressVerticalWhitespace || Options().Follow != PreviewFollowMode.None)
                throw new Exception("Preview options require an explicit Apply action");
            var crop = (CheckBox)dialog.FindName("AutoCropPdfBox"); crop.IsChecked = false; dialog.UpdateLayout();
            if (compress.IsEnabled || preview.AutoCrop) throw new Exception("Compression control must be disabled without cropping");
            crop.IsChecked = true; dialog.UpdateLayout();
            if (!compress.IsEnabled || compress.IsChecked == true) throw new Exception("Compression preference did not survive disabling crop");
            ((RadioButton)dialog.FindName("PreviewManualZoom")).IsChecked = true; zoomInput.Text = "137.62%"; dialog.UpdateLayout();
            if (preview.IsAutoWidth || Math.Abs(preview.Zoom - 1.3762) > .00001 || width.IsEnabled)
                throw new Exception("Manual zoom did not apply precisely or disabled width still editable");
            zoomInput.Text = "NaN";
            if (Math.Abs(preview.Zoom - 1.3762) > .00001) throw new Exception("Invalid live zoom was accepted");
            ClickSettingsButton(dialog, "取消");
        });
        await Settle();
        if (Options() != original) throw new Exception("Cancel did not restore all opening preferences and automatic scale");
        await WithLayoutSettingsAsync(window, dialog =>
        {
            var width = (TextBox)dialog.FindName("PreviewMaximumWidth"); width.Text = "0";
            if (preview.AutoWidthMaximumCm != 30) throw new Exception("Invalid centimeter cap was accepted");
            width.Text = "22.25";
            ((CheckBox)dialog.FindName("CompressWhitespaceBox")).IsChecked = false;
            ((RadioButton)dialog.FindName("PreviewManualZoom")).IsChecked = true;
            var zoom = (TextBox)dialog.FindName("PreviewZoomInput"); zoom.Text = "500%";
            ((CheckBox)dialog.FindName("AutoCropPdfBox")).IsChecked = false;
            if (preview.AutoCrop || preview.Zoom != 3) throw new Exception("Turning off cropping failed to clamp manual zoom");
            zoom.Text = "137.62%";
            ClickSettingsButton(dialog, "保存设置");
        });
        await Settle();
        if (Math.Abs(preview.Zoom - 1.3762) > .00001 || preview.AutoWidthMaximumCm != 22.25 || preview.CompressVerticalWhitespace)
            throw new Exception("Saving layout settings failed to retain preview options");
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("TEXEED_HOME")!, "settings.json"))))
            if (json.RootElement.GetProperty("AutoWidthMaximumCm").GetDouble() != 22.25 || json.RootElement.GetProperty("CompressPdfWhitespace").GetBoolean())
                throw new Exception("Physical width and compression were not persisted");
        var confirmed = Options();
        await WithLayoutSettingsAsync(window, dialog => { ((CheckBox)dialog.FindName("InvertPdfBox")).IsChecked = !confirmed.Invert; dialog.Close(); });
        if (Options() != confirmed) throw new Exception("Caption close did not roll back live options");
        ThemeManager.Apply("github-light-default");
        await WithLayoutSettingsAsync(window, dialog => { ((FrameworkElement)dialog.FindName("PreviewManualZoom")).BringIntoView(); dialog.UpdateLayout(); Capture(dialog, Path.Combine(output, "settings-light.png")); dialog.Close(); });
        ThemeManager.Apply("ristretto");
        Invoke(window, "ApplyPreviewOptions", original); await WaitForPreviewRenderedAsync(window);
        Console.WriteLine("PASS: layout menu preview settings, live validation/zoom/follow/colors, 30 cm cap, compression, persistence and cancel/close restoration");
    }
}
