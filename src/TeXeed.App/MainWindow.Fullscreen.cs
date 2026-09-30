using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace TeXeed.App;

public partial class MainWindow
{
    private bool _fullscreen;
    private Rect _beforeFullscreenBounds;
    private WindowState _beforeFullscreenState;
    private WindowChrome? _beforeFullscreenChrome;
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
        UpdateCaptionState(); UpdateTitleBarLayout(); UpdateCompactMenuSize();
        ShowFullscreenToast(_fullscreen ? "已进入全屏 · 按 F11 退出" : "已退出全屏");
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
