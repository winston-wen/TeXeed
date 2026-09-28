# TeXeed

原生 Windows LaTeX 编辑器，使用 **C# / .NET 10 / WPF**，编辑器基于 AvalonEdit，PDF 使用本机 PDFium 渲染。

## 运行

发行目录内直接运行 `TeXeed.exe`。也可以把主 `.tex` 文件拖到窗口，或以命令行参数传入：

```powershell
.\TeXeed.exe "D:\Documents\My paper\main.tex"
```

源代码开发需要 Windows 10/11 x64 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

```powershell
dotnet restore TeXeed.slnx
dotnet run --project src/TeXeed.App
```

编译 LaTeX 需要另行安装 [TeX Live](https://www.tug.org/texlive/windows.html) 或 [MiKTeX](https://miktex.org/download)。按 Alt 显示菜单，在“排版 → 设置”中检测引擎或指定可执行文件。支持 `latexmk`、XeLaTeX、pdfLaTeX、LuaLaTeX，以及 [Tectonic](https://tectonic-typesetting.github.io/)。中文项目建议使用 XeLaTeX 和 `ctex`。TeX 发行版及宏包未包含在应用发行包中。

## 工作方式

- 打开主文件，递归加载 `\input`、`\include`、`\subfile`、`\import`、`\subimport`、文献数据库、本地样式和图片资源。文件树展示发现的依赖，文件标签保留编辑状态和撤销历史。
- 开启“实时预览”后，修改经过可配置的延迟，**自动保存全部修改并编译**。关闭后可手动保存、编译。编译在后台进行，失败时保留上次成功的预览。
- XeLaTeX、pdfLaTeX 和 LuaLaTeX 按辅助文件变化和引擎提示决定是否补跑；引用稳定的普通正文修改通常只编译一遍。文献输入未变时复用 BibTeX/Biber 结果，引用、目录或文献改变时自动补齐所需步骤。编译过程中重复保存同一版本会合并，新修改按剩余防抖时间接续编译。
- 编辑器提供 LaTeX/BibTeX 高亮、行号、查找替换、注释、缩进、保守格式化、命令片段和上下文补全。`\ref`、`\eqref`、`\cite` 等补全来自整个项目，包含尚未保存的编辑。
- 按住 Alt 并用左键单击可在不同行列添加多个光标；Alt+左键拖动或中键拖动可进行列框选。输入、粘贴、删除和换行应用到所有光标，一次操作可整体撤销；Esc 或普通单击返回单光标。
- 字体可以在设置中任意选择。编辑区保留所选字体的字形，按固定网格排版：英文及窄字符一格、中文及宽字符两格，Tab 对齐缩进停靠点；光标、选区与命中测试使用同一网格，不修改文档中的字符。
- 编辑区复用已经排版的字形，降低滚动和大纲跳转时的重绘开销。缓存区分字体、字号、颜色、装饰及 DPI，并限制保留数量。
- Ctrl+H 展开当前文件的替换栏，可替换当前匹配或全部替换，支持大小写、全词及正则选项。普通模式按原文插入替换内容；正则模式支持 `$1`、`${name}` 等捕获组。一次全部替换可用一次 Ctrl+Z 撤销。
- PDF 可以缩放、适合宽度、导出。预览栏和设置中的跟随模式提供三种选择：**不跟随**、**跟随光标**、**跟随视图**。跟随光标时，对应文字框垂直居中，文档首尾也可居中；跟随视图随源码滚动定位到可见内容的中间位置，默认使用此模式。切换立即生效并保存，下次启动会恢复；所有模式下 Ctrl+J 都可手动定位到光标。Ctrl+滚轮缩放以鼠标下的 PDF 内容为中心，工具栏缩放和适合宽度保留视口中心的内容。PDF 上**右键双击**或 **Ctrl+左键**返回对应源码。使用 SyncTeX，优先调用发行版的 `synctex.exe`，缺失时使用内置映射读取器。
- 左侧有大纲、文件树、全文搜索、标签搜索和 TODO/FIXME 待办。编译问题可点击跳转源码，完整日志可展开查看。
- 大纲按标题层级显示为可折叠的树，灰色竖线标明缩进，圆角 h1、h2、h3 等标记表示树中的层级。展开三角在行末右对齐，点虚线连接标题与三角。点击三角展开/折叠，点击标题跳转源码；方向键浏览与展开/折叠，Enter 跳转。悬停可查看完整标题、文件名和行号；编辑、保存或切换侧栏后保留折叠状态。
- 单击大纲标题会居中定位源码，同屏内相邻标题也立即重新居中；文档首尾受可滚动范围限制。重复点击同一标题会重新触发当前预览跟随模式，选择“不跟随”时预览保持不动。
- 大纲标题整行都可点击，包括层级标记、点线和上下留白；右侧展开三角只控制折叠。没有子标题的行可点击到最右侧。
- 大纲标题栏提供“全部展开”和“全部折叠”，一次操作所有层级；折叠后只显示顶层标题，展开时也会恢复被隐藏的深层标题。
- PDF 缩放、适合宽度和调整预览区大小时，保留现有页面图像并即时缩放，后台完成新分辨率渲染后再替换，避免先清空页面造成闪烁；连续缩放时丢弃过期结果。
- 主菜单默认隐藏，按 Alt 显示或隐藏；设置、实时预览和停止编译均保留在菜单中。工作区可通过标题旁的箭头或 Ctrl+B 折叠/展开，恢复展开时保留之前的宽度和内容。
- 检测外部修改，保存前检查冲突，避免覆盖磁盘上的新内容。UTF-8 与带 BOM 的 UTF-8/UTF-16/UTF-32 文件保留原编码。无法安全识别的旧编码文件以只读资源显示。

## 快捷键

| 操作 | 快捷键 |
| --- | --- |
| 打开 / 新建 | Ctrl+O / Ctrl+N |
| 显示 / 隐藏主菜单 | Alt |
| 折叠 / 展开工作区 | Ctrl+B |
| 保存全部并编译 | Ctrl+S |
| 编译 | Ctrl+Enter |
| 补全 | Ctrl+Space |
| 注释 / 取消注释 | Ctrl+/ |
| 缩进 / 取消缩进 | Tab / Shift+Tab |
| 格式化 | Ctrl+Shift+F |
| 文件内查找 | Ctrl+F |
| 文件内替换 | Ctrl+H |
| 添加光标 | Alt+左键单击 |
| 列框选 | Alt+左键拖动 / 中键拖动 |
| 返回单光标 | Esc / 普通左键单击 |
| 源码定位 PDF | Ctrl+J |
| PDF 定位源码 | 右键双击 / Ctrl+左键 |
| PDF 缩放 | Ctrl+滚轮 |

## 构建与验证

```powershell
dotnet build TeXeed.slnx -c Release
dotnet run --project tests/TeXeed.Tests -c Release
dotnet run --project tests/TeXeed.EditorTests -c Release
dotnet run --project tests/TeXeed.PdfTests -c Release
dotnet run --project tests/TeXeed.AppTests -c Release
.\scripts\publish.ps1
```

`TeXeed.Tests` 使用真正的子进程模拟引擎，验证编译参数、退出码、取消、旧 PDF 保留和 SyncTeX，同时检查项目解析、保存冲突、索引和格式化。编辑器与 PDF 测试在 Windows STA 线程上运行并加载实际控件/原生库。测试命令返回非零退出码表示失败；不依赖额外的测试框架。

`TeXeed.AppTests` 验证完整 WPF 窗口、多文件保存、异步保存期间的编辑保护、Alt 菜单、侧栏折叠及退出流程，并在 `artifacts/ui-check` 生成界面渲染图。菜单测试需要允许 WPF 获取键盘焦点与鼠标捕获的 Windows 桌面会话；可传入 `--close-only` 单独验证退出流程。如果工作区存在 `.tools/tectonic/tectonic.exe`，还会验证 Ctrl+S 实际编译、PDF 双向定位、视图跟随与自动保存预览；传入 `--viewport-only` 可单独检查视图跟随、手动定位、PDF 浏览/缩放取消同步和旧设置迁移。传入 `--build-scheduling-only` 可单独验证编译请求合并、剩余防抖、取消重试和项目切换。便携测试引擎及缓存不包含在发行包中。

大纲的专项界面检查可运行 `dotnet run --project tests/TeXeed.AppTests -c Release -- --outline-only`，验证层级竖线、折叠状态保留、标题跳转、键盘操作及窄侧栏和放大显示。

传入 `--outline-profile "主文件路径" ["已有 PDF 路径"]` 可只读测量标题点击及编辑区重绘耗时，提供 PDF 时还测量 SyncTeX 定位；使用独立测试设置，不触发编译。首次字形排版通常比重复跳转慢，测量中的 SyncTeX 时间不包含自动跟随的防抖等待和后续 PDF 栅格绘制。

传入 `--outline-interactive` 可打开独立的点击诊断窗口。窗口标题显示大纲跳转次数与目标行，便于人工检查连续单击、留白命中和焦点切换；关闭窗口即可结束。

## 项目结构

- `src/TeXeed.Core`：项目发现与保存、索引、格式化、工具链、编译、SyncTeX。
- `src/TeXeed.App`：WPF 窗口、编辑器、PDF 预览与设置。
- `tests`：核心、控件和本机渲染检查。
- `examples`：可直接打开的示例工程。

编译中间文件放在项目的 `.texeed/build`，最后成功的 PDF 和 SyncTeX 保存在 `.texeed/preview`。应用设置与最近项目默认保存在 `%LOCALAPPDATA%\TeXeed\settings.json`；可用 `TEXEED_HOME` 环境变量指定独立配置目录。

## 当前边界

- 这是 Windows x64 版本；未提供 ARM64 原生构建。
- 文件解析是静态分析，动态 TeX 宏生成的路径不会展开。跨出主文件目录的依赖以及符号链接会被拒绝并提示；请把项目依赖整理到主文件目录中。
- 实时预览仍需让 TeX 引擎处理整份文档；增量复用中间结果并减少重复遍数，耗时取决于工程、引擎和宏包。每次编译最长两分钟，可以手动停止。
- PDF 的反向定位精度取决于编译器产生的 SyncTeX。修改源码后，需更新编译结果再定位，避免误跳行。
- PDF 预览当前以页面渲染和同步定位为重点，不提供 PDF 文字选择、批注或表单编辑。
- 格式化只调整可判断的结构缩进，保留注释和 verbatim 类环境，不替代完整 TeX 宏展开。

组件及第三方许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
