using TeXeed.App.Controls;
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
    private double _expandedWorkspaceWidth = 294;
    private double _expandedWorkspaceMinimum = 242;
    private GridLength _expandedSplitterWidth = new(2);

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
            OpenTitleSubmenu(menu);
            return true;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None && (MainMenu.IsKeyboardFocusWithin || _compactMenu.IsOpen || _titleMenus.Any(item => item.IsSubmenuOpen) || _fullscreenTitleBarVisible))
        {
            if (_compactMenu.IsOpen && _compactPanel.GoBack()) return true;
            // The menu's own keyboard handler closes the innermost popup first.
            if (_titleMenus.Any(item => item.IsSubmenuOpen)) return false;
            HideMainMenu(restoreFocus: true);
            SetFullscreenTitleBarVisible(false);
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
            if (_fullscreen)
            {
                if (_fullscreenTitleBarVisible) { HideMainMenu(restoreFocus: true); SetFullscreenTitleBarVisible(false); }
                else ShowMainMenu(FirstMenuItem);
            }
            else if (MainMenu.IsKeyboardFocusWithin || _compactMenu.IsOpen) HideMainMenu(restoreFocus: true);
            else ShowMainMenu(FirstMenuItem);
        }
        return handled;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Alt+Tab may never deliver its key-up to this window.
        ResetMenuAltGesture();
        if (_closing || _allowClose) return;
        CloseTitleMenus();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing || _allowClose) return;
        _menuAltAlone = false;
        if (!IsWithin(e.OriginalSource as DependencyObject, MainMenu)
            && !IsWithin(e.OriginalSource as DependencyObject, _compactPanel)
            && !IsWithin(e.OriginalSource as DependencyObject, CompactMenuButton)) CloseTitleMenus();
        if (!IsWithin(e.OriginalSource as DependencyObject, TitleBar)
            && !IsWithin(e.OriginalSource as DependencyObject, _compactPanel)) SetFullscreenTitleBarVisible(false);
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
        foreach (var menu in _titleMenus.Where(item => item.IsEnabled && LiteralText.GetRecognizesAccessKey(item)))
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
        SetFullscreenTitleBarVisible(true);
        MainMenu.IsMainMenu = false;
        if (_compactTitleBar)
        {
            if (!_compactMenu.IsOpen)
            {
                _menuReturnFocus = Keyboard.FocusedElement;
                UpdateCompactMenuSize();
                _compactPanel.Open();
                _compactMenu.IsOpen = true;
                _compactPanel.UpdateLayout();
            }
            _compactPanel.FocusItem(selectedItem);
            return;
        }
        if (!IsWithin(Keyboard.FocusedElement as DependencyObject, MainMenu))
        {
            _menuReturnFocus = Keyboard.FocusedElement;
        }
        if (MainMenu.Visibility != Visibility.Visible)
        {
            MainMenu.Visibility = Visibility.Visible;
            MainMenu.UpdateLayout();
        }
        selectedItem.Focus();
    }

    private void HideMainMenu(bool restoreFocus)
    {
        var previous = _menuReturnFocus;
        _menuReturnFocus = null;
        CloseTitleMenus();
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
                    : SidebarColumn.Width.IsAbsolute ? SidebarColumn.Width.Value : 294);
            _expandedSplitterWidth = SidebarSplitterColumn.Width;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarColumn.MinWidth = 42;
            SidebarColumn.Width = new GridLength(42);
            _workspaceExpanded = false;
            if (returnFocusWasInSidebar) _menuReturnFocus = Editor.TextArea;
            if ((focusWasInSidebar || returnFocusWasInSidebar) && Editor.IsVisible && Editor.IsEnabled) Editor.TextArea.Focus();
        }
        else
        {
            var focusWasOnRail = WorkspaceRail.IsKeyboardFocusWithin;
            SidebarColumn.MinWidth = _expandedWorkspaceMinimum;
            SidebarColumn.Width = new GridLength(_expandedWorkspaceWidth);
            SidebarSplitterColumn.Width = _expandedSplitterWidth;
            SidebarSplitter.Visibility = Visibility.Visible;
            SidebarPanel.Visibility = Visibility.Visible;
            _workspaceExpanded = true;
            if (_sidebar == "files" && _catalogDirty) InvalidateDirectoryCatalog();
            if (focusWasOnRail) SidebarPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
        WorkspaceVisibilityMenuItem.IsChecked = _workspaceExpanded;
        UpdateWorkspaceIcons();
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
