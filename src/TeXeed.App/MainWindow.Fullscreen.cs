using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace TeXeed.App;

public partial class MainWindow
{
    private bool _fullscreen;
    private Rect _beforeFullscreenBounds;
    private WindowState _beforeFullscreenState;
    private WindowChrome? _beforeFullscreenChrome;
    private bool _shellFullscreenMarked;
    private void InitializeFullscreen()
    {
        Activated += (_, _) => { if (_fullscreen) MarkShellFullscreen(true); };
        Closed += (_, _) => { if (_shellFullscreenMarked) MarkShellFullscreen(false); };
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (_closing || _allowClose) return;
        CloseTitleMenus();
        if (!_fullscreen)
        {
            _beforeFullscreenState = WindowState;
            _beforeFullscreenBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _beforeFullscreenChrome = WindowChrome.GetWindowChrome(this);
            WindowState = WindowState.Normal;
            _fullscreen = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(), GlassFrameThickness = new Thickness(), UseAeroCaptionButtons = false });
            TitleBar.Visibility = Visibility.Collapsed;
            TitleBarRow.Height = new GridLength(0);
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            _fullscreen = false;
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, _beforeFullscreenChrome);
            TitleBar.Visibility = Visibility.Visible;
            TitleBarRow.Height = new GridLength(40);
            Left = _beforeFullscreenBounds.Left; Top = _beforeFullscreenBounds.Top;
            Width = _beforeFullscreenBounds.Width; Height = _beforeFullscreenBounds.Height;
            WindowState = _beforeFullscreenState;
        }
        FullscreenMenuItem.IsChecked = _fullscreen;
        MarkShellFullscreen(_fullscreen);
        UpdateCaptionState(); UpdateTitleBarLayout(); UpdateCompactMenuSize();
        ShowFullscreenToast(_fullscreen ? "已进入全屏 · 按 F11 退出" : "已退出全屏");
    }
    private void MarkShellFullscreen(bool fullscreen)
    {
        // Explicitly notify Explorer: screen-sized bounds alone do not reliably
        // move the taskbar behind a fullscreen window. The Shell owns Win-key
        // activation and restores the taskbar when this window loses activation.
        IFullscreenTaskbar? taskbar = null;
        try
        {
            taskbar = (IFullscreenTaskbar)new FullscreenTaskbar();
            taskbar.HrInit();
            taskbar.MarkFullscreenWindow(new WindowInteropHelper(this).Handle, fullscreen);
            _shellFullscreenMarked = fullscreen;
        }
        catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { if (taskbar is not null) Marshal.FinalReleaseComObject(taskbar); }
    }
    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class FullscreenTaskbar { }
    [ComImport, Guid("602D4995-B13A-429B-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFullscreenTaskbar
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }
    private void ShowFullscreenToast(string text)
    {
        FullscreenToastText.Text = text;
        FullscreenToast.Visibility = Visibility.Visible;
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.6), FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.18))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6))));
        fade.Completed += (_, _) => FullscreenToast.Visibility = Visibility.Collapsed;
        FullscreenToast.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
}
