# 0.4.1 AI 文稿展示性能夹具

在同一台 Windows 机器、Edge/WebView2 及相同测试脚本下，记录 120 段、0 或 60 条评论的重绘和选区；原型与原生宿主的数据分开，不能直接混比。各轮先预热两次、记录七次，计入两帧等待。V8/CDP 采样包含 major/minor GC 已回收对象，但属于估算，不代表 WPF/WebView2 进程总分配。选择由测试脚本设置，不模拟系统输入法。

- `display-041`：原型纯正文与 0.4.0 前后比较；`display-041-historic` 与同场景历史版本 74f01c7 比较，均包含分项采样分配。
- `display-041-full`：完整 TeX 外壳（导言、标题、120 段及图片/Bib 引用命令）；`display-041-plain`：同一原生独立夹具的纯正文对照。完整文稿的图/Bib 仅写引用，未提供真实资源；不代表实际资源读取和加载耗时。
- 优化前候选和最终版的调用栈样本、采样脚本、资源及指纹也在本目录；按最近命名栈帧归因，采样误差与引擎内部归因变化不可忽略。原生“前”样本由 `git archive 0be9eb7` 导出 0.4.0 源码、只额外加入 `AiFullDocumentPerfTests.cs` 与启动分支后构建；对应资源、测试源码、二进制哈希和样本均保存。样本含分项重绘、选区采样分配。原生旧版与新版 HTML 行为存在功能差异，测量差值不能直接全归因于可避免开销。

验证样本与源码指纹：

```powershell
node tests/performance-baselines/display-041/verify.cjs
node tests/performance-baselines/display-041/verify-webview.cjs
node tests/performance-baselines/display-041/verify-profile.cjs
node tests/performance-baselines/display-041-historic/verify.cjs
node tests/performance-baselines/display-041-full/verify.cjs
node tests/performance-baselines/display-041-full/verify-webview.cjs
node tests/performance-baselines/display-041-plain/verify-webview.cjs
```

重新采原型样本前设置 `TEXEED_PLAYWRIGHT_PATH` 和 `TEXEED_BROWSER_PATH`，分别运行各目录 `compare.cjs`、`allocation.cjs` 并传基准提交。原生测试需要在 Windows DPAPI/WebView2 可用环境中执行，使用隔离配置：

```powershell
dotnet build tests/TeXeed.AppTests -c Release --no-restore
dotnet tests/TeXeed.AppTests/bin/Release/net10.0-windows/win-x64/TeXeed.AppTests.dll --ai-full-document-perf-only --output artifacts/full-bench
$env:TEXEED_PERF_PLAIN='1'
dotnet tests/TeXeed.AppTests/bin/Release/net10.0-windows/win-x64/TeXeed.AppTests.dll --ai-full-document-perf-only --output artifacts/plain-bench
Remove-Item Env:TEXEED_PERF_PLAIN
```
