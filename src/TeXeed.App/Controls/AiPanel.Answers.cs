using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    internal bool HasActiveOperation => _operation is not null;
    internal void RetargetProject(string path) => _project = path;
    private readonly HashSet<MarkdownView> _answerViews = [];
    private static AiTurn? AnswerTurn(MarkdownView view)
    {
        for (DependencyObject? current = view; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: AiTurn turn }) return turn;
        return null;
    }
    private void Answer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MarkdownView view || _answerViews.Contains(view)) return;
        view.ChatMode = true;
        view.TurnNumber = AnswerTurn(view) is { } answerTurn ? _book.Turns.IndexOf(answerTurn) + 1 : _book.Turns.Count + 1;
        view.ImageBaseDirectory = System.IO.Path.GetDirectoryName(_project);
        _answerViews.Add(view);
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy, CommandTarget = view });
        var copyAnswer = new MenuItem { Header = "复制这段回答" };
        copyAnswer.Click += (_, _) =>
        {
            try { view.CopyToClipboard(view.Document.ContentStart, view.Document.ContentEnd); }
            catch (System.Runtime.InteropServices.ExternalException ex) { ShowError(ex); }
        };
        menu.Items.Add(copyAnswer);
        view.ContextMenu = menu;
    }
    private void Answer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MarkdownView view) _answerViews.Remove(view);
    }
}
