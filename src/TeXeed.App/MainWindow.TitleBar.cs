using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using TeXeed.App.Controls;

namespace TeXeed.App;

public partial class MainWindow
{
    private MenuItem[] _titleMenus = [];
    private readonly System.Windows.Controls.Primitives.Popup _compactMenu = new() { AllowsTransparency = true, StaysOpen = false };
    private CompactMenuPanel _compactPanel = null!;
    private bool _compactTitleBar;
    private double _wideMenuWidth;

    private void InitializeTitleBar()
    {
        _titleMenus = MainMenu.Items.OfType<MenuItem>().ToArray();
        _compactPanel = new CompactMenuPanel(_titleMenus, (Style)FindResource("CompactMenuRow"));
        _compactPanel.CloseRequested += (_, _) => HideMainMenu(true);
        _compactMenu.Child = _compactPanel;
        _compactMenu.PlacementTarget = TitleBar;
        _compactMenu.Placement = PlacementMode.Relative;
        _compactMenu.HorizontalOffset = 6;
        _compactMenu.VerticalOffset = 40;
        _compactMenu.Closed += (_, _) => { if (_menuReturnFocus is not null) RestoreMenuFocus(); };
        // Popup panels have their own input route. Preserve the window's
        // menu activation and document shortcuts while focus is in that HWND.
        _compactPanel.PreviewKeyDown += Window_KeyDown;
        _compactPanel.PreviewKeyUp += Window_KeyUp;
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarLayout();
        WindowRoot.SizeChanged += (_, _) => UpdateCompactMenuSize();
        Loaded += (_, _) => UpdateTitleBarLayout();
        Activated += (_, _) => UpdateTitleBarLayout();
        StateChanged += (_, _) => UpdateCaptionState();
        UpdateCaptionState();
        InitializeWindowMenu();
    }

    private void UpdateTitleBarLayout()
    {
        if (_closing || _allowClose || (!_fullscreen && TitleBar.ActualWidth <= 0)) return;
        if (!_compactTitleBar)
        {
            MainMenu.Measure(new Size(double.PositiveInfinity, 40));
            if (MainMenu.DesiredSize.Width > 0) _wideMenuWidth = MainMenu.DesiredSize.Width;
        }
        // Reserve caption buttons and a draggable gap before switching
        // modes. The compact panel reads the same live menu items; it never opens
        // their cascading popups or changes their logical ownership.
        var compact = (_fullscreen && !_fullscreenTitleBarVisible) || TitleBar.ActualWidth < 12 + _wideMenuWidth + 138 + 80;
        if (compact != _compactTitleBar)
        {
            CloseTitleMenus();
            _compactTitleBar = compact;
        }
        MainMenu.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactMenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CaptionTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UpdateCompactMenuSize();
    }

    private void UpdateCaptionState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // WM_GETMINMAXINFO bounds the HWND to the monitor work area; a second
        // client inset would duplicate that correction and waste edge space.
        WindowRoot.Margin = new Thickness();
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "还原" : "最大化");
        MaximizeGlyph.Data = Geometry.Parse(maximized
            ? "M3.5,0.5 H10.5 V7.5 M0.5,3.5 H7.5 V10.5 H0.5 Z"
            : "M0.5,0.5 H10.5 V10.5 H0.5 Z");
    }

    private void CloseTitleMenus()
    {
        foreach (var menu in _titleMenus) menu.IsSubmenuOpen = false;
        _compactMenu.IsOpen = false;
        if (IsWithin(Mouse.Captured as DependencyObject, MainMenu)
            || IsWithin(Mouse.Captured as DependencyObject, _compactPanel)) Mouse.Capture(null);
    }

    private void RestoreMenuFocus()
    {
        var previous = _menuReturnFocus;
        _menuReturnFocus = null;
        if (_closing || _allowClose || !IsActive) return;
        if (previous is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } element && element.Focus()) return;
        if (previous is ContentElement { IsEnabled: true, Focusable: true } content && content.Focus()) return;
        if (Editor.IsVisible && Editor.IsEnabled) Editor.TextArea.Focus();
    }

    private void CompactMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_compactMenu.IsOpen) HideMainMenu(true);
        else ShowMainMenu(FirstMenuItem);
    }

    private void UpdateCompactMenuSize()
    {
        if (_closing || _allowClose) return;
        var titleHeight = TitleBar.IsVisible ? TitleBar.ActualHeight : 0;
        _compactMenu.PlacementTarget = titleHeight > 0 ? TitleBar : WindowRoot;
        _compactMenu.VerticalOffset = titleHeight;
        _compactPanel.Width = Math.Min(420, Math.Max(1, WindowRoot.ActualWidth - 12));
        var available = Math.Max(1, WindowRoot.ActualHeight - titleHeight - 6);
        if (IsLoaded && PresentationSource.FromVisual(TitleBar) is not null)
        {
            var screen = WindowRoot.PointToScreen(new Point(6, titleHeight));
            available = Math.Min(available, WindowMenuSpaceBelow(screen) / VisualTreeHelper.GetDpi(this).DpiScaleY);
        }
        _compactPanel.MaxHeight = Math.Max(1, available);
    }

    private void OpenTitleSubmenu(MenuItem menu)
    {
        if (_compactTitleBar) _compactPanel.NavigateTo(menu);
        else menu.IsSubmenuOpen = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) WindowState = WindowState.Minimized;
        else SystemCommands.MinimizeWindow(this);
    }
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) { ToggleFullscreen(); return; }
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseCaption_Click(object sender, RoutedEventArgs e) => Close();
}
