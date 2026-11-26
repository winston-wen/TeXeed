namespace TeXeed.App.AI;

public enum AiPermission { FullAccess, AskBeforeShell, ReadOnly }

internal sealed record AiPermissionChoice(AiPermission Value, string Name, string Description)
{
    public override string ToString() => Name;
    internal static readonly AiPermissionChoice[] Choices =
    [
        new(AiPermission.FullAccess, "完全访问", "按明确指令直接改稿。自动执行命令、修改文件、访问网络，使用当前 Windows 用户权限。"),
        new(AiPermission.AskBeforeShell, "执行前询问", "保存 AI 会话改稿及执行 Shell 命令前确认。项目源码编辑、保存和编译自动执行。"),
        new(AiPermission.ReadOnly, "只读", "仅使用项目读取与搜索工具；禁止修改、保存、编译及 Shell。")
    ];
    internal static bool Allows(AiPermission permission, string tool) => permission switch
    {
        AiPermission.FullAccess or AiPermission.AskBeforeShell => tool is "ask_user" or "list_files" or "list_directory" or "read_source" or "search_source" or "replace_source" or "create_source" or "save_project" or "compile_project" or "shell" or "list_attachments" or "list_attachment_directory" or "read_attachment" or "read_attachment_media" or "search_attachments",
        _ => tool is "ask_user" or "list_files" or "list_directory" or "read_source" or "search_source" or "list_attachments" or "list_attachment_directory" or "read_attachment" or "read_attachment_media" or "search_attachments"
    };
    internal static string Instructions(AiPermission permission) => permission switch
    {
        AiPermission.FullAccess => "当前会话权限：完全访问。可使用 shell 执行 PowerShell，读写当前用户可访问的文件、运行 Git 和脚本、联网下载及安装任务所需依赖；无需为已授权的常规操作反复询问。工作目录默认项目根，但并非文件系统沙箱。系统管理员权限仍受 Windows 控制。",
        AiPermission.AskBeforeShell => "当前会话权限：执行前询问。保存 AI 改稿时，宿主展示拟修改的轮次和正文，等用户同意后保存。项目源码编辑、保存和编译已允许；调用 shell 时由宿主先解释用途和影响，命令全文可展开，等待用户批准或匹配本会话已有授权。被拒绝的命令不得换写法重试绕过。",
        _ => "当前会话权限：只读。仅可读取和搜索项目资料，不得改写会话、修改文件、保存、编译或运行 Shell；需要执行时说明具体动作及切换权限的原因。"
    };
}
