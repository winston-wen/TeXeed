# 开发指南

TeXeed 使用 C#、.NET 10 和 WPF，编辑器基于 AvalonEdit，PDF 渲染使用 PDFium。开发需要 Windows x64 与 .NET 10 SDK。以下命令在仓库根目录执行。

## 文档

- [编辑器与工作区](ui-chrome.md)：导航、标签、侧栏、快捷键和主题。
- [AI 工作区](ai-workspace.md)：会话、连接、工具、权限和存储。
- [AI 批注与排版](ai-annotations.md)：圈点、发送、公式和复制。
- [PDF 裁边](pdf-cropping.md)、[打印](pdf-printing.md)。
- [排版与 UI 性能](ui-layout-and-performance.md)：布局原则和性能复测。

## 构建与运行

```powershell
dotnet restore TeXeed.slnx -r win-x64 --locked-mode
dotnet build TeXeed.slnx -c Release --no-restore
dotnet run --project src/TeXeed.App -c Release --no-build
```

排版项目需要 TeX Live、MiKTeX 或 Tectonic。在应用的“排版 → 设置”中检测引擎，也可填写引擎和 SyncTeX 的绝对路径。AI 数学排版需要 `latex`、`xelatex`、`dvisvgm`、AMS 宏包；含中文的公式使用 xeCJK。

## 验证

```powershell
dotnet run --project tests/TeXeed.Tests -c Release --no-build
dotnet run --project tests/TeXeed.EditorTests -c Release --no-build
dotnet run --project tests/TeXeed.PdfTests -c Release --no-build
dotnet run --project tests/TeXeed.AppTests -c Release --no-build
```

测试以非零退出码报告失败。核心测试使用子进程模拟编译器；编辑器、PDF 和应用测试需要可访问鼠标与剪贴板的 Windows 桌面会话。应用测试使用独立的 `TEXEED_HOME`，截图和测试数据默认写入 `artifacts/ui-check`。用 `--output` 指定独立目录。

应用专项测试在命令末尾追加 `-- 参数`：

| 参数 | 验证范围 |
| --- | --- |
| `--welcome-only` | 最近项目、教程、窗口尺寸和主题 |
| `--file-explorer-only`、`--file-priority-only` | 文件目录、按需展开、重命名和优先显示 |
| `--outline-only`、`--outline-preview-only` | 大纲导航、折叠和预览定位 |
| `--titlebar-only`、`--workspace-only`、`--themes-only` | 窗口、菜单、布局和主题 |
| `--viewport-only`、`--build-scheduling-only` | 预览跟随、编译调度和取消 |
| `--completion-only`、`--code-style-only` | 补全和 Markdown 代码样式 |
| `--ai-only`、`--ai-permissions-only` | 模拟 AI 服务、工具和权限 |
| `--ai-history-only`、`--ai-reliability-only` | 会话保存、恢复和长对话 |
| `--ai-pins-only`、`--ai-reading-only`、`--ai-annotations-only` | 批注编辑、确认和主编辑框发送 |
| `--ai-prototype-only` | 原生 AI 界面与原型约定 |
| `--ai-math-scroll-only`、`--ai-math-copy-only`、`--ai-math-proofread-only` | 公式渲染、复制和校对 |
| `--crop-only`、`--layout-only`、`--print-only` | 裁边、版心和打印输出 |
| `--ai-ui-perf-only` | 固定合成资料的 UI 性能简评 |

真实排版专项需要相应 TeX 引擎和宏包。Tectonic 测试使用 `.tools/tectonic/tectonic.exe`；`--outline-preview-only` 使用本机 XeLaTeX。`--print-only` 输出测试文件，`--print-review` 打开人工预览窗口。

当前 AI 原型与脚本测试位于 [prototypes/ai-chat-v02](../prototypes/ai-chat-v02/README.md)。完整测试参数以 `tests/TeXeed.AppTests/Program.cs` 为准。

## 发布

```powershell
.\scripts\publish.ps1 -Output artifacts/TeXeed-check -SkipShortcuts
.\scripts\publish.ps1 -Output artifacts/TeXeed-<版本>
```

默认发布 Windows x64 的共享运行时版本。需要捆绑运行时可加 `-SelfContained`，使用新的输出目录。正式发布更新当前用户桌面、开始菜单及既有 TeXeed 任务栏快捷方式，应用身份统一为 `TeXeed.Desktop`。首次固定到任务栏通过 Windows 界面完成。

发布前核对版本号、构建、测试和产物，提交源码快照。源码包含主题、锁文件和许可证；发布二进制、个人配置与测试输出放在忽略目录中。组件许可证见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 代码与数据

| 目录 | 职责 |
| --- | --- |
| `src/TeXeed.Core` | 项目发现、保存、索引、格式化、编译和 SyncTeX |
| `src/TeXeed.App` | WPF 工作区、编辑器、PDF、AI 和设置 |
| `tests` | 核心、控件和应用回归 |
| `examples` | 示例工程 |
| `prototypes/ai-chat-v02` | AI 交互原型 |
| `shared` | 原生与原型共享资料 |
| `scripts` | 发布和资源生成 |

项目中间文件位于 `.texeed/build`，最近成功的 PDF 与 SyncTeX 位于 `.texeed/preview`。缓存通过源码、资源、依赖和引擎信息验证；过期时显示已有预览并安排更新。手动编译可刷新结果。

应用配置位于 `%LOCALAPPDATA%\TeXeed`，可用 `TEXEED_HOME` 指定独立目录。源码工具保留编码，保存时检测磁盘冲突；编译以当前入口所在目录为工作目录，使用相对路径。项目依赖采用静态解析，动态宏生成的路径需要明确指定；依赖限制在项目根目录内，越界路径和符号链接会提示错误。

教程公式用 `scripts/build-tutorial-math.py` 生成，运行需要 Python、LaTeX 和 dvisvgm。文件图标用 `scripts/build-explorer-icons.py` 生成；应用图标用 `scripts/build-icon.ps1` 生成。
