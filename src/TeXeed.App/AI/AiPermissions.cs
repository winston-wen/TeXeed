namespace TeXeed.App.AI;

public enum AiPermission { FullAccess, AskBeforeShell, ReadOnly }

internal sealed record AiPermissionChoice(AiPermission Value, string Name, string Description)
{
    public override string ToString() => Name;
    internal static readonly AiPermissionChoice[] Choices =
    [
        new(AiPermission.FullAccess, "完全访问", "自动执行命令、修改文件、访问网络；使用当前 Windows 用户权限，不限于项目目录。"),
        new(AiPermission.AskBeforeShell, "执行前询问", "项目源码编辑、保存和编译自动执行；每条 PowerShell 命令展示内容并等待你确认。"),
        new(AiPermission.ReadOnly, "只读", "仅使用项目读取与搜索工具；禁止修改、保存、编译及 Shell。")
    ];
    internal static bool Allows(AiPermission permission, string tool) => permission switch
    {
        AiPermission.FullAccess or AiPermission.AskBeforeShell => tool is "list_files" or "list_directory" or "read_source" or "search_source" or "replace_source" or "create_source" or "save_project" or "compile_project" or "shell",
        _ => tool is "list_files" or "list_directory" or "read_source" or "search_source"
    };
    internal static string Instructions(AiPermission permission) => permission switch
    {
        AiPermission.FullAccess => "当前会话权限：完全访问。可使用 shell 执行 PowerShell，读写当前用户可访问的文件、运行 Git 和脚本、联网下载及安装任务所需依赖；无需为已授权的常规操作反复询问。工作目录默认项目根，但并非文件系统沙箱。系统管理员权限仍受 Windows 控制。",
        AiPermission.AskBeforeShell => "当前会话权限：执行前询问。项目源码编辑、保存和编译已允许；调用 shell 时由宿主展示完整命令与工作目录，等待用户批准。被拒绝的命令不得换写法重试绕过。",
        _ => "当前会话权限：只读。仅可读取和搜索项目资料，不得修改、保存、编译或运行 Shell；需要执行时说明具体动作及切换权限的原因。"
    };
}
