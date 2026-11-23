# 浏览器公式引用

Windows，Chrome 157.0.8103.80，Node 24.22.3，视口 920 × 760 CSS 像素。三个版本均打开相同的 12 轮回答，每轮含一个双行公式、八段文字，以及字面编号和显式公式引用各一处。在第七轮激活引用。旧版识别字面编号，单击直接跳转。新版单击显式引用就地预览，双击跳转。[夹具及测量脚本](browser-benchmark.cjs)

三个版本交错测三批，每批每项预热两次，取合计 15 个实测样本的中位数。同步一栏从首次 click 捕获到最后一个手势事件处理完成并读取布局。完成计时包括后续两帧与 200 ms 等待。校正一栏逐次减去配对的空等待再取中位数。V8 堆采样间隔为 1,024 B，列出操作完成时仍存活的抽样字节。[逐次数据及页面指纹](browser-samples.json)

| 版本 | 操作 | 同步 ms | 完成 ms | 校正 ms | 抽样字节 B | 视口内控件 | 引用行位移 px |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 0.5.19，12b1077 | 单击跳转 | 18.9 | 239.9 | 10.5 | 38,876 | 22 | 379 |
| 0.5.21，2444ae2 | 单击跳转 | 18.2 | 238.1 | 4.9 | 34,296 | 22 | 379 |
| 0.5.22 | 单击预览 | 0.9 | 237.9 | 20.8 | 2,328 | 22 | 0 |
| 0.5.22 | 双击跳转 | 1.7 | 240.0 | 20.2 | 2,128 | 24 | 379 |

目标已展开时，双击直接定位，省去了整段会话的重绘。同步耗时从上个版本的 18.2 ms 降至 1.7 ms。新版视口多出的两个按钮对应原来以普通文本显示的显式引用。新版的配对空等待中位数约 214 ms，历史版本约 229 ms，逐次校正结果受帧相位影响。包含等待的完成时间在四个场景中介于 237.9 和 240.0 ms。

复测时用同一台机器的 Playwright 和 Chrome，并指定两份历史源码快照及当前原型目录。需要时设置 `TEXEED_PLAYWRIGHT_PATH` 和 `TEXEED_BROWSER_PATH`。在仓库根目录运行以下 PowerShell 命令。

```powershell
$base = 'artifacts/eqref-0522/prototype'
foreach ($rev in @('12b1077', '2444ae2')) {
    $dir = Join-Path $base $rev
    New-Item -ItemType Directory -Force $dir | Out-Null
    git archive --format=zip --output="$dir/prototype.zip" $rev prototypes/ai-chat-v02
    Expand-Archive -Force -LiteralPath "$dir/prototype.zip" -DestinationPath $dir
}
node tests/performance-baselines/eqref-20261123/browser-benchmark.cjs "$base/results.json" "$base/12b1077/prototypes/ai-chat-v02" "$base/2444ae2/prototypes/ai-chat-v02" prototypes/ai-chat-v02
node prototypes/ai-chat-v02/equation-reference-browser.test.cjs
```
