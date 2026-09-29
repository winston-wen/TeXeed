using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace TeXeed.App;

public partial class MainWindow
{
    private Key? _pressedMenuAlt;
    private bool _menuAltAlone;
    private bool _consumeMenuAltRelease;
    private IInputElement? _menuReturnFocus;
    private bool _workspaceExpanded = true;
    private double _expandedWorkspaceWidth = 252;
    private double _expandedWorkspaceMinimum = 200;
    private GridLength _expandedSplitterWidth = new(5);
    private Point? _workspaceIconPress;
    private int _workspaceIconPressTime;
    private bool _workspaceIconClickCompleted;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private bool HandleWorkspaceIconPress(DependencyObject? source, Point position, int timestamp, MouseButton button)
    {
        // The first click can turn the vertical rail into a horizontal bar. Match
        // the second click in window coordinates even if the icon moved away.
        var dpi = VisualTreeHelper.GetDpi(this);
        var doubleClick = button == MouseButton.Left && _workspaceIconClickCompleted && _workspaceIconPress is { } previous
            && unchecked((uint)(timestamp - _workspaceIconPressTime)) <= GetDoubleClickTime()
            && Math.Abs(position.X - previous.X) <= GetSystemMetricsForDpi(36, (uint)dpi.PixelsPerInchX) / dpi.DpiScaleX / 2
            && Math.Abs(position.Y - previous.Y) <= GetSystemMetricsForDpi(37, (uint)dpi.PixelsPerInchY) / dpi.DpiScaleY / 2;
        _workspaceIconPress = null;
        _workspaceIconClickCompleted = false;
        if (doubleClick)
        {
            if (_workspaceExpanded) ToggleWorkspace();
            return true;
        }
        if (button != MouseButton.Left) return false;
        foreach (var icon in WorkspaceIconBar.Children.OfType<Button>().Concat(WorkspaceRailIcons.Children.OfType<Button>()))
        {
            if (icon.Tag is not string || !IsWithin(source, icon)) continue;
            _workspaceIconPress = position;
            _workspaceIconPressTime = timestamp;
            break;
        }
        return false;
    }

    private bool HandleMenuKeyDown(KeyEventArgs e)
    {
        var handled = ProcessMenuKeyDown(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, e.IsRepeat);
        if (handled) e.Handled = true;
        return handled;
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (ProcessMenuKeyUp(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    // These two methods keep the gesture state independent of the input device:
    // Alt release is meaningful only after an unmodified press in this window.
    private bool ProcessMenuKeyDown(Key key, ModifierKeys modifiers, bool isRepeat = false)
    {
        if (_closing || _allowClose) { ResetMenuAltGesture(); return false; }
        // WPF's built-in Alt/F10 activation independently restores menu focus.
        // We own that activation so it cannot undo our collapsed-menu state.
        MainMenu.IsMainMenu = false;
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            if (isRepeat) return _consumeMenuAltRelease;
            if (_pressedMenuAlt is not null)
            {
                _menuAltAlone = false;
                return _consumeMenuAltRelease;
            }
            _pressedMenuAlt = key;
            _menuAltAlone = (modifiers & ~ModifierKeys.Alt) == ModifierKeys.None;
            _consumeMenuAltRelease = _menuAltAlone;
            return _consumeMenuAltRelease;
        }

        if (_pressedMenuAlt is not null) _menuAltAlone = false;

        // Never consume system shortcuts such as Alt+Tab/Alt+F4, or AltGr text.
        if (modifiers == ModifierKeys.Alt && FindMenuAccessKey(key) is { } menu)
        {
            ShowMainMenu(menu);
            menu.IsSubmenuOpen = true;
            return true;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None && MainMenu.Visibility == Visibility.Visible)
        {
            // The menu's own keyboard handler closes the innermost popup first.
            if (MainMenu.Items.OfType<MenuItem>().Any(item => item.IsSubmenuOpen)) return false;
            HideMainMenu(restoreFocus: true);
            return true;
        }
        return false;
    }

    private bool ProcessMenuKeyUp(Key key, ModifierKeys modifiers)
    {
        if (_closing || _allowClose) { ResetMenuAltGesture(); return false; }
        if (key is not (Key.LeftAlt or Key.RightAlt) || _pressedMenuAlt != key) return false;
        var toggle = _menuAltAlone && (modifiers & ~ModifierKeys.Alt) == ModifierKeys.None;
        var handled = _consumeMenuAltRelease;
        ResetMenuAltGesture();
        if (toggle)
        {
            if (MainMenu.Visibility == Visibility.Visible) HideMainMenu(restoreFocus: true);
            else ShowMainMenu(FirstMenuItem);
        }
        return handled;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Alt+Tab may never deliver its key-up to this window.
        ResetMenuAltGesture();
        if (_closing || _allowClose) return;
        if (MainMenu.Visibility == Visibility.Visible) HideMainMenu(restoreFocus: false);
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing || _allowClose) return;
        if (HandleWorkspaceIconPress(e.OriginalSource as DependencyObject, e.GetPosition(this), e.Timestamp, e.ChangedButton))
        { e.Handled = true; return; }
        _menuAltAlone = false;
        if (MainMenu.Visibility == Visibility.Visible && !IsWithin(e.OriginalSource as DependencyObject, MainMenu))
            HideMainMenu(restoreFocus: false);
    }

    private void ResetMenuAltGesture()
    {
        _pressedMenuAlt = null;
        _menuAltAlone = false;
        _consumeMenuAltRelease = false;
    }

    private MenuItem? FindMenuAccessKey(Key key)
    {
        var name = key.ToString();
        if (name.Length != 1) return null;
        foreach (var menu in MainMenu.Items.OfType<MenuItem>().Where(item => item.IsEnabled))
        {
            var header = menu.Header?.ToString() ?? "";
            for (var i = 0; i < header.Length - 1; i++)
            {
                if (header[i] != '_') continue;
                if (header[i + 1] == '_') { i++; continue; }
                if (char.ToUpperInvariant(header[i + 1]) == char.ToUpperInvariant(name[0])) return menu;
                break;
            }
        }
        return null;
    }

    private void ShowMainMenu(MenuItem selectedItem)
    {
        if (_closing || _allowClose || !IsLoaded || !IsVisible) return;
        MainMenu.IsMainMenu = false;
        if (MainMenu.Visibility != Visibility.Visible)
        {
            var previous = Keyboard.FocusedElement;
            _menuReturnFocus = IsWithin(previous as DependencyObject, MainMenu) ? null : previous;
            MainMenu.Visibility = Visibility.Visible;
            MainMenu.UpdateLayout();
        }
        selectedItem.Focus();
    }

    private void HideMainMenu(bool restoreFocus)
    {
        var previous = _menuReturnFocus;
        _menuReturnFocus = null;
        foreach (var item in MainMenu.Items.OfType<MenuItem>()) item.IsSubmenuOpen = false;
        if (IsWithin(Mouse.Captured as DependencyObject, MainMenu)) Mouse.Capture(null);
        MainMenu.Visibility = Visibility.Collapsed;
        if (!restoreFocus || _closing || _allowClose || !IsActive) return;
        if (previous is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } element)
        {
            if (element.Focus()) return;
        }
        else if (previous is ContentElement { IsEnabled: true, Focusable: true } content && content.Focus()) return;
        if (Editor.IsVisible && Editor.IsEnabled) Editor.TextArea.Focus();
    }

    private void ToggleWorkspace_Click(object sender, RoutedEventArgs e) => ToggleWorkspace();

    private void ToggleWorkspace()
    {
        if (_closing || _allowClose) return;
        if (_workspaceExpanded)
        {
            var focusWasInSidebar = SidebarPanel.IsKeyboardFocusWithin;
            var returnFocusWasInSidebar = IsWithin(_menuReturnFocus as DependencyObject, SidebarPanel);
            _expandedWorkspaceMinimum = SidebarColumn.MinWidth;
            _expandedWorkspaceWidth = Math.Max(_expandedWorkspaceMinimum,
                SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth
                    : SidebarColumn.Width.IsAbsolute ? SidebarColumn.Width.Value : 252);
            _expandedSplitterWidth = SidebarSplitterColumn.Width;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarColumn.MinWidth = 42;
            SidebarColumn.Width = new GridLength(42);
            CollapsedWorkspaceRail.Visibility = Visibility.Visible;
            _workspaceExpanded = false;
            if (returnFocusWasInSidebar) _menuReturnFocus = Editor.TextArea;
            if ((focusWasInSidebar || returnFocusWasInSidebar) && Editor.IsVisible && Editor.IsEnabled) Editor.TextArea.Focus();
        }
        else
        {
            var focusWasOnRail = CollapsedWorkspaceRail.IsKeyboardFocusWithin;
            CollapsedWorkspaceRail.Visibility = Visibility.Collapsed;
            SidebarColumn.MinWidth = _expandedWorkspaceMinimum;
            SidebarColumn.Width = new GridLength(_expandedWorkspaceWidth);
            SidebarSplitterColumn.Width = _expandedSplitterWidth;
            SidebarSplitter.Visibility = Visibility.Visible;
            SidebarPanel.Visibility = Visibility.Visible;
            _workspaceExpanded = true;
            if (focusWasOnRail) SidebarPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
        WorkspaceVisibilityMenuItem.IsChecked = _workspaceExpanded;
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            // Menu popup visuals live in a separate HWND; item ownership bridges
            // that boundary when deciding whether focus/capture is in our menu.
            if (element is MenuItem item && ItemsControl.ItemsControlFromItemContainer(item) is { } owner)
            { element = owner; continue; }
            var parent = element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : null;
            element = parent ?? LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
}
