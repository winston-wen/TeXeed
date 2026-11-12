using System.Windows;
using System.Windows.Controls;

namespace TeXeed.App.Controls;

internal sealed record AiFormAttachmentState(int Count, string Description, int PreviousCount = 0);

internal sealed class FormAttachmentButton : Button
{
    private readonly AiFormContext _forms;
    private readonly string _id;
    private readonly string _purpose;

    internal FormAttachmentButton(AiFormContext forms, string id, string purpose)
    {
        _forms = forms; _id = id; _purpose = purpose;
        SetResourceReference(StyleProperty, typeof(Button));
        Margin = new Thickness(3, 0, 3, 0); Padding = new Thickness(8, 2, 8, 2);
        System.Windows.Automation.AutomationProperties.SetName(this, "添加附件：" + purpose);
        var menu = new ContextMenu();
        void Add(string title, string tip, string kind)
        {
            var item = new MenuItem { Header = title, ToolTip = tip };
            item.Click += (_, _) => { if (!_forms.IsLocked()) _forms.PickAttachment?.Invoke(_id, _purpose, kind); };
            menu.Items.Add(item);
        }
        Add("上传文件", "保存所选文件的内容快照。", "upload");
        Add("引用文件", "引用文件路径，按需读取当前内容。", "file");
        Add("引用文件夹", "引用文件夹路径，按需读取当前内容。", "directory");
        ContextMenu = menu;
        Click += (_, _) => { if (!_forms.IsLocked()) { menu.PlacementTarget = this; menu.IsOpen = true; } };
        Refresh();
    }

    internal void Refresh()
    {
        var state = _forms.AttachmentState?.Invoke(_id) ?? new AiFormAttachmentState(0, "");
        var status = state.Count > 0 ? $" · {(_forms.IsLocked() ? "已提交" : "已选择")} {state.Count} 项" : "";
        if (!_forms.IsLocked() && state.PreviousCount > 0) status += $" · 上次提交 {state.PreviousCount} 项";
        Content = _purpose + " · 添加附件" + status;
        ToolTip = state.Description.Length == 0 ? _purpose : _purpose + "\n" + state.Description;
        IsEnabled = !_forms.IsLocked();
    }
}

public sealed partial class MarkdownView
{
    private Control CreateFormAttachmentControl(AiFormContext forms, string id, string purpose) => new FormAttachmentButton(forms, id, purpose);
}
