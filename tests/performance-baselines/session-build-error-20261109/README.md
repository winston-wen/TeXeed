# 会话编译失败界面性能夹具

同机 WPF 900×700 窗口，以 1000 行、约 65,889 字符的模拟编译日志交替最后一个字符触发失败状态。预热两次，采七次，测到 ApplicationIdle；每次与相邻空操作配对，扣除排队耗时和 UI 线程分配。`before.json` 是修改前样本，`before/` 保留对应界面及编译实现的源码快照；`after/` 保存修改后实现。`fixture.cs.txt` 是当前测量夹具，入口为 AppTests 的 `--ai-build-failure-perf-only`。前后的失败展示操作相同；展开与收起日志为新增交互，没有旧版对应操作。

既有可复核的同场景历史样本只有本次修改前采集的数据，故此样本同时是目前的历史最佳；不能推断更早版本的成绩。修改前的失败状态把日志放进单个状态文本，旧 PDF 可见高度为零；修改后默认折叠日志并保留旧 PDF，功能不同。文本末尾交替本身创建一份大字符串，分配包含这一夹具成本。只测 UI 线程直至 ApplicationIdle，不包含真实编译或异步 PDF 栅格化，也不能代表超长日志展开后的全面性能。

快照 SHA-256：`before/AiSessionViews.cs` 为 `C2C3FA9F9E5012D5675D095032D0B967DC179A7EA30BBD6F1A19C80FAF2857BB`；`after/AiSessionViews.cs` 为 `B48C3B2DA6D5AB8D8AE807117C54E038AE3FB121F2DF6C69D4308A921FC73E5D`；`fixture.cs.txt` 为 `E1169068E0EC3B46CFEB77AFFAC1305D19F01A4C3625AF3DD0BB18D967268640`。
