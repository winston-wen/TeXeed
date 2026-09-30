using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace TeXeed.App;

public partial class MainWindow
{
    private MenuItem[] _titleMenus = [];
    private readonly ContextMenu _compactMenu = new();
    private bool _compactTitleBar;
    private double _wideMenuWidth;

    private void InitializeTitleBar()
    {
        _titleMenus = MainMenu.Items.OfType<MenuItem>().ToArray();
        _compactMenu.PlacementTarget = CompactMenuButton;
        _compactMenu.Placement = PlacementMode.Bottom;
        _compactMenu.SetResourceReference(Control.BackgroundProperty, "SidebarBackground");
        _compactMenu.SetResourceReference(Control.ForegroundProperty, "Ink");
        _compactMenu.SetResourceReference(Control.BorderBrushProperty, "Line");
        _compactMenu.Closed += (_, _) => { if (_menuReturnFocus is not null) RestoreMenuFocus(); };
        // ContextMenu popups have their own input route. Preserve the window's
        // menu activation and document shortcuts while focus is in that HWND.
        _compactMenu.PreviewKeyDown += Window_KeyDown;
        _compactMenu.PreviewKeyUp += Window_KeyUp;
        CompactMenuButton.ContextMenu = _compactMenu;
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarLayout();
        Loaded += (_, _) => UpdateTitleBarLayout();
        StateChanged += (_, _) => UpdateCaptionState();
        UpdateCaptionState();
    }

    private void UpdateTitleBarLayout()
    {
        if (_closing || _allowClose || TitleBar.ActualWidth <= 0) return;
        if (!_compactTitleBar)
        {
            MainMenu.Measure(new Size(double.PositiveInfinity, 40));
            if (MainMenu.DesiredSize.Width > 0) _wideMenuWidth = MainMenu.DesiredSize.Width;
        }
        // Reserve the logo, caption buttons and a draggable gap before switching
        // modes. Keep the same menu items so checks, recent projects and handlers
        // remain live in both presentations.
        var compact = TitleBar.ActualWidth < 12 + 44 + _wideMenuWidth + 138 + 80;
        if (compact != _compactTitleBar)
        {
            CloseTitleMenus();
            var source = _compactTitleBar ? _compactMenu.Items : MainMenu.Items;
            var destination = compact ? _compactMenu.Items : MainMenu.Items;
            source.Clear();
            foreach (var item in _titleMenus) destination.Add(item);
            _compactTitleBar = compact;
            MainMenu.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
        CompactMenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CaptionTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCaptionState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // The maximized HWND includes its resize frame outside the work area.
        // Inset client controls so the buttons remain visible on every edge.
        WindowRoot.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness();
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
            || IsWithin(Mouse.Captured as DependencyObject, _compactMenu)) Mouse.Capture(null);
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

    private void AppIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _allowClose) return;
        SystemCommands.ShowSystemMenu(this, AppIconButton.PointToScreen(new Point(0, AppIconButton.ActualHeight)));
    }

    private void AppIcon_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseCaption_Click(object sender, RoutedEventArgs e) => Close();
}
