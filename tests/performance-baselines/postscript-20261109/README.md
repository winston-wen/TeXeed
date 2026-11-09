# 文稿附言原型性能样本

在同一台 Windows 机器、Edge 157、1000×820 视口比较历史 `74f01c7`、`0be9eb7`、修改前 `cce0214` 和本次原型。每组比较三轮交错，120 段正文分别配 0 和 60 条评论；每轮预热两次，再测七次。重绘和选区同时记录同步时间和包含两帧的时间，采样分配包含回收对象与测试调度。页面视口相交的控件数不考虑祖先裁切。选区由 Range 构造，不模拟真实鼠标或输入法。

`results-*.json` 和 `allocation-*.json` 是原始结果；`sample-compare.cjs` 与 `sample-allocation.cjs` 为当次脚本。三个版本及当前原型目录包含记录实际请求的资源，`verify.cjs` 用样本中的 SHA-256 校验脚本和资源。重写历史后旧 Git 提交可能不可达，冻结资源仍可用于该场景的对照。此夹具没有附言区，不能代表含附言场景或原生 WebView2 的性能；修改前 `74f01c7` 也没有现有的选区浮动评论入口。

校验并复测（需要设置 `TEXEED_PLAYWRIGHT_PATH` 与 `TEXEED_BROWSER_PATH`）：

```powershell
node tests/performance-baselines/postscript-20261109/verify.cjs
node tests/performance-baselines/postscript-20261109/rerun-compare.cjs 74f01c7
node tests/performance-baselines/postscript-20261109/rerun-allocation.cjs 74f01c7
```

复测脚本使用冻结资源并写入 `artifacts/postscript-perf-replay/`，而当次采样脚本读取当时的 Git 资源和工作树；二者仅更换资源与输出位置，脚本指纹因此不同。可以将提交参数换为 `0be9eb7` 或 `cce0214`。不同机器、浏览器及调度会影响结果。

`selection-profile.cjs` 使用冻结资源另做选区深评，`selection-profile.json` 是当次结果。四组分别为历史、当前、仅禁止浮动按钮定位调度以及复用定位 Range 并将矩形合并移至循环外的反事实；后两组只在测试服务返回脚本时替换代码，不改变产品。各组按顺序及逆序交错三批，分别测 0／60 评论、预热两次后采样七次，V8 间隔 256 字节，包含回收对象和两帧排队；报告采样调用栈、分配总量和按钮几何。其选区由脚本创建，不包含鼠标拖选或系统输入法。执行 `node tests/performance-baselines/postscript-20261109/selection-profile.cjs` 会重新采样并覆盖该文件；若要保留原样本，请在副本中运行。
