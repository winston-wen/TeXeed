# 表单输入与对话性能（2026-11-17）

两版均在同一 Windows 10.0.26300、WindowsDesktop 10.0.7 主机、900×760 DIP WPF 窗口中串行采样；改前是 0.5.3，改后为版本号 0.5.4 的 Release 功能构建。每项预热两次并采七次，记录首次 ApplicationIdle 的反馈时间、配对 800 ms 空等待与草稿保存锁后的耗时和 UI 线程分配、可见控件数。窗口必须可见，测试期间不并行构建或运行其他 UI 测试。计时不包含物理键盘、后台线程和 GPU；配对时间可能为负。

`panel-width-validated.cs.txt` 沿用 `../steering-20261112/panel-fixture.cs.txt` 的 120 轮场景，在变宽后额外核实实际窗口宽度及面板宽度变化，其他采样逻辑相同。`form-input-fixture.cs.txt` 沿用 `../forms-native-20261112/forms-fixture.cs.txt` 的首项表格输入、18 个单行表格字段以及其先前设置和采样，省略该项之后的其他操作；新编译的宿主及源码哈希见各 `*-fingerprints.json`。它通过属性设置文本，不模拟录屏中的焦点与键盘操作，正确性由 `--ai-form-viewport-only` 单独验证。

`before-panel.json`、`after-panel.json`、`before-form.json`、`after-form.json` 保存全部逐次数据；可复核历史最佳和其功能差异分别见 `../graph-canvas-20261111/README.md` 和 `../forms-native-20261112/README.md`。本次程序集、两份宿主及夹具的 SHA-256 均在指纹文件中。正式发布产物须另行与功能构建核对。

`resize-fixture.cs.txt` 与 `../quote-scroll-20261116/resize-fixture.cs.txt` 相同，只测变宽，未先执行输入操作。`resize-before-a.json`、`resize-final-a.json`、`resize-final-b.json`、`resize-before-b.json` 按该顺序交错运行；宿主 SHA-256 `74B8CFB77C6FFB31CE160476291472CCCB3AC2A575F1D1426534CE3CC5C92909`。`intermediate-*` 是高多行框光标修复之前的样本，不代表最终代码。

复测可用 `../scroll-wheel-20261116/Harness.csproj`，将 `ProductDir` 指向对应版完整 Release 依赖目录、`Fixture` 指向上面源码，分别以 `AssemblyName=Perf` 和 `AssemblyName=TeXeed.AppTests` 构建，后者使用应用授予测试程序集的内部访问权限。将对应宿主文件和完整产品依赖放入独立目录，依次运行 `dotnet Perf.dll <新输出目录>` 和 `dotnet TeXeed.AppTests.dll <新输出目录>`。两版应使用同一宿主二进制，并记录哈希。
