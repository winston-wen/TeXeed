# 批注浮窗浏览器性能简评（2026-11-12）

同一台 Windows 主机、Chrome 157、1000×820，固定使用一份内置示例会话和一条批注，测量打开浮窗、输入一个字及完成批注。比较历史提交 `8990572`、修改前 `dddee8f` 和当前原型；每版每项预热两次，采集七对空操作和实际操作，等待两帧及 220 ms，CDP 以 1024 B 间隔采样分配。`results.json` 保存环境、页面资源 SHA-256、逐对结果及可见控件数量。

运行：设置 `TEXEED_PLAYWRIGHT_PATH` 为可用的 Playwright 模块目录、`TEXEED_BROWSER_PATH` 为 Chrome 可执行文件，然后在仓库根目录运行 `node tests/performance-baselines/annotation-popup-20261112/bench.cjs`。脚本从对应 Git 提交导出旧版页面；新版采用当前工作区页面，应先核对结果中的资源哈希。

样本仅每版一批，采样分配包含调度及 JavaScript 运行时开销，不是完整内存分配；等待时间受帧相位影响。此浮窗夹具此前没有已保存的同方法历史样本，本次两份可复现旧提交作为暂定历史候选；不覆盖真实 WPF、输入法或 AI 调用。
