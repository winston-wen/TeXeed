# 用户书签目录测量

Windows 本机 Release、120 轮及每轮同一回答，测量 `--ai-bookmark-ui-only` 中首次打开、输入无匹配、清空查询和可见控件；打开和输入后等待 ApplicationIdle。设 `TEXEED_BOOKMARK_PERF_USERS=1` 时程序化加入每轮 3 个用户书签，共 360 条；按用户书签界面创建删除按钮。运行：

```powershell
$env:TEXEED_BOOKMARK_PERF_USERS='1'
dotnet run --project tests/TeXeed.AppTests -c Release --no-build -- --ai-bookmark-ui-only --output artifacts/bookmark-user-performance
```

`sample.json` 为一次运行中的结果；七次搜索及清空数据逐项列出，冷启动只有一次。测试夹具 SHA256：E4AB21143144B5BA72AE2BD700BDF0B8A48083B370E93498B14B64555CF86210。物理鼠标与键盘未验证。历史比较见 docs/ui-layout-and-performance.md 的 0.3.51 书签记录；旧夹具 360 条为自动生成的书签，不具删除按钮，当前为用户书签，且其他 UI 功能不同，因此不是严格相同功能场景。
