# 原生指针压力检查对照

同一台 Windows 机器、.NET 10、820×900 DIP 的 WPF 窗口，按旧／新／新／旧顺序运行 `--ai-reliability-only`，每次使用独立输出与 AI 配置目录。夹具构造 8 轮、每轮 60 节含公式长回答；滚动 30 次并用 `InputHitTest`，随后通过 `SetCursorPos` 移动实际系统指针 25 次，每次等待 16 ms 和 Dispatcher Background，按原测试的 500 ms 限制检查单次总耗时。每版两批，遇到首次失败即结束该批。没有更改阈值或等待逻辑，仅在断言前输出每次实测耗时。

旧版取 Git `d304eeb` 归档并在隔离目录构建；新版取该提交归档，再覆盖测量时工作树的源码和测试。两版均在隔离目录用 `dotnet build tests/TeXeed.AppTests -c Release -m:1` 构建。旧／新程序 DLL SHA-256 分别为 `028411E6E4976DC273E5B76ED5D69DF0B69BC0F292E8524D9D6809E64FDF86B3`／`DA3E271B60F340F167CE45D907053CF56DBAA95BD2EF3F1DFB0236C986F7D9D8`；这些是隔离构建而非已发布二进制。为打印数据，仅把 `AiReliabilityTests.cs` 中原断言改为 `Console.WriteLine($"Pointer sample {i}: elapsed={watch.Elapsed.TotalMilliseconds:F1}ms");` 后紧接原断言；记录完整测试源时的 SHA-256 旧／新分别为 `2047C1A5F3A718F54806C8DDF0E8127654DF83C40647A9133BAECE1F2CEAECCB`／`CA054C8132EB8E00240C9E2E217FBF32C5C53AD5183057A06E3AAE5434BB3365`。源码按版本各自包含相同的指针压力段；新版另含附件恢复测试。

| 次序 | 版本 | 结果 | 已记录的最大单次指针耗时 |
| --- | --- | --- | ---: |
| 1 | 0.4.6 | 失败 | 520.2 ms |
| 2 | 功能构建 | 全部 55 项通过 | 380.2 ms |
| 3 | 功能构建 | 失败 | 711.2 ms |
| 4 | 0.4.6 | 失败 | 664.8 ms |

`old1.txt`、`new1.txt`、`new2.txt` 和 `old2.txt` 保存与指标相关的完整采样；未记录会话或用户文件内容。程序原先另有两次未加打印的新版运行在同一检查失败。旧版同环境也失败，不能直接归因本次附件改动；两版每版只有两批，且失败时后续样本未运行，不能据此推断性能改善、无退化或已通过。该计时包含定时器等待、Dispatcher 调度和系统指针移动，不单独记录各项原因，因此也不能确认根因。真实 journal 恢复需以通过该套件的运行及其他专门回归共同判断，不能将压力检查跳过视为全部通过。
