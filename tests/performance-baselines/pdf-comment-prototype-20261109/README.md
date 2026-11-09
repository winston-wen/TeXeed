# PDF 评论原型性能样本

在 Windows、Edge 无头浏览器 1400×900 视口中，使用 120 行源码、0 或 60 条评论，每种场景采三批，每批预热两次、记录七次。测试程序派发源码输入，记录同步耗时及等待两帧后的耗时、V8 采样分配和视口相交控件数。60 条场景要求预览评论 DOM 数量达到 60；编译和用户真实键盘输入不在计时范围内。分配数为九次（含预热）的总采样平均，不是进程总分配；两帧包括排队与调度。

首次原型调整过程中曾采样，但资源随后改变，缺少可复核的修改前资源；这里不将其作为对照。当前场景无可复核的历史最佳和修改前基准，也不能与旧版 HTML 文稿的其他夹具直接比较。

`results.json` 保存每次测量和资源指纹；`resources/` 保存采样时实际文件，`measure.cjs` 为运行脚本。核验：

```
node tests/performance-baselines/pdf-comment-prototype-20261109/verify.cjs
```

`profile.cjs` 另保留同条件每种场景第一批的 V8 调用栈；`profile-0.json`、`profile-60.json` 和 `profile-results.json` 为样本，`profile-hashes.json` 校验其指纹。采样不能精确拆分浏览器内部开销。

要重测，先启动本原型服务并设置 `TEXEED_PLAYWRIGHT_PATH`（可选设置 `TEXEED_BROWSER_PATH` 和 `TEXEED_PROTO_URL`），再运行 `measure.cjs`；该脚本会覆盖本目录的 `results.json`，需先另存已有样本。
