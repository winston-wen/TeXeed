# 阅读中轴的浏览器性能（2026-11-19）

同一主机使用 Windows 10.0.26300、Chrome 157.0.8103.79、1000×820 视口和本机冬青字体。历史为此前同夹具记录里较快的 `8f3e434`，改前为正式 0.5.17 的 `fb3cdee`，改后为最终候选。旧版未使用现有正文全套字体、字号和控件。每项三批交错运行，每批预热两次后保留七个样本。队列校正从两个动画帧及 200 ms 后的测量里扣除同批空等待。可见控件按与浏览器窗口相交估计。

| 50 轮、300 标题 | 历史 | 0.5.17 | 改后 |
| --- | ---: | ---: | ---: |
| 首次重建同步耗时 | 42.9 ms | 64.5 ms | 60.4 ms |
| 热重建同步耗时 | 30.7 ms | 53.7 ms | 59.2 ms |
| 热重建与队列校正 | 30.0 ms | 58.7 ms | 60.1 ms |
| V8 采样保留堆 | 102,228 B | 119,020 B | 16,608 B |
| 可见控件 | 21 | 27 | 27 |

| 50 轮、300 标题、600 个行内公式 | 历史 | 0.5.17 | 改后 |
| --- | ---: | ---: | ---: |
| 首次重建同步耗时 | 190.2 ms | 215.1 ms | 201.7 ms |
| 热重建同步耗时 | 132.0 ms | 162.9 ms | 154.7 ms |
| 热重建与队列校正 | 137.0 ms | 182.7 ms | 169.8 ms |
| V8 采样保留堆 | 157,888 B | 217,276 B | 223,996 B |
| 可见控件 | 20 | 28 | 27 |
| 实际渲染的行内追问 | 0 | 49 | 49 |

纯标题热重建的三批改前后分别快慢不一，配对队列耗时的差值也跨零。含公式夹具的初版在读取阅读视口几何后修改公式及气泡样式，热重建同步耗时从 205.4 ms 增至 246.7 ms。最终候选把这些样式写入放到几何读取之前，并省掉无行间公式回答的字号查询。最终三批热重建均比同批 0.5.17 快。V8 数字是保留堆抽样，无法代表总分配。公式夹具改前和改后的取值区间重叠，另有 49 个气泡图像承载节点，现有样本不能把约 6.7 KB 的中位数差额完全归给该节点或其他工作。

## 复测

在仓库根目录执行。可用 `TEXEED_PLAYWRIGHT_PATH` 和 `TEXEED_BROWSER_PATH` 指定 Playwright 及 Chrome。

```powershell
node tests/performance-baselines/chat-font-size-20261119/browser-measure.cjs --history 8f3e434 --baseline fb3cdee --output artifacts/browser-headings.json
node tests/performance-baselines/chat-font-size-20261119/browser-measure.cjs --history 8f3e434 --baseline fb3cdee --fixture tests/performance-fixtures/browser-formula-axis-v1.cjs --output artifacts/browser-formulas.json
```

逐次采样、夹具和资源指纹保存在 [标题](browser-headings.json)、[公式和追问](browser-formulas.json) 与 [含公式的初版](browser-formulas-initial.json)。
