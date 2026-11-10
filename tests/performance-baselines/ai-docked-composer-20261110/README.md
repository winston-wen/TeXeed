# AI 底部编辑区的原生性能样本

同机 Windows 10.0.26300、AMD Ryzen 7 5700X、.NET 10.0.14，独立配置、Release 构建、WPF 900×760 DIP 窗口。夹具为 120 轮，每轮 3 条过程记录、每条 200 行详情，回答和草稿为合成文本；窗口宽度在 900／620 DIP 之间切换。依次测量输入、窗口宽度、阅读区滚动、编辑框高度，每项预热两次后测七次。可见控件按 WPF IsVisible 与窗口矩形相交估计，未判断全部祖先裁剪。

每次操作计时至 ApplicationIdle，并继续等待 800 ms、保存计时器停止、保存锁释放及 ApplicationIdle，覆盖 700 ms 自动保存及队列。每次与相邻的同样等待但不操作的空样本配对，JSON 保存原始值、空样本和扣除后的耗时及 UI 线程分配；`feedbackMs`、`feedbackBytes` 单独记录初次 ApplicationIdle 前的反馈，未扣除队列。800 ms 两次等待的差异可能超过操作本身，因此校正后的时间允许为负，不应把这些负数当作实际负耗时或可靠排序；线程分配不含后台线程及 GPU。测试不含真实输入法、模型请求和用户手动发送。

`history-verified.json` 是 0.3.47（发布程序集来源 `b91d3df`），`before-verified.json` 是改前 0.4.1（`892e106`），`after-verified.json` 是此次改动。各运行一批，前后不是交错重复测试，耗时稳定性尚未确认。`before-1.json` 是对自动保存计时器未作显式完成检查时的初测，只供追溯，不能与严格夹具结果混比。此夹具下前两版是现有可复核历史候选：每个分配及控件指标以两版中的较小值比较；耗时受配对等待抖动影响，不确定的负值不用于稳定排名。0.3.47 没有后来加入的波浪强调和书签功能，差异包含功能成本及可能的实现开销。

测量程序集的 SHA-256：历史版 `0A78D42976B296D6C0343C7B3D5F51DB29D322FF44DE7393B599767988EBD37D`，修改前 `31A212DB3BD572A8AD7D393692F69CE8941DCBC3489A9B60293B972C7E0727C2`，修改后 `43D26A0EA42E9D78E588BFFEE7586A8AA2C763FB57A6CED987E2B9CCD30774C8`。修改后测量时的 XAML 和相对 `892e106` 的程序源码差异分别保存在 `after-AiPanel.xaml.txt` 和 `after-source.patch.txt`。夹具 `fixture.cs.txt` 的 SHA-256 为 `012EFEABE5CB7AF359FACF481EFA26E4D34BBA4C9F69276CC04F6DCCC28F99C6`。

复测时将同一 `fixture.cs.txt` 编译为 .NET 10 Windows WPF 可执行宿主，分别引用对应版本的 TeXeed 与 TeXeed.Core 程序集，并保留各自发布目录依赖；命令行参数为隔离结果目录。保证运行机器、DPI、桌面和主题一致，串行运行并排除其他桌面测试。七次完整样本保存在同目录的 JSON，临时构建和配置位于忽略的 `artifacts/composer-dock-perf/`。
