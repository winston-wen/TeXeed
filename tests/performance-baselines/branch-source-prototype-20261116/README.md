# 新追问来源展示原型性能（2026-11-16）

同机 Windows、Chrome 157、1000×820；以 120 轮、60 条未发送的追问为夹具，先打开关系图并选中同一分支，再测打开该分支。历史 `cf7cb23`、改前 `2011360` 和本次工作树各采一批，热身两次、七对样本；每对等待两帧与 220 ms，用相邻空操作校正。V8 HeapProfiler 按 1,024 B 采样，分配为操作与空操作样本之差，仅为估算。历史版不显示新追问来源，本次新增了来源文字与可返回出处的链接；功能不同，不能把差额直接解释为同功能提速或退化。

| 指标 | 历史 | 改前 | 本次 |
| --- | ---: | ---: | ---: |
| 同步耗时中位 | 0.6 ms | 0.6 ms | 0.7 ms |
| 配对等待校正中位 | −1.1 ms | 2.5 ms | −1.4 ms |
| 净采样分配中位 | 174,628 B | 164,756 B | 162,940 B |
| 可见控件／DOM 节点 | 6／360 | 19／437 | 20／441 |

一次采样不足以证明小差异，负值反映调度与采样波动；历史版与当前的功能及节点不同，包含功能成本和未拆分的实现开销。此夹具只测短文本来源，不含公式或图片渲染、联网和原生程序；原始配对、环境及资源和脚本指纹见 `samples.json`。真实浏览器分别验证了文字、公式、长公式、图片、表格、重开、引用及草稿，见 `prototypes/ai-chat-v02/branching-origin-display-browser.test.cjs`。

指定 `TEXEED_PLAYWRIGHT_PATH` 和 `TEXEED_BROWSER_PATH` 后运行 `node tests/performance-baselines/branch-source-prototype-20261116/measure.cjs artifacts/branch-source-prototype-samples.json`；旧版由 Git archive 加载到忽略的 `artifacts/`。
