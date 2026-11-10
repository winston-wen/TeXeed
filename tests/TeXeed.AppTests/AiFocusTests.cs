using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TeXeed.App;

internal static partial class Program
{
    private static async Task CheckAiFocusAsync(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout());
        if (Descendants<Button>(window).Any(b => Equals(b.Tag, "ai-mark")))
            throw new Exception("PDF marking must not leave a sidebar button");
        Console.WriteLine("PDF marking sidebar removal passed.");
    }
}
