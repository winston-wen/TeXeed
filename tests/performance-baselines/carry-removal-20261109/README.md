# 移除携带轮次后的原型性能样本

同一浏览器夹具比较历史基准 `0be9eb74bc12582bc4c2736f30f27c171a980d74`、修改前 `fe878b84bc4ccf5166b4a9f40401061ffaebee69` 和本次原型。每次两组交错测量：120 段、0 或 60 条评论；预热两次，七次取中位数，三轮。重绘及选区分别计同步耗时与包含两帧等待的耗时，分配量按七次总采样除以七，包含调度和夹具开销。可见控件按视区相交判断，不处理祖先裁切。选区由 Range 构造，不模拟输入法。

测量环境：Windows 10.0.19045 x64，AMD Ryzen 7 5700X，Node 24.22.1，Edge 157.0.4341.42，1000×820 视口。V8 采样分配包含采样期间已回收对象，但属于估算而非进程总分配。原始样本为 `results-*.json` 和 `allocation-*.json`；实际采样脚本为 `sample-*.cjs`。三个目录是当次实际请求的五个资源，哈希已记录在样本里。版本号是采样时的 Git 对象；历史重写后对象可能不可达，这些冻结资源仍可用于本场景对照，不能证明未采集的文件内容。

先校验记录、脚本及资源：

```powershell
node tests/performance-baselines/carry-removal-20261109/verify.cjs
```

在安装了 Playwright 和 Edge 的环境中，设置 `TEXEED_PLAYWRIGHT_PATH` 与 `TEXEED_BROWSER_PATH`，运行 `rerun-compare.cjs` 和 `rerun-allocation.cjs`，分别传 `0be9eb7` 或 `fe878b8`。复测脚本使用冻结资源并写入 `artifacts/carry-perf/`；相较采样脚本仅改动资源及输出路径，脚本指纹因此不同。复测结果会随设备、系统、浏览器和调度变化。该夹具只覆盖浏览器原型，不能代表原生 WebView2 宿主。
