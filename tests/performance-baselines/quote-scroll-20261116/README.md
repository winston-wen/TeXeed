# 引文与滚动条原生性能夹具

`interaction-deep.cs.txt` 用合成的中文文字和四个公式构造引文，并在相同窗口中测纯文字引文和纵向滚动条。`math-svg/` 是这些公式的 SVG 热缓存，由 dvisvgm 3.6 生成；文件名对应应用的公式缓存键，包含 0、1、D 和夹具中的优势公式，长公式保留行内、行间两种缓存键。不含用户会话、项目文件或个人数据。测量时夹具将它们复制到独立的 `TEXEED_HOME`，不会访问用户的数据。

每项预热两次，采七次；窗口为 900×760 DIP。每次配对等待 1.6 秒，覆盖旧滚动条的延迟和淡出，记录即时与配对耗时、UI 线程分配、可见控件数和公式视觉元素数。窗口不可最小化，指针不可悬停在被测滚动条；本夹具检查窗口可见性。它测试热缓存，不含冷启动编译、后台线程、GPU 或物理鼠标开销。

同目录 `compare-pre-a/b.json` 与 `compare-batched-a/b.json` 是批量替换优化前后交错的中间版样本；`native-before.json` 为 0.5.2 改前，`native-after-batched-intermediate.json` 为中间版 120 轮。其原始夹具为 [`../steering-20261112/panel-fixture.cs.txt`](../steering-20261112/panel-fixture.cs.txt)，历史同场景最佳值和条件见 [`../graph-canvas-20261111/README.md`](../graph-canvas-20261111/README.md) 与 [`../scroll-wheel-20261116/README.md`](../scroll-wheel-20261116/README.md)。各版功能不同，不能将差额全部视为退化。

## 复测

先独立构建待测版本，关闭其他构建与桌面测试。下面使用同机保留的历史 120 轮宿主和专项宿主；若宿主丢失，可用 `../scroll-wheel-20261116/Harness.csproj` 分别编译两个夹具，将 `ProductDir` 设为待测依赖目录、`Fixture` 设为相应源码，记录新宿主指纹。重新编译的宿主不应冒称历史同一个二进制。

```powershell
./tests/performance-baselines/quote-scroll-20261116/run-native.ps1 `
  -ProductDir ./src/TeXeed.App/bin/Release/net10.0-windows/win-x64 `
  -OutputDir ./artifacts/perf-quote-scroll-20261116/final-candidate `
  -PanelHostDir ./artifacts/scroll-perf-20261116/after-half `
  -InteractionHostDir ./artifacts/perf-quote-scroll-20261116/harness-deep-v2
```

输出目录必须不存在。脚本复制完整应用依赖并记录程序集、夹具和缓存哈希，再串行运行 120 轮及专项；每个输出目录拥有各自的隔离 profile。结果分别为 `panel/results.json`、`interaction/results.json` 和 `fingerprints.json`。运行完成后将需要保存的原始结果复制进本目录，文档比较同场景历史最佳、改前和最终版的各项中位数，并说明功能差异与尚未归因的开销。若窗口失去可见性或样本不完整，丢弃该批并在空闲桌面重测。

`resize-fixture.cs.txt` 是只测变宽的深评夹具，不强制更改缓存长度，校验窗口可见性；先后执行 `resize-051-a`、`resize-052-a`、`resize-053-a`、`resize-053-b`、`resize-052-b`、`resize-051-b` 六批，分别使用冻结的 0.5.1、0.5.2 和候选程序集。它未执行完整夹具中的输入操作，不能将该专项数据与完整夹具直接比较。用上面的 `Harness.csproj` 和此源码构建宿主，复制宿主 `Harness*` 文件至每份冻结的完整应用目录，逐批运行 `dotnet <应用目录>/Harness.dll <不存在的独立输出目录>`。每批结果及全部单次数据以同名 JSON 保存。夹具 SHA-256 `8F49BB5C917034EDE44AAD3A8C181751541FE82EE67401B10B6F5E5D42AAA273`，本次宿主 `74B8CFB77C6FFB31CE160476291472CCCB3AC2A575F1D1426534CE3CC5C92909`；0.5.1、0.5.2、候选应用 DLL 分别为 `CC16A59CD6998A9DBA1BDB09E1BE593F9251C394ADB9B19451F3AFF7BE1F2654`、`A19D86BAF3FFB8E878D9650AC621501BD0818861A4CD3D9B8685DF1061B1F12D`、`302E767F3DF24543A28F3AFE440D921729E8A87B0E7635FDCC1551B12089ADB6`。
