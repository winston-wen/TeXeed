# 多主文件切换性能夹具

Windows 10.0.26300、AMD Ryzen 7 5700X、.NET 10.0.14，同机 1200×800 WPF 窗口、两个各 120 段的源文件、Tectonic 编译。每种操作预热两次，随后采七次；每次以前后相邻的空 ApplicationIdle 调度和布局作为配对，记录原始耗时、UI 线程分配及扣除空操作的结果。可见控件按可见性及窗口矩形相交计数，不检查祖先裁剪。

`source-switch` 是两个版本都有的源码标签切换；`preview-switch` 是新版 A/B PDF 的切换，不含异步页面栅格完成时间。新版运行前增加了第二个主文件及预览，旧版的第二份文件只是普通源码，故控件及行为并不完全相同。对照版本 0.4.0 的提交为 `e1d73e7`，其运行程序集 SHA-256 为 `BCBE53142BD6225E20017F5C6459C4836079CFEFA964715562A8064FDBA3B22D`。

`before-1.json` 为旧版测量，`after-1.json` 为开发中版本的候选测量。开发中版本 DLL SHA-256 为 `2BE444FCE425DF2AFA577E590C3C3FF9A7E18BEFA63160BC573588A35FB17322`；测量后发现相关源码发生了后续修改，因此它不是最终实现的成绩，也不能用当前源码重现该 DLL。`after-final.json` 为修复缩放后、处理嵌套项目边界之前的测量，DLL SHA-256 为 `3F04111D15DD7DC279DBC1046D590ADDDF47058077CE29679FC3C045215D116E`。测量时相对 `e1d73e7` 的已跟踪源码差异保存在 `final-source.patch.txt`，新增入口文件保存在 `entries-final.cs.txt`；这个样本不能替代后续修改完成后的复测。

`after-latest.json` 是嵌套项目边界修复后的复测，DLL SHA-256 为 `6C939B285EC7ECD0DE82E05B145D2D23EE8CB13151FEEB4A38B7CD2AE4B2FADF`；该次的已跟踪源码差异和新增入口文件分别为 `latest-source.patch.txt`、`entries-latest.cs.txt`。文件菜单新增的独立项目入口在测量时没有展开，因此不包含在可见控件数中。

这一夹具没有同方法更早可复核的样本；旧版是目前唯一的历史基准候选，历史最佳暂定为此样本。每版只有一批七样本，不能判断时间差是否稳定；开发中候选结果保留以展示波动。

复测时从 `e1d73e7` 和目标提交构建独立发布程序集；将同一个 `fixture.cs.txt` 分别编译为引用各自程序集的 .NET 10 Windows WPF 测试宿主。每个宿主使用相同项目路径、隔离的 `TEXEED_HOME`，依次传入结果目录、合成项目目录和仓库根目录。运行前确保 `.tools/tectonic/tectonic.exe` 及其缓存可用，并排除其他桌面测试的干扰。源码与输出夹具均可保存在忽略的 `artifacts/`；七个原始样本随此目录保留。
