using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    internal AiEquationTarget? ResolveEquation(AiTurn? source, string reference, string localKey, out int turnNumber)
    {
        var turn = AiEquationIndex.TargetTurn(_book.Turns, source, reference, out turnNumber);
        return turn is null ? null : AiEquationIndex.Resolve(turn, localKey);
    }

    private void RefreshConversationEquationReferences()
    {
        foreach (var view in _answerLayers.Keys) view.RefreshEquationReferences();
        foreach (var view in _bookmarkQuestions) view.RefreshEquationReferences();
    }

    internal async void NavigateConversationEquation(AiEquationTarget target)
    {
        try
        {
            if (_revisionEditors.ContainsKey(target.TurnId)) CloseRevisionEditor(target.TurnId);
            if (!await NavigateBookmarkAsync(new("", "", "", target.TurnId, target.SegmentId,
                AiBookmarkKind.Equation, -1, -1, -1, null, target.Equation)))
                StatusText.Text = "无法定位引用的公式。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError(ex); }
    }
}
