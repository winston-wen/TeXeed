# 性能历史基准

性能比较以相同场景和测量条件下可追溯的历史最佳为主要基准，同时展示修改前的结果。每个指标单独寻找最低的可靠中位数；不同指标的最佳值可以来自不同版本，不能将它们描述为某个版本同时达到的成绩。历史最佳并非理论下限，记录应保留来源和不确定性。

比较必须匹配夹具、测量方式、软硬件、DPI、字体、主题和计时边界。采集 Release 版本、隔离测试数据、至少一次热身及七个样本；等待 ApplicationIdle 等所有计划内任务完成。滚动方向、窗口尺寸方向或不同内容密度应单独记录，避免样本分布改变导致中位数不可比。新方法另建场景，不丢弃旧记录。旧版本缺少新功能时，同场景结果仍应保留，并解释功能成本，不把它当作无效数据抹掉。

历史候选归档在 `tests/performance-baselines/history/`，相应源夹具在 `tests/performance-fixtures/`。0.3.47、0.3.48 和 0.3.49 的普通／高密度数据保留七个原始样本与摘要。它们缺少完整运行环境和程序集证据，且部分样本混合滚动方向，只能作为【未核验的历史候选】；重测时要在同机交替运行历史与当前版本。查看候选及与本次摘要的差距：

```powershell
node scripts/perf-history.cjs archive-report --archive tests/performance-baselines/history/history.json --scenario ordinary
node scripts/perf-history.cjs archive-report --archive tests/performance-baselines/history/history.json --scenario dense --current-summary artifacts/当前测试/ui-performance.json
```

候选报告不会自动把无法核实的环境和程序集身份当成已验证事实。比较报告对各指标使用对应的最低候选，注明版本和样本；测试方法与环境尚未核实时，百分比仅供定位后续复测。旧基线工具的 `--baseline` 仍可用于修改前对照，它不会自动读取历史数据库，不能单独作为历史最佳比较。

2026-11-04 已在同一环境下对 0.3.47 与 0.3.49 的高密度场景交错运行各三次，六组原始数据与清单保存在 `tests/performance-baselines/verified-history.json`，测试宿主、方法、依赖和环境指纹也已保存。该场景调整窗口宽度的 UI 线程分配，稳定的历史基准为 1,854,424 B，0.3.49 为 3,615,632 B，增加约 95%，触发报警；滚动分配由 4,701,976 B 增至 5,840,544 B，增加约 24%。旧版没有波浪强调，先前的分项测试显示主要增量来自 WPF 查询文字位置，仍须区分新增功能成本和可避免的开销。0.3.49 的滚动及缩放耗时在三批之间波动较大，不将它们标为稳定成绩。

复查这次已保存的结果：

```powershell
$history = 'tests/performance-baselines/verified-history.json'
$runs = (Get-Content -Raw $history | ConvertFrom-Json).runs
$old3 = ($runs | Where-Object { $_.manifest.execution_id -eq '20261104-dense-old-3' }).id
$new3 = ($runs | Where-Object { $_.manifest.execution_id -eq '20261104-dense-new-3' }).id
$old2 = ($runs | Where-Object { $_.manifest.execution_id -eq '20261104-dense-old-2' }).id
$new2 = ($runs | Where-Object { $_.manifest.execution_id -eq '20261104-dense-new-2' }).id
node scripts/perf-history.cjs compare --history $history --current $new3 --before $old3 --current-confirm $new2 --before-confirm $old2
```

本次比较返回码 2，表示历史基准报警。重跑时分别从提交 `31a6656` 和 `8250cca` 导出独立源码副本，将 `tests/performance-fixtures/high-density-unique-width-fixture.cs` 与 `wpf-history-host.cs` 分别用于副本中的 `AiUiPerformanceTests.cs` 和 `Program.cs`；两个副本以 `dotnet restore tests/TeXeed.AppTests/TeXeed.AppTests.csproj --locked-mode -r win-x64 -p:NuGetAudit=false --ignore-failed-sources` 还原，再以 `dotnet build tests/TeXeed.AppTests/TeXeed.AppTests.csproj -c Release -r win-x64 --no-restore` 构建。交错执行各自的 `TeXeed.AppTests.dll --ai-ui-perf-only --with-headings`，每次使用独立输出目录。完整夹具、方法哈希与运行条件见 `tests/performance-baselines/methods/` 和 `environments/`。

完整的新测量使用 `scripts/perf-history.cjs`：`import` 将清单、原始样本和摘要一并写入受 Git 版本管理的历史 JSON；`compare` 同时给出历史最佳和修改前的差额，任一历史最佳报警时返回非零退出码；`confirm` 以同版本两次独立执行检查单批异常。`historical_best` 排除本次测量、其确认批次及晚于本次的记录；`candidate_historical_best` 另列包含本次的潜在新纪录，避免把本次结果与自身比较。命令参数及清单字段可运行 `node scripts/perf-history.cjs --help` 查看。按实填写提交、各程序集来源、执行时间、软硬件、字体、主题、DPI、夹具与方法哈希；切勿用当前环境推定旧环境，也不要把诊断版或人为关闭功能的结果作为正式版本。样本、清单与历史记录均提交 Git；临时构建和截图放在 `artifacts/`。

未复测的单批最低结果标为暂定并参与比较，不冒称已确认；确认时检查同场景、同环境、同版本的全部已导入批次，不能只挑选相近的两次。若后续批次显示波动过大，原确认结果降为暂定。若环境、源码身份或测量方法证据不足，应保留候选并注明局限，在相同条件下复测历史版本；不能静默退回以上一版作为长期基准。稳定增加即使未达报警线，也须分析功能成本与可避免开销。现有报警仍要求耗时同时超过基准 30% 和 8 ms，或分配同时超过基准 30% 和 256 KiB。
