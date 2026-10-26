using System.Runtime.CompilerServices;

namespace TeXeed.App.Services;

internal static class TextSelectionRendering
{
    // Select native text highlighting before TextBox/PasswordBox cache WPF compatibility switches.
    [ModuleInitializer]
    internal static void Initialize() => AppContext.SetSwitch(
        "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);
}
