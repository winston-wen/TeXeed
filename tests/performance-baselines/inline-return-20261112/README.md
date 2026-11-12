# 行内追问与上游返回的原型性能（2026-11-12）

同一台 Windows 主机、Chrome 157、1000×820，固定 120 轮、60 条分支、一处折叠章节，比较历史提交 `cf7cb23`、修改前 `dddee8f` 和当前原型。分别测试含末尾偏移的新分支返回、仅有 quote 的旧分支返回，以及展开章节。每项预热两次，采集七对实际操作／空操作，等待两帧及 220 ms，并使用 CDP 以 1024 B 间隔采样分配。`results.json` 保存环境、源码指纹、全部原始样本及可见控件数。

初次样本的原始脚本为 `measure-initial.cjs`，其 SHA-256 与 `results.json` 内的夹具指纹一致。最终版单独复测脚本为 `measure.cjs`：在仓库根目录设置 `TEXEED_PLAYWRIGHT_PATH` 与 `TEXEED_BROWSER_PATH`，设置 `TEXEED_PERF_MODES=after`，再运行 `node tests/performance-baselines/inline-return-20261112/measure.cjs artifacts/inline-return-perf/final.json`。脚本从 Git 导出两版历史资源，最新版采用当前文件，需核对结果中的 SHA-256；所有临时文件留在 `artifacts/`。

每版仅一批，配对等待受帧和调度波动影响，V8 采样值是估计而非完整内存分配。旧版没有行内链接、新来源偏移和居中行为，差额既有功能变化也可能有实现成本；当前夹具没有复杂公式、实际编辑或真实模型调用，也不能代表原生 WPF。

初次样本冻结后，原型又加入定位期间的一次性滚轮、指针、触摸和导航键取消保护，并将成功提示提前到定位帧内计算阅读区中心。初次 JS 指纹为 `216be6e1813be8866573f3de958c17e56069362555e55b351046fa9d6e8492fc`，含保护的最终 JS 指纹为 `91609febb34cf16f73f7c86b0866281ab2b22dd8f16dc806d3e46fdf2047e668`。最终版三项重新测量结果在 `final.json`，沿用初次 `results.json` 的历史和改前样本；最终版未与历史、改前交错运行，不能依据单批小差断定稳定变化。
