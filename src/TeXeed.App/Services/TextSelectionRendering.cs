using System.Runtime.CompilerServices;

namespace TeXeed.App.Services;

internal static class TextSelectionRendering
{
    // Must run before any TextBox/PasswordBox caches WPF compatibility switches,
    // including controls hosted by our regression runner.
    [ModuleInitializer]
    internal static void Initialize() => AppContext.SetSwitch(
        "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);
}
