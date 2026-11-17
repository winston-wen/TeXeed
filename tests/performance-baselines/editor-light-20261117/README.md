# 亮色源码编辑器性能（2026-11-17）

`fixture.cs.txt` 基于 `../forms-native-20261112/editor-emoji-fixture.cs.txt`，明确启用亮色主题并记录字体族和字重。1000 行中文与 TeX，分别测普通文本和每行 4 个 emoji 的绘制、单字符修改和滚动；窗口为 900×760 DIP，各操作预热两次、采七对并配对 80 ms 空等待。`offscreen-fixture.cs.txt` 仅将窗口移到屏幕外并禁止抢焦点。

因桌面持续有人使用，本次只取得屏幕外的正式 0.5.5 和 0.5.6 功能构建各一批：`offscreen-055.json`、`offscreen-056.json`，程序集与宿主哈希在对应 `offscreen-*-fingerprints.json`。两版字重不同，差异包含视觉功能成本；此前深色主题数据不是相同主题的历史最佳。屏幕外测量不能替代可见窗口及真实键盘测试。

复测时以 `Harness.csproj` 的 `ProductDir` 指向对应版本的 Release 应用目录，以 `Fixture` 指定相应源码，用同一个 `TeXeed.AppTests` 宿主分别运行两版，参数为各自全新的输出目录。测量仅统计 UI 线程分配和窗口内可见控件，不包含 GPU 与后台线程；配对耗时可能出现负值。
