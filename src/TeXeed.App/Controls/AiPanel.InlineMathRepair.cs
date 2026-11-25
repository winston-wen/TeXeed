using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    internal async Task<IReadOnlyDictionary<string, string>> RepairRevisionInlineMathAsync(
        IReadOnlyList<AiRevisionFormulaIssue> issues, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (issues.Count == 0) return new Dictionary<string, string>();
        if (Settings.IsBusy) throw new InvalidOperationException("AI 设置正在连接，请在连接完成后重新保存。");
        if (!AiSettingsPanel.HasConnection()) throw new InvalidOperationException("请先在 AI 设置中登录 ChatGPT 或保存 API Key，再修正行内公式。");
        if (ConversationModelProblem() is { } modelProblem) throw new InvalidOperationException(modelProblem);
        var model = ConversationModelId ?? throw new InvalidOperationException("请选择用于修正行内公式的 AI 模型。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            return await new AiResponses(_connection).RepairInlineMathAsync(model, ConversationEffort, issues, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new InvalidOperationException("AI 修正行内公式超时，请重新保存或手动修正。");
        }
    }
}
