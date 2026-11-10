# 自主书签原型性能记录

同机 Windows、Chrome 157、1000×820，使用 120 轮相同的合成问答；历史 `7af94c2`、变更前 `cf7cb23` 与新版各交错两批，热身两次、记录七次。输入及滚动计同步耗时，并等待两帧和 220 ms，以配对空操作校正排队；V8 HeapProfiler 以 1024 B 采样，分别累积七次实际操作与空操作，差值除以七。采样值是估计且受调度影响，负值是噪声，不代表负分配。滚动时检查各自位置存储，改前的阅读位置及新版独立视口记录均已落盘；新版没有因此取消普通视口恢复。

| 指标 | 历史 7af | 变更前 cf7 | 新版 |
| --- | --- | --- | --- |
| 输入同步中位 | 1.0–1.2 ms | 0.1 ms | 0.1 ms |
| 输入配对净分配采样／次 | 135–137 KB | −0.81–0.50 KB | −1.54–3.27 KB |
| 滚动同步中位 | 0–0.1 ms | 0 ms | 0–0.1 ms |
| 滚动配对净分配采样／次 | 140–142 KB | −1.77–4.79 KB | −0.20–−0.02 KB |
| DOM 元素／可见控件 | 2,283／11 | 1,132／4 | 1,148／5 |

可见控件计数检查视口和祖先裁剪。历史版功能、页面结构与现行原型不同，因此分配差不能完全归为实现退化或改善；采样噪声淹没新旧相近的分配值，不能断言稳定改善。等待校正值包含帧与定时器波动，不用于判定真实负耗时。

使用 120 轮、60 追问会话的夹具另测操作，每项两批、各热身两次并测七对，同样等待两帧、220 ms 后逐对扣除空操作采样：

| 操作 | 变更前 cf7 | 新版 | 可见控件 |
| --- | --- | --- | --- |
| 查看提问出处，新版 60 个书签 | 同步 2.0–2.4 ms；约 315–320 KB | 2.1 ms；约 343–346 KB | 7／7 |
| 打开 60 个书签 | 无同功能对照 | 0.3–0.4 ms；约 12.6–14.7 KB | 22 |
| 跳到书签 | 无同功能对照 | 2.6–2.7 ms；约 332–342 KB | 7 |

同一新版不含书签时查看提问出处为 1.9–2.0 ms、约 307–310 KB，与 60 书签时相差约 33–38 KB。会话切换调用 `save()` 序列化含书签的完整状态，可能是增量的一部分；按需避免对不变数据反复序列化是可改善的候选。采样没有调用栈，无法将差额全部归因为该路径或断言全是不可避免的功能成本。

历史版不提供自主书签及其跳转，无法计算这些新增功能相对于历史最佳的退化；原生高密度夹具的 1.3 倍且增加 8 ms／256 KiB 的报警条件不能被用作新动作自动通过的依据。测试为浏览器、合成短文本和模拟输入，不覆盖真实输入法、长公式、项目文件及原生 WPF；净分配值为统计采样，不是进程全部内存或精确总分配。两批采样不排除后台环境干扰，当前结果不足以宣称完整性能验收通过。

`samples.json`、`operations.json`、`source-zero.json` 保存原始样本、脚本和资源 SHA。`operations-sampled.cjs` 是 60 条书签时的实际采样脚本，`operations.cjs` 增加 `TEXEED_PERF_BOOKMARKS` 以运行零书签反事实。`verify.cjs` 校验历史及变更前 Git 资源、当前原型资源和脚本指纹；请在对应版本代码检出后运行。

```powershell
$env:TEXEED_PLAYWRIGHT_PATH = '<Playwright 包目录>'
$env:TEXEED_BROWSER_PATH = '<Chrome 可执行文件路径>'
$env:TEXEED_PERF_ROUNDS = '2'
node tests/performance-baselines/bookmarks-branching-20261110/measure.cjs artifacts/bookmarks-reading-perf.json
node tests/performance-baselines/bookmarks-branching-20261110/operations-sampled.cjs artifacts/bookmarks-ops-perf.json
$env:TEXEED_PERF_MODES = 'after'
$env:TEXEED_PERF_OPS = 'source'
$env:TEXEED_PERF_BOOKMARKS = '0'
node tests/performance-baselines/bookmarks-branching-20261110/operations.cjs artifacts/bookmarks-zero-perf.json
node tests/performance-baselines/bookmarks-branching-20261110/verify.cjs
```
