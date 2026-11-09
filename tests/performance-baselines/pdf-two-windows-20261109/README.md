# PDF 双窗口原型编辑性能

同一 Windows、Edge 157、1400×900 视口，沿用 `pdf-comment-prototype-20261109` 的 120 行源码与 0／60 评论输入夹具，三批各预热两次、采七次，记录同步耗时、等待两帧、V8 采样和视口相交控件。新原型改为在独立编辑窗口测量，旧版是在单窗口测量；当前计时不包括另一个会话窗口接收事件的耗时，也不包含 PDF 编译或真实输入法。控件因窗口拆分不能简单作总量比较。

`results.json` 为样本，`resources/` 保存实际测试资源，`measure.cjs` 为脚本；旧版样本和资源见 `../pdf-comment-prototype-20261109/`。优化前双窗样本和资源为 `before-results.json`、`before/`；跨窗同步脚本和前后样本为 `sync.cjs`、`sync-before-results.json`、`sync-results.json`，包含浏览器驱动和两帧调度，只采样会话窗口的 V8 分配。通过 `node tests/performance-baselines/pdf-two-windows-20261109/verify.cjs` 校验指纹。重测需要启动原型本地服务、设置 `TEXEED_PLAYWRIGHT_PATH`，运行 `measure.cjs` 和 `sync.cjs`；脚本覆盖结果，先保存历史样本。
