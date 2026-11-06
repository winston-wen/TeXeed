# 共同文稿 WebView 性能夹具

数据：`webview-results.json`。`sha256` 记录夹具及被测源码。验证当前文件：

```powershell
node tests/performance-baselines/document-040/verify-webview.cjs
```

复测需允许隔离测试使用 Windows DPAPI 和 WebView2；测试会在屏幕外启动真实 WPF 宿主，调用模拟模型而不进行真实模型网络请求：

```powershell
dotnet restore tests/TeXeed.AppTests --ignore-failed-sources -p:NuGetAudit=false
dotnet build tests/TeXeed.AppTests -c Release --no-restore
dotnet tests/TeXeed.AppTests/bin/Release/net10.0-windows/win-x64/TeXeed.AppTests.dll --ai-mentor-integration-only --output artifacts/mentor-integration
```

同一 120 段夹具分别为 0 和 60 条评论，三批，批内预热两次后七次；记录文稿渲染和排队两帧、程序化选择及排队两帧。CDP HeapProfiler 对七次操作的 JS 分配进行采样，包含 major/minor GC 已回收对象；结果是估计值，不代表精确总分配或整个 WPF 进程的总分配。控件数是 WebView DOM 视区内控件数，不含 WPF 控件。视口为屏幕外 1000×800 WPF 宿主。此项不是同路径的历史 WPF 基准，也不能与页面内同步计时直接混比。
