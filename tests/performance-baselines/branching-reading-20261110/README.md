# 中途追问原型：浏览器性能样本

同一台 Windows 机器、Chrome 157.0.8103.40、1000×820 视口；使用 120 轮、同一份合成提问和短回答，比较历史 `7af94c2`、改前 `a0b50ba9d2944706c6c6a4cd2905d1ef34c7e8b0` 和独立追问原型。`samples-final.json` 保存两批交错数据及全部已加载资源 SHA-256；各批热身两次、记录七次。操作为在主框输入、上下滚动正文；记录同步耗时与等待两帧、220 ms 后的耗时，并以相邻空操作耗时配对校正。CDP HeapProfiler 对空操作与操作各采七次，扣除空操作采样量后除以七，间隔 1024 B，包含调度及夹具成本；是统计估计，不是精确总分配。可见控件以控件矩形与视口相交估计，未检查祖先裁剪。

| 交互 | 历史 7af94c2 | 改前 | 新原型 |
| --- | --- | --- | --- |
| 输入同步中位（两批） | 1.1–1.3 ms | 1.0–1.2 ms | 0.1 ms |
| 输入净采样分配／次 | 130.5–132.2 KB | 138.7–145.4 KB | 0.17–0.60 KB |
| 滚动同步中位 | 0.1 ms | 0–0.1 ms | 0 ms |
| 滚动净采样分配／次 | 140.8–143.1 KB | 145.5–149.3 KB | 0.90–1.70 KB |
| DOM 节点／估计可见控件 | 2,283／11 | 2,331／19 | 1,132／4 |

新增功能另用 120 轮、60 条分支测量，`branch-operations-final.json` 保留单批七组原始配对。关系图打开同步中位 1.3 ms、净采样约 232,632 B、估计可见控件 13；从图切换至追问 0.9 ms、213,440 B、6；返回父阅读断点 3.0 ms、304,800 B、6；查看提问出处 2.4 ms、315,032 B、7。两帧及 220 ms 等待后的配对耗时分别为 -0.5、0.2、3.1、11.4 ms；负值来自调度波动，不表示真实负耗时。旧版没有同等交互，故无历史最佳，不对这些新增功能比较退化。此四项在 `branching.js` 指纹 `dec6d6565b97495c026a9967a1ff97795eaca8a6844a95a83b56ef36dd0f4293` 的版本测量；之后仅修改了发送时的持久化失败处理。

最终 `58888b9efdfedd59252cc704f687b39eb18b7c0c8515bfe17cd8a20e72b331ab` 版本补测受上述修改影响的正常发送并本地持久化：同步中位 0.6 ms、校正等待 0.9 ms、净采样 330,560 B、估计可见控件 4；仅模拟预设答复与本地存储，不含联网。无相同功能历史对照。该增量包括持久化与新轮渲染，尚未拆分功能成本及可避免开销，详见 `send-final.json`。

历史和新原型功能、UI 结构、排版与节点数量不同；采样只覆盖较短合成内容，不覆盖长公式、鼠标真实选区、输入法、可复现的当前会话用户数据、原生 WPF 或完整项目，也未排除测试期间其他进程活动。因而分配差额包括功能差异，不能断言新原型全面更快。新的三个动作涉及序列化和重绘，所测净分配均为可见增量；测试尚未分离不可避免的功能成本与可避免开销。新增操作和本次排队校正方法没有同功能历史最佳；现存原生高密度夹具的 1.3 倍加 8 ms／256 KiB 等报警条件不能直接判作这些无对照的新操作已通过。此测试不构成完整性能验收。

`samples.json` 为字符级断点改进前的早期单批，SHA-256 与最终页面不同，未冻结当时的新原型文件，仅作开发观察；以上表格使用 `samples-final.json`，其中新版 `branching.js` 指纹为 `2539ce0c7f4ab3bc5522875602d976f6adf448dd1dcefda7196e12e0363b3c3c`。实际采样脚本原样保存在 `measure-sampled.cjs`，指纹与 JSON 的 `fixtureHash` 相同。`samples-last.json` 是后续 `dec6d656…` 版本的单批三版复测。最终 `58888b9e…` 版本的输入、滚动单批分别为 0.1、0 ms，净分配约 2,062、−291 B；负分配只是两次独立采样差的波动。原始样本为 `samples-ultimate.json`。`measure.cjs` 固定改前提交并可通过环境变量选择模式。新增操作的采样脚本保存为 `branch-operations-sampled.cjs` 和 `branch-operations-recheck-sampled.cjs`，当前 `branch-operations.cjs` 另支持模拟发送测试。

中间版 `2539ce0…` 和 `dec6d656…` 的新版资源当时没有冻结，故这些中间版样本无法完整复现；校验脚本只核对其测量脚本以及历史和改前 Git 资源，不声称已核对中间版源码。最终版的 `samples-ultimate.json` 和 `send-final.json` 会核对当前工作树文件。

复测在 PowerShell 中指定 Playwright 包和 Chromium 路径后运行。两版旧页面由 `git archive` 解压到忽略的 `artifacts/`，新版使用工作树文件；应先核对样本中各资源 SHA。输出路径写到 `artifacts/` 避免覆盖已保存样本：

```powershell
$env:TEXEED_PLAYWRIGHT_PATH = '<Playwright 包目录>'
$env:TEXEED_BROWSER_PATH = '<Chrome 或 Edge 可执行文件路径>'
$env:TEXEED_PERF_ROUNDS = '2'
node tests/performance-baselines/branching-reading-20261110/measure.cjs artifacts/branching-reading-perf.json
$env:TEXEED_PERF_ROUNDS = '1'
node tests/performance-baselines/branching-reading-20261110/branch-operations.cjs artifacts/branching-operations-perf.json
node tests/performance-baselines/branching-reading-20261110/verify.cjs
```
