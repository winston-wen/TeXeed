# 表格视口与普通会话性能（2026-11-17）

`click-fixture.cs.txt` 准备了长回答中第二张表的真实鼠标点击场景：每次将文字定位到视口，配对 800 ms 空等待和保存锁，记录屏幕位置变化、耗时、UI 线程分配和可见控件。`expect-stable` 要求点击后文字仍留在原处。运行需要交互桌面；修改前如果发生跳转，与修复版并非相同的最终视觉状态，不能直接将差值当作同功能快慢。由于桌面持续有人使用，本次未运行该真实鼠标性能夹具；实际点击正确性由独立 UI 回归验证。

`panel-offscreen.cs.txt` 沿用 `../same-conversation-links-20261117/panel-fixture.cs.txt` 的 120 轮、尺寸变化校验与 800 ms 配对等待，仅将窗口设为屏幕外且不抢焦点。`offscreen-panel-055.json`、`offscreen-panel-056.json` 是正式 0.5.5 与 0.5.6 功能构建各一批的原始样本；`offscreen-*-fingerprints.json` 保存程序集、宿主及夹具的哈希。此环境与历史可见窗口不同，不能用作同环境历史最佳或宣称性能验收通过。

复测时以 `../scroll-wheel-20261116/Harness.csproj` 指向源码和对应版本的完整 Release 依赖，普通会话宿主程序集名设为 `Perf`，真实点击宿主设为 `TeXeed.AppTests`。将同一宿主及各自版本依赖放入隔离目录后串行运行 `dotnet Perf.dll <新输出目录>` 或 `dotnet TeXeed.AppTests.dll <新输出目录> [expect-stable]`。真实点击应在用户不使用桌面时进行，保留指针并检查输入是否被外部移动；测试不含 GPU、后台线程及真实模型调用。
