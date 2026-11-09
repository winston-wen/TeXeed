# 重做 0.4.0 的原生阅读性能样本

2026-11-09 在同一台 Windows NT 10.0.26300、AMD64 Family 25 Model 33 Stepping 2、.NET 10.0.7、WPF tier 2、DPI 200%、ristretto 主题环境下运行。使用独立配置和 Release 构建，窗口 900×760 DIP；120 轮、每轮 15 条过程、详情 2000 行、六级标题与密集强调。每批热身一次、采样七次，等待 ApplicationIdle；JSON 保存逐次样本及中位数。方法源为 `tests/performance-fixtures/high-density-unique-width-fixture.cs` 和 `wpf-history-host.cs`。

重做版从 `2847f34` 导出源码，将本次兼容性修复后的 `AiModels.cs`、`AiSegment.cs` 覆盖后构建，尚未包含最终版本号及其他后续改动。修改前从 `31140d6` 导出；两版注入同一性能夹具，旧版宿主使用历史宿主，新版宿主使用自身 `Program.cs`（历史宿主引用了该版已经删除的测试方法），测试入口一致。各版本分别执行 `dotnet restore tests/TeXeed.AppTests/TeXeed.AppTests.csproj --locked-mode -p:NuGetAudit=false --ignore-failed-sources`（重做版另加 `-r win-x64`）及 `dotnet build tests/TeXeed.AppTests/TeXeed.AppTests.csproj -c Release --no-restore -m:1`，重做版另加 `-r win-x64`。执行各自测试程序 `--ai-ui-perf-only --with-headings --output <独立目录>`。源文件和程序集的 SHA-256 见 `hashes.json`。构建副本位于忽略的 `artifacts/perf-redo-native/`，不作正式发布。

0.4.2 在同一夹具中未生成过程记录行，无法继续测量，失败日志保存在 `before-failure.txt`；因此没有同场景修改前结果，亦不能用 PDF／共同文稿与逐轮阅读的不同操作直接比较。这一缺口不能用本次与历史基准的差额替代。

重做版滚动三批中位数 80.19–91.99 ms／5,880,360–5,892,328 B；详情 13.16–16.53 ms／1,438,624–1,443,736 B；变宽 93.22–100.71 ms／3,366,944–3,581,888 B；可见控件均为 28。历史同夹具最佳依 `docs/ui-layout-and-performance.md` 0.3.54 记录分别为滚动 74.66 ms／4,696,648 B、详情 11.90 ms／1,419,776 B、变宽 194.22 ms（暂定）／1,854,424 B、可见控件 27。变宽分配较历史最佳增加约 82%–93%，超过既有报警条件。历史最佳缺少波浪强调和书签；此前归因显示 WPF 字符位置查询是主要增量，波浪缓存优化曾降低约 7%，但无法把此次剩余差距完全分成必要功能成本与可避免的实现退化。

这个历史夹具等待 ApplicationIdle，但没有配对空操作进行排队校正；结果包括队列等待和其他 UI 工作。尚未完成具备排队校正的历史版交替复测，也未完成正式最终构建及 0.4.2 主文稿场景的可比测试，不能声称性能验收通过。
