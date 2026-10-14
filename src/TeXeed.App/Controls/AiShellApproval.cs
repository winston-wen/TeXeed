using System.Windows;
using System.Windows.Controls;

namespace TeXeed.App.Controls;

internal static class AiShellApproval
{
    internal static async Task<bool> RequestAsync(Window owner, string command, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window { Owner = owner, Title = "允许执行此命令？", Width = 660, Height = 390, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var deny = new Button { Content = "拒绝", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 10, 0) };
        var allow = new Button { Content = "允许本次执行", Padding = new Thickness(16, 7, 16, 7) };
        buttons.Children.Add(deny); buttons.Children.Add(allow); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var info = new TextBlock { Text = "PowerShell · 当前 Windows 用户权限\n工作目录：" + directory, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(info, Dock.Top); panel.Children.Add(info);
        panel.Children.Add(new TextBox { Text = command, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = panel;
        deny.Click += (_, _) => dialog.Close();
        allow.Click += (_, _) => { done.TrySetResult(true); dialog.Close(); };
        dialog.Closed += (_, _) => done.TrySetResult(false);
        using var cancellation = token.Register(() => owner.Dispatcher.BeginInvoke(() => { done.TrySetCanceled(token); dialog.Close(); }));
        dialog.Show();
        return await done.Task;
    }
}
