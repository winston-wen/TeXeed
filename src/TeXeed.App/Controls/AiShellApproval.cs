using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace TeXeed.App.Controls;
internal sealed record AiDecision(string Choice, string Feedback = "");
internal static class AiShellApproval
{
    internal static Task<AiDecision> RequestAsync(Window owner, string command, string directory, CancellationToken token,
        string intent = "执行项目命令", string impact = "可能读取或修改文件，以及访问网络。")
        => ShowAsync(owner, "允许执行此命令？", $"命令意图：{intent}\n\n影响范围：{impact}\n工作目录：{directory}\n\n所需权限：当前 Windows 用户的文件、程序与网络访问权限，不会自动提升为管理员。\n允许类似：本次会话内，相同目录的简单 Git 只读查询按同类查询记住；其他脚本仅记住完全相同的命令。",
            [("拒绝", "deny"), ("允许一次", "once"), ("允许类似", "similar")], token, command);

    internal static async Task<AiDecision> ShowAsync(Window owner, string title, string question,
        IReadOnlyList<(string Label, string Value)> choices, CancellationToken token, string? command = null)
    {
        token.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource<AiDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window { Owner = owner, Title = title, Width = 700, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        if (command is not null)
        {
            var editor = new TextEditor { Text = command, IsReadOnly = true, ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 13, WordWrap = true, Height = Math.Clamp((command.Count(c => c == '\n') + 2) * 20, 90, 230), Padding = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            editor.SetResourceReference(Control.BackgroundProperty, "EditorBackground");
            editor.SetResourceReference(Control.ForegroundProperty, "EditorForeground");
            editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "Selection");
            panel.Children.Add(new Expander { Header = "查看命令全文", Content = editor, Margin = new Thickness(0, 0, 0, 12) });
        }
        var feedback = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var input = new TextBox { Name = "DecisionFeedback", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "Enter 换行；Ctrl+Enter 提交" };
        var submit = new Button { Content = "提交", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 8, 0, 0) };
        feedback.Children.Add(input); feedback.Children.Add(submit);
        void Complete(string choice, string text = "") { done.TrySetResult(new(choice, text)); dialog.Close(); }
        void Submit() { if (!string.IsNullOrWhiteSpace(input.Text)) Complete("feedback", input.Text.Trim()); else input.Focus(); }
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, value) in choices)
        {
            var button = new Button { Content = label, Tag = value, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 4, 8, 0), MaxWidth = 620 };
            button.Click += (_, _) => Complete(value); buttons.Children.Add(button);
        }
        var other = new Button { Content = "其他意见", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 4, 0, 0) };
        other.Click += (_, _) => { if (feedback.Visibility == Visibility.Visible) Submit(); else { feedback.Visibility = Visibility.Visible; input.Focus(); } };
        buttons.Children.Add(other); panel.Children.Add(buttons); panel.Children.Add(feedback);
        submit.Click += (_, _) => Submit();
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Submit(); } };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.Closed += (_, _) => done.TrySetResult(new("deny"));
        using var cancellation = token.Register(() => owner.Dispatcher.BeginInvoke(() => { done.TrySetCanceled(token); dialog.Close(); }));
        dialog.Show();
        return await done.Task;
    }
}
