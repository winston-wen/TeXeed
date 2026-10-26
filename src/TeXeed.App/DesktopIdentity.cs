using System.Runtime.InteropServices;

namespace TeXeed.App;

internal static class DesktopIdentity
{
    // Keep this stable across versions and in sync with update-shortcuts.ps1.
    internal const string AppUserModelId = "TeXeed.Desktop";

    internal static void Initialize()
    {
        var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        // Shell integration is optional; failure must not prevent editing.
        if (result < 0) System.Diagnostics.Debug.WriteLine($"TeXeed shell identity failed: 0x{result:X8}");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
