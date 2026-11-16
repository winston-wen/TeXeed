# 引文公式与滚动条原型性能

同机 Chrome 157、1000×820、120 轮合成对话，末尾 10 轮各含一条相同的四公式引文。`samples.json` 保存历史 `7af94c2`、改前 `b7ca515` 与本次原型的资源指纹和各次样本。每版预热两次后测七对，采集同步耗时、两帧和 700 ms 队列等待、配对空操作的差值及 V8 堆采样分配。700 ms 包括滚动活跃状态的 550 ms 计时器。等待受帧调度影响，负校正值是噪声；分配是采样估计，不是总分配。历史版及改前不具备正确的引文公式显示，功能覆盖不同；本夹具此前没有已验证的同场景历史最佳，历史版本仅供参考。三版只有一批，不能据此判断微小差异是否稳定。

| 操作 | 历史 | 改前 | 本次 |
| --- | --- | --- | --- |
| 输入，同步中位数 | 1.1 ms | 1.1 ms | 1.1 ms |
| 滚动，同步中位数 | 0.1 ms | 0 ms | 0.1 ms |
| 重绘，同步中位数 | 21.4 ms | 22.5 ms | 26.6 ms |
| 输入，净采样分配 | 142,518 B | 150,179 B | 149,198 B |
| 滚动，净采样分配 | 148,524 B | 156,767 B | 153,409 B |
| 重绘，净采样分配 | 1,181,190 B | 1,216,950 B | 1,059,385 B |
| 可见控件／DOM 节点 | 11／2,733 | 18／2,775 | 18／3,927 |

公式现在产生实际 KaTeX 节点，故 DOM 数及重绘时间不能直接判为退化。`cache-samples.json` 在当前同版同一重复引文夹具下按关闭、开启、开启、关闭交替采样：重绘同步中位数关闭 26.9–29.1 ms、开启 25.9–28.2 ms；净采样分配关闭 2.50–2.52 MB、开启 1.04–1.06 MB，均为 19 个可见控件、3,928 个 DOM 节点。缓存限制条目及大小；此对照仅说明重复引文避免重复解析的成本，不能推断全部不同引文的收益。

`samples-precache.json` 为开发中加缓存前的额外样本，含资源指纹；不要将其误作最终版。`measure-sampled.cjs` 是产生三版样本时的原脚本；`measure.cjs` 固定了改前提交以便日后复测。设置 `TEXEED_PLAYWRIGHT_PATH` 与 `TEXEED_BROWSER_PATH` 后，分别运行：

```powershell
node tests/performance-baselines/quote-scrollbar-prototype-20261116/measure.cjs artifacts/quote-scrollbar-retest.json
node tests/performance-baselines/quote-scrollbar-prototype-20261116/cache-measure.cjs artifacts/quote-cache-retest.json
```

测量不覆盖真实输入法、海量不同公式、原生 WPF、GPU 及实际 AI 请求；DOM 节点计数也不等于原生控件数。
