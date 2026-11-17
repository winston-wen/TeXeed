# AI 回答批注浮窗性能（2026-11-17）

`fixture.cs.txt` 沿用 `../annotation-native-20261112/fixture.cs.txt` 的打开及完成操作、每项两次预热七次采样、250 ms 空等待和 ApplicationIdle 配对，改用 AI 回答的 MarkdownView 和批注位置。旧夹具使用普通文档宿主，不是同一浮窗行为的历史最佳。

因桌面持续有人使用，本次未运行此可见窗口性能夹具；真实拖动、缩窗及删除交互的功能验证与性能测量应分开报告。复测可用 `../scroll-wheel-20261116/Harness.csproj`，将 `ProductDir` 和 `Fixture` 指向待测应用依赖和本文件，将 `AssemblyName` 设为 `TeXeed.AppTests`，以同一宿主分别运行正式 0.5.5 和待测版本，各自使用新的输出目录并记录哈希。夹具仅测打开与完成，不测真实拖动、图片、后台线程及磁盘保存。
