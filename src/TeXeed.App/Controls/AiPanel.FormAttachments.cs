using Microsoft.Win32;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private async void SelectFormAttachment(string turnId, string id, string purpose, string kind)
    {
        var conversation = _book.Active;
        var turnIndex = conversation.Turns.FindIndex(turn => turn.Id == turnId);
        bool Available() => !_disposed && ReferenceEquals(_book.Active, conversation)
            && conversation.Turns.Any(turn => turn.Id == turnId)
            && !_pendingFormSubmissions.ContainsKey(turnId) && AiForms.SubmittedFor(conversation, turnId) is null;
        if (turnIndex < 0 || !Available()) return;
        string[] paths;
        if (kind == "directory")
        {
            var dialog = new OpenFolderDialog { Title = "选择要引用的文件夹" };
            if (dialog.ShowDialog() != true) return;
            paths = [dialog.FolderName];
        }
        else
        {
            var dialog = new OpenFileDialog { Title = kind == "file" ? "选择要引用的文件" : "选择要上传的文件", Multiselect = true };
            if (dialog.ShowDialog() != true) return;
            paths = dialog.FileNames;
        }
        if (!Available()) { StatusText.Text = "会话已切换或表单已提交，请重新操作。"; return; }
        var origin = new AiFormAttachmentOrigin(1, conversation.Id, conversation.Title, turnId, turnIndex + 1, id, purpose);
        if (kind == "file") AddFileReferences(paths, origin, Available);
        else await AddAttachmentsAsync(paths, origin, Available);
        RefreshFormEnabled();
    }

    private AiFormAttachmentState FormAttachmentState(AiConversation conversation, AiTurn turn, string fieldId)
    {
        var submission = _pendingFormSubmissions.TryGetValue(turn.Id, out var pending) ? pending : AiForms.SubmittedFor(conversation, turn.Id);
        if (submission is not null)
        {
            var allowLegacy = submission.Attachments is not null || AiForms.AllowLegacy(AiForms.FieldTypes(turn), fieldId);
            if (!allowLegacy && submission.Values.ContainsKey(fieldId))
                return new(0, "旧值同时对应多种控件，无法确定附件归属，请核对原始记录：\n" + submission.Values[fieldId]);
            var files = AiFormAttachmentOrigins.ReadSelection(submission, fieldId, allowLegacy);
            var lockedDescription = files.Length == 0 ? "未提交附件" : "本次提交：\n" + string.Join("\n", files);
            if (submission.AmbiguousLegacy?.TryGetValue(fieldId, out var old) == true)
                lockedDescription += "\n旧值同时对应多种控件，无法确定归属，请核对原始记录：\n" + old;
            return new(files.Length, lockedDescription);
        }
        var entries = conversation.Entries.Where(entry => AiFormAttachmentOrigins.TryGet(entry, out var origin)
            && origin!.ConversationId == conversation.Id && origin.TurnId == turn.Id && origin.FieldId == fieldId).ToArray();
        var count = entries.Where(entry => entry.AttachmentSelected).Sum(entry => entry.AttachmentItems.Count);
        var description = string.Join("\n", entries.SelectMany(entry => entry.AttachmentItems.Select(item =>
            (entry.AttachmentSelected ? "待发送：" : "未勾选：") + item.DisplayPath)));
        var previous = conversation.Turns.SelectMany(AiForms.AllFromTurn).LastOrDefault(item => item.TurnId == turn.Id);
        var allowLegacyPrevious = previous?.Attachments is not null || AiForms.AllowLegacy(AiForms.FieldTypes(turn), fieldId);
        var previousFiles = previous is null ? [] : AiFormAttachmentOrigins.ReadSelection(previous, fieldId, allowLegacyPrevious);
        if (previousFiles.Length > 0) description = "上次提交（不会自动再次上传）：\n" + string.Join("\n", previousFiles) + (description.Length == 0 ? "" : "\n" + description);
        return new(count, description, previousFiles.Length);
    }
}
