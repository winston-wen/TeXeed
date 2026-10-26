using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace TeXeed.App.Controls;

public sealed partial class LatexEditor
{
    public static RoutedUICommand ToggleReplaceCommand { get; } = new("替换", nameof(ToggleReplaceCommand), typeof(LatexEditor));
    private StackPanel _replacementControls = null!;
    private TextBox _replaceText = null!;
    private TextBlock _replacementStatus = null!;

    private void InitializeSearchUi()
    {
        var assembly = typeof(LatexEditor).Assembly.GetName().Name;
        var resources = new ResourceDictionary { Source = new Uri($"/{assembly};component/Controls/EditorSearchTheme.xaml", UriKind.Relative) };
        _search.Template = (ControlTemplate)resources["EditorSearchTemplate"];
        _search.Resources[typeof(Button)] = resources["SearchActionButton"];
        _search.Resources[typeof(CheckBox)] = resources["SearchCheckBox"];
        _search.MarkerBrush = Brush("SearchMatch");
        _replaceText = new TextBox
        {
            Name = "ReplaceTextBox", Height = 28, Padding = new Thickness(4, 1, 4, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "替换文本；正则模式支持 $1 和 ${name}。Enter 替换当前匹配。"
        };
        System.Windows.Automation.AutomationProperties.SetName(_replaceText, "替换为");
        _replaceText.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            { RunReplacement(false); e.Handled = true; }
        };
        var buttons = new UniformGrid { Columns = 2 };
        var current = new Button { Name = "ReplaceCurrentButton", Content = "替换当前", Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(0, 5, 3, 0) };
        var all = new Button { Name = "ReplaceAllButton", Content = "全部替换", Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(3, 5, 0, 0) };
        current.Click += (_, _) => RunReplacement(false);
        all.Click += (_, _) => RunReplacement(true);
        buttons.Children.Add(current); buttons.Children.Add(all);
        _replacementStatus = new TextBlock { Name = "ReplacementStatus", Text = "仅替换当前文件 · Ctrl+Z 撤销", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush("Muted"), Margin = new Thickness(0, 6, 0, 0) };
        _replacementControls = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        _replacementControls.Children.Add(new TextBlock { Text = "替换为", FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
        _replacementControls.Children.Add(_replaceText);
        _replacementControls.Children.Add(buttons);
        _replacementControls.Children.Add(_replacementStatus);
        _search.Tag = _replacementControls;
        _search.SearchOptionsChanged += (_, _) => ResetReplacementUi();
        _replaceText.TextChanged += (_, _) => ResetReplacementUi();
        _search.CommandBindings.Add(new CommandBinding(ToggleReplaceCommand, (_, e) =>
        {
            SetReplacementVisible(_replacementControls.Visibility != Visibility.Visible);
            e.Handled = true;
        }));
    }

    private void OpenSearch(bool replace)
    {
        ResetMultiCaret();
        if (CurrentFilePath is null) return;
        CloseCompletion();
        if ((!replace || _search.IsClosed) && !_search.IsKeyboardFocusWithin
            && !string.IsNullOrWhiteSpace(SelectedText) && !SelectedText.Contains('\n'))
            _search.SearchPattern = _search.UseRegex ? System.Text.RegularExpressions.Regex.Escape(SelectedText) : SelectedText;
        SetReplacementVisible(replace);
        // Opening immediately after a project becomes visible must first create
        // the editor's adorner layer, otherwise AvalonEdit remembers an unmounted panel.
        ApplyTemplate();
        UpdateLayout();
        _search.Open();
        _search.ApplyTemplate();
        _search.UpdateLayout();
        _search.Reactivate();
    }

    private void SetReplacementVisible(bool visible)
    {
        _replacementControls.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ResetReplacementUi();
    }

    private void ResetReplacementUi() => _replacementStatus.Text = "仅替换当前文件 · Ctrl+Z 撤销";

    private void RunReplacement(bool all)
    {
        var result = all ? ReplaceAll(_replaceText.Text) : ReplaceCurrent(_replaceText.Text);
        _replacementStatus.Text = result.Message;
        // Return successful edits to the document so Ctrl+Z undoes the replacement,
        // rather than undoing text typed into the replacement input.
        if (result.Count > 0) TextArea.Focus();
        else _replaceText.Focus();
    }
}
