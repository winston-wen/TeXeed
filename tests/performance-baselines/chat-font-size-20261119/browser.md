# 阅读字号与字重的原型性能（2026-11-19）

在同一 Windows 主机、Chrome 157.0.8103.79、1000×820 视口下复用 50 轮、每轮 6 个标题的 [合成夹具](../../performance-fixtures/browser-heading-axis-v1.cjs)。历史版为 [上一份标题中轴记录](../title-axis/browser-performance.json) 中的 `8f3e434`，改前为 0.5.15 的 `c1cf4ee`。按「历史、改前、改后」与反序交替执行三批，各次预热两次，记录七次。现版本使用同一份本机冬青字体文件，历史页面没有引入该字体。样本记录文件指纹。

| 指标 | 历史 | 改前 | 改后 |
| --- | ---: | ---: | ---: |
| 首次重建同步中位数 | 40.0 ms | 67.0 ms | 64.9 ms |
| 热重建同步中位数 | 32.5 ms | 56.7 ms | 54.9 ms |
| 热重建后两帧中位数 | 36.4 ms | 61.4 ms | 59.9 ms |
| 等待两帧及 200 ms 队列，扣除配对空等待的中位数 | 16.5 ms | 60.8 ms | 59.5 ms |
| V8 采样保留堆中位数 | 105,356 B | 20,452 B | 19,864 B |
| 可见控件估计 | 21 | 28 | 27 |

短测在改前与改后之间未显示稳定劣化。历史版使用不同字体、样式和控件。浏览器帧、定时器及 V8 采样受调度与回收影响，保留堆样本不能代表总分配量，两版的区间相交。页面有 300 个标题，普通会话的滚动和文字输入另由原生夹具覆盖。

在仓库根目录执行，浏览器与 Playwright 位置按需用 `TEXEED_BROWSER_PATH` 与 `TEXEED_PLAYWRIGHT_PATH` 指定。

```powershell
node tests/performance-baselines/chat-font-size-20261119/browser-measure.cjs --history 8f3e434 --baseline c1cf4ee --output artifacts/chat-font-size-browser.json
```

[逐次样本和资源指纹](browser-heading.json)
