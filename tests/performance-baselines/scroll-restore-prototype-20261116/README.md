# 原型延迟恢复与滚轮（2026-11-16）

同机 Windows、Chrome 157，1000×820 视口；历史 `7af94c2`、改前提交 `2011360`、本次工作树各运行一批，固定 120 轮短回答。沿用 `branching-reading-20261110/measure-sampled.cjs` 的输入与滚动夹具，但三版均使用原型 `index.html`，并在滚动前派发相同滚轮事件，使待取消的处理参与测量；因此历史版为本次同方法的可复核参照，不直接沿用旧采样值。每项热身两次、测七对，等待两帧及 220 ms，并以空操作配对校正；V8 HeapProfiler 以 1,024 B 间隔分别采集七次操作及空操作，相减后取均值，仅是估计。样本、页面与脚本 SHA-256、环境信息见 `samples.json`。

| 交互 | 历史／改前／本次同步中位 | 历史／改前／本次校正耗时中位 | 历史／改前／本次净采样分配 | 历史／改前／本次可见控件 |
| --- | --- | --- | --- | --- |
| 编辑框输入 | 1.2／1.3／1.2 ms | −8.6／1.0／4.5 ms | 135,724／143,943／141,399 B | 11／18／18 |
| 正文滚动 | 0.1／0／0.1 ms | 1.4／0.7／1.1 ms | 139,058／155,874／149,243 B | 11／18／18 |

单批采样与负校正耗时反映排队、采样及调度波动，不能由此断言小幅速度或分配差异；历史页面缺少后续功能，差额包含功能成本及未拆分实现开销。该合成滚动事件用于性能对照；真实浏览器滚轮、延迟恢复和生成结束行为另由 `prototypes/ai-chat-v02/scroll-restore-browser.test.cjs` 验证，不能由此推论原生 WPF 或真实联网性能。

运行时指定 `TEXEED_PLAYWRIGHT_PATH` 和 `TEXEED_BROWSER_PATH`；例如 `node tests/performance-baselines/scroll-restore-prototype-20261116/measure.cjs artifacts/scroll-restore-prototype-samples.json`。旧版本以 Git archive 提取到忽略的 `artifacts/`，测试无需修改用户会话。
