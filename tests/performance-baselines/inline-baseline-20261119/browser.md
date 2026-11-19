# 行内公式与正文数字的浏览器性能

Windows 10.0.26300，Chrome 157.0.8103.79，Node 24.22.3，窗口 1000 × 820 CSS px。每列为三批交错测量的中位数，历史／改前／改后分别使用 8f3e434、0.5.19（12b1077）与当前原型。每批预热两次、七次计时，完整反馈覆盖两个动画帧和后续 200 ms，再减去同批空等待。V8 数据是按 1024 B 间隔采样的保留堆估计，不能据此推算总分配。各版功能及视口内容有差异。逐批计时、环境、夹具和加载资源指纹都在原始记录中。

| 操作 | 首次重建 ms | 热重建 ms | 计入队列并校正 ms | 采样保留堆 B | 视口内控件 |
| --- | --- | --- | --- | --- | --- |
| [50 轮、300 个标题](browser-headings.json) | 45.1／66.6／60.1 | 33.4／53.8／51.2 | 31.0／46.3／58.0 | 98,768／23,088／27,332 | 21／27／27 |
| [另含 600 个混排公式](browser-formulas.json) | 189.5／219.7／204.9 | 143.7／169.8／147.7 | 155.9／186.9／170.3 | 151,792／230,556／213,316 | 20／27／28 |
| [900 个纯数字与 300 个表达式](browser-numbers.json) | 181.1／199.4／125.7 | 156.7／166.1／83.6 | 184.5／167.2／91.1 | 197,996／281,680／218,996 | 20／27／28 |

三批数字样本都从 1200 棵公式树降为 300 棵，页面元素由 19,443 减至 9,544。混排公式的同步和队列耗时也在三组配对测量中分别下降。两种公式夹具均为 441 个控件，计入视口的控件在校正排版后多一个；历史版本没有当前的 49 条追问链接。

纯标题样本的队列校正耗时比 0.5.19 高 11.7 ms。配对单批差值为 +0.7、−1.7、+14.8 ms。V8 采样保留堆差值在三批中也有正有负。纯标题没有数学节点或引用裁剪框，界面控件数相同；现有样本未能证明这两项变化是新代码的稳定开销。此前相同夹具的历史较快热重建为 30.7／132.0 ms（纯标题／混排公式），逐次成绩见[上次基准](../ai-reading-axis-20261119/browser.md)。

在仓库根目录执行以下命令即可复测。其中第一条使用默认的纯标题夹具。可用环境变量 TEXEED_PLAYWRIGHT_PATH 和 TEXEED_BROWSER_PATH 指定 Playwright 与 Chrome。

```powershell
node tests/performance-baselines/chat-font-size-20261119/browser-measure.cjs --history 8f3e434 --baseline 12b1077 --output artifacts/browser-headings.json
node tests/performance-baselines/chat-font-size-20261119/browser-measure.cjs --history 8f3e434 --baseline 12b1077 --fixture tests/performance-fixtures/browser-formula-axis-v1.cjs --output artifacts/browser-formulas.json
node tests/performance-baselines/chat-font-size-20261119/browser-measure.cjs --history 8f3e434 --baseline 12b1077 --fixture tests/performance-baselines/inline-baseline-20261119/browser-numbers-fixture.cjs --output artifacts/browser-numbers.json
```
