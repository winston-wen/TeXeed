# PDF 双窗原型发送框性能

`measure.cjs` 在同一 Edge 无头浏览器、1000×820 视口测量发送框三行内容中程序派发的输入事件。每版交错三批，各预热两次并采七次，记录同步耗时、两帧耗时和 V8 采样分配；PDF 编译以固定错误响应替代。`before/` 为 Git 提交 `cb727ad` 的原双窗原型，`after/` 为本次源码快照。旧版无行号和恢复的底栏，新版增加这些功能，结果不能作为同功能实现快慢的单独结论。资源和测试脚本的 SHA-256 保存在 `results.json`；运行 `node verify.cjs` 验证。

重测须设置 `TEXEED_PLAYWRIGHT_PATH` 和 `TEXEED_BROWSER_PATH`，运行 `node tests/performance-baselines/pdf-composer-20261109/measure.cjs`。此脚本会覆盖 `results.json`，先另存需保留的样本。当前场景没有更早可复核的同夹具历史最佳；测量不覆盖真实输入法、真实模型和原生 WPF 控件。

`reconstructed/` 是依据本次优化前的函数文本重建的反事实：每次输入测量并重建全部行号，其他资源与最终版相同；它不是冻结的历史发布资源。`prepare-reconstructed.cjs` 记录重建方式。`measure-long.cjs` 对比该变体与最终版的 300 行草稿，三批交错、各预热两次并采七次；结果和资源指纹见 `long-results.json`。该脚本会覆盖该结果。
