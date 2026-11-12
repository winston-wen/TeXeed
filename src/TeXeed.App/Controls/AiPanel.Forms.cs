using TeXeed.App.AI;
using System.Windows.Controls;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private readonly Dictionary<string, AiForms.Submission> _pendingFormSubmissions = new(StringComparer.Ordinal);
    private bool _formGradeCommitPending;
    private sealed record FormBinding(Control Control, string? Option);
    private readonly Dictionary<(string Conversation, string Turn, string Type, string Id), List<FormBinding>> _formBindings = [];
    private bool _updatingFormBindings;

    private void ConfigureAnswerForms(MarkdownView view, AiTurn turn)
    {
        if (!AiForms.SupportsForms(turn) || !view.Markdown.Contains("{{", StringComparison.Ordinal)) return;
        var conversation = _book.Active;
        var types = AiForms.FieldTypes(turn);
        AiForms.Submission? Submitted() => _pendingFormSubmissions.TryGetValue(turn.Id, out var pending) ? pending : AiForms.SubmittedFor(conversation, turn.Id);
        Dictionary<string, string> Values() => Submitted()?.Values ?? AiForms.Draft(turn);
        string? Ambiguous(string type, string id)
        {
            if (Submitted()?.AmbiguousLegacy?.TryGetValue(id, out var unresolved) == true) return unresolved;
            var values = Values();
            return !AiForms.AllowLegacy(types, id) && !values.ContainsKey(AiForms.FieldKey(type, id)) && values.TryGetValue(id, out var old) ? old : null;
        }
        if (Submitted()?.AmbiguousLegacy is { Count: > 0 } || types.Any(pair => !AiForms.AllowLegacy(types, pair.Key) && Values().ContainsKey(pair.Key)))
            StatusText.Text = "旧表单有不同类型共用同一字段标识，旧值已保留但无法自动归属；请检查并重新填写。";
        void Observe(string type, string id, Control control, string? option)
        {
            var key = (conversation.Id, turn.Id, type, id);
            control.Loaded += (_, _) =>
            {
                if (!_formBindings.TryGetValue(key, out var bindings)) _formBindings[key] = bindings = [];
                if (!bindings.Any(binding => ReferenceEquals(binding.Control, control))) bindings.Add(new(control, option));
            };
            control.Unloaded += (_, _) =>
            {
                if (!_formBindings.TryGetValue(key, out var bindings)) return;
                bindings.RemoveAll(binding => ReferenceEquals(binding.Control, control));
                if (bindings.Count == 0) _formBindings.Remove(key);
            };
        }
        void Write(string type, string id, string value)
        {
            if (_updatingFormBindings || Submitted() is not null) return;
            AiForms.SetDraft(turn, type, id, value); Dirty();
            if (_turnViews.TryGetValue(turn.Id, out var turnView))
                foreach (var answer in turnView.Answers) answer.RefreshFormFeedback(type, id);
            var key = (conversation.Id, turn.Id, type, id);
            if (!_formBindings.TryGetValue(key, out var bindings)) return;
            _updatingFormBindings = true;
            try
            {
                foreach (var binding in bindings.ToArray())
                {
                    if (binding.Control.ToolTip is string tip && tip.StartsWith("旧记录的同名不同类型字段有冲突", StringComparison.Ordinal)) binding.Control.ToolTip = null;
                    if (binding.Control is TextBox text && text.Text != value) text.Text = value;
                    else if (binding.Control is RadioButton radio && radio.IsChecked != (binding.Option == value)) radio.IsChecked = binding.Option == value;
                    else if (binding.Control is CheckBox check && check.IsChecked != (value == "true")) check.IsChecked = value == "true";
                }
            }
            finally { _updatingFormBindings = false; }
        }
        view.ConfigureForms(new AiFormContext(turn.Id,
            (type, id) => AiForms.Value(Values(), type, id, AiForms.AllowLegacy(types, id)),
            Write,
            () => Submitted() is not null,
            (id, purpose, kind) => SelectFormAttachment(turn.Id, id, purpose, kind),
            id => FormAttachmentState(conversation, turn, id), Observe, Ambiguous,
            (type, id) => AiFormGrading.GradeFor(conversation, turn, type, id, types) is { } grade ? new FormFieldFeedback(grade.Result, grade.Comment) : null));
    }
    private void RefreshFormEnabled()
    {
        foreach (var view in _turnViews.Values)
            foreach (var answer in view.Answers) answer.RefreshFormEnabled();
    }
    private void InvalidateAttachmentFeedback(AiEntry entry)
    {
        if (!AiFormAttachmentOrigins.TryGet(entry, out var origin) || origin is null) return;
        var conversation = _book.Conversations.FirstOrDefault(item => item.Id == origin.ConversationId);
        var source = conversation?.Turns.FirstOrDefault(turn => turn.Id == origin.TurnId);
        if (source is null) return;
        AiFormGrading.InvalidateField(source, "file", origin.FieldId); Dirty();
        if (_turnViews.TryGetValue(source.Id, out var view))
            foreach (var answer in view.Answers) answer.RefreshFormFeedback("file", origin.FieldId);
    }

    private async Task<(bool Applied, bool Invalid, bool QueuePaused)> SaveCompletedFormTurnAsync(AiNotebook book, AiTurn completed, IReadOnlySet<string> allowedGradingSources)
    {
        var project = _project ?? throw new InvalidOperationException("项目已关闭。");
        var conversation = book.Active;
        var candidate = completed.AnsweredAt is not null && AiFormGrading.HasCandidate(completed.Answer);
        var applied = false; var paused = false;
        Dictionary<string, System.Text.Json.JsonElement>? oldConversationData = null;
        var oldUpdated = conversation.UpdatedAt;
        Dictionary<string, AiForms.Submission> oldPending = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, System.Text.Json.JsonElement>?> oldTurnData = new(StringComparer.Ordinal);
        IReadOnlyList<string> changed = [];
        void Mutate()
        {
            oldConversationData = conversation.AdditionalData; oldUpdated = conversation.UpdatedAt;
            oldPending = new(_pendingFormSubmissions, StringComparer.Ordinal);
            if (candidate) oldTurnData = book.Turns.GroupBy(turn => turn.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().AdditionalData, StringComparer.Ordinal);
            book.Turns.Add(completed);
            if (candidate)
            {
                var lastSubmissions = book.Turns.SelectMany(AiForms.AllFromTurn).GroupBy(item => item.TurnId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
                applied = AiFormGrading.Apply(conversation, completed, allowedGradingSources, out changed);
                foreach (var id in changed)
                    if (lastSubmissions.TryGetValue(id, out var submission)) _pendingFormSubmissions.TryAdd(id, submission);
                if (applied)
                {
                    var queue = AiFollowUpQueue.Load(conversation);
                    if (!queue.IsPaused && queue.Items.Any(item => item.Kind == "queue" && item.State == "pending"))
                    {
                        queue.Pause("表单已重新开放，请检查答案后决定是否继续排队。"); queue.Store(conversation); paused = true;
                    }
                }
            }
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
        }
        void Rollback()
        {
            book.Turns.Remove(completed);
            foreach (var id in changed)
                if (book.Turns.FirstOrDefault(turn => turn.Id == id) is { } source && oldTurnData.TryGetValue(id, out var previous)) source.AdditionalData = previous;
            conversation.AdditionalData = oldConversationData; conversation.UpdatedAt = oldUpdated;
            _pendingFormSubmissions.Clear();
            foreach (var pair in oldPending) _pendingFormSubmissions[pair.Key] = pair.Value;
        }
        _book.Draft = Composer.Text;
        if (!_restoringReading) _book.Active.ReadingOffset = MessagesScroll.VerticalOffset;
        CaptureReadingAnchor();
        if (_store is not null) await _store.SaveWithMutationAsync(Mutate, Rollback);
        else
        {
            await _saveGate.WaitAsync();
            try
            {
                try { Mutate(); var snapshot = book.Snapshot(); await Task.Run(() => AiStorage.Save(project, snapshot)); }
                catch { Rollback(); throw; }
            }
            finally { _saveGate.Release(); }
        }
        return (applied, candidate && !applied, paused);
    }
}
