# 同会话来源链接原生性能（2026-11-17）

同一 Windows 10.0.26300、WindowsDesktop 10.0.7 主机，900×760 DIP WPF；每项预热两次、采七对，配对 800 ms 空等待。结果记录首次 ApplicationIdle 反馈、校正耗时、UI 线程分配和窗口内可见控件数，不包含后台线程、GPU、物理鼠标或真实模型请求。负的校正时间表示调度波动。

`fixture.cs.txt` 构造 120 轮与 8 条指向第一轮的旧引用。默认模式不要求链接；`links` 要求 8 条可见链接并验证点击后目标轮次进入视口；`invalid` 将引用 revision 设置为无效，索引仍有这些引用但不插入链接，用于反事实。第三参数 `startup-only` 只运行新建面板、装载会话与链接的操作；不传则另测热滚回和点击。无效锚点没有所需功能，不是可交付替代。`fixture-before-optimization.cs.txt` 是首次测量的源码，哈希与对应 `initial-*-fingerprints.json` 一致。

`deep-*.json` 按文件名顺序 `054-a`、`before-a`、`invalid-a`、`after-a`、`after-b`、`invalid-b`、`before-b`、`054-b` 串行采集。`054` 是正式发布 0.5.4，`before` 为 0.5.5 长度计算优化前，`after` 为优化后；`invalid` 使用优化后程序集但不产生链接。所有批次使用相同宿主；逐批程序集、宿主和源码哈希见 `deep-*-fingerprints.json`。`initial-054/055.json` 是优化前完整操作，`final.json` 是优化后完整操作。输出记录链接数和目标的位置，以便发现无效场景。

`panel-fixture.cs.txt` 沿用 `../steering-20261112/panel-fixture.cs.txt` 的普通会话场景，并校验变宽时实际窗口和面板宽度；完整采样见 `panel-054.json` 和 `panel-055.json`。可复核历史最佳和其功能差异见 `../graph-canvas-20261111/README.md`、`../quote-scroll-20261116/README.md`。普通会话每版仅一批，不能判定小幅分配变化稳定。

`standalone-stages-fixture.cs.txt` 在独立 MarkdownView 中比较冷缓存解析 8 个短文字锚点和完整插入 8 个链接，结果和指纹为 `standalone-stages.json`、`standalone-stages-fingerprints.json`。每次新建视图，各预热两次采七对，配对 800 ms 空等待；两项不是同一个操作，插入还包含末点查找、校验、控件与布局，差额仅作为分项线索，不覆盖公式、表格或长回答。

复测时可使用 `../scroll-wheel-20261116/Harness.csproj`，令 `ProductDir` 指向待测应用依赖目录，`Fixture` 指向相应源码，以 `AssemblyName=TeXeed.AppTests` 构建同会话宿主或 `AssemblyName=Perf` 构建普通会话宿主。将宿主文件和各自版本的完整依赖放入隔离运行目录，按上述顺序分别执行 `dotnet TeXeed.AppTests.dll <新输出目录> <none|links|invalid> startup-only`。完整场景省略 `startup-only`；普通会话执行 `dotnet Perf.dll <新输出目录>`。各版使用同一宿主，并核对程序集与源码哈希；运行期间避免其他 UI 测试或构建。
