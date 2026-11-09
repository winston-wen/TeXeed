using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TeXeed.App;

internal static partial class Program
{
    private static async Task CheckAiFocusAsync(MainWindow window)
    {
        var button = Descendants<Button>(window).Single(b => Equals(b.Tag, "ai-mark"));
        button.ApplyTemplate();
        Keyboard.Focus(button);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout());
        var chrome = (Border)button.Template.FindName("Chrome", button);
        if ((bool)Field(window, "_aiMarking")!) throw new Exception("AI marking did not turn off");
        if (!button.IsKeyboardFocused) throw new Exception("Regression requires retained input focus");
        if (chrome.BorderBrush is not SolidColorBrush brush || brush.Color.A != 0)
            throw new Exception("Mouse focus left a yellow border after AI marking was disabled");
        if (button.FocusVisualStyle is null)
            throw new Exception("Keyboard navigation must retain a focus visual");
        Console.WriteLine("AI marking focus regression passed.");
    }
}
