# 首轮来源待发显示性能

Chrome 157，同机 1000×820 视口，固定已发送分支、60 条普通引用及 1 条专用来源，交错运行改前／本次／本次／改前，每批预热两次、记录七对样本。脚本仅切换 `pendingReferences` 的来源过滤，其他原型资源相同；`samples.json` 保存脚本及资源 SHA-256、各次样本和环境。

打开引用列表的同步中位数改前 4.0–4.6 ms、本次 3.2–3.9 ms；配对 V8 净采样分配分别为 28.1–30.2 KB、27.0–30.7 KB，可见控件均 37。隐藏来源后列表行数从 61 降至 60，DOM 节点从 689 降至 681。每次测量等待两帧与 700 ms 并减去配对空操作，等待包含滚动活跃状态的 550 ms 计时器；负校正耗时来自调度波动。分配是采样估计，不能根据本次小额差异判断稳定变化。

这是来源列表场景的同版前后对照；此前无可复核的同夹具历史最佳，不等于全产品历史性能评价。它未覆盖真实 AI 请求、输入法或原生程序。

设置 `TEXEED_PLAYWRIGHT_PATH` 和 `TEXEED_BROWSER_PATH` 后可复测：

```powershell
node tests/performance-baselines/branch-origin-pending-20261116/measure.cjs artifacts/branch-origin-pending-retest.json
```
