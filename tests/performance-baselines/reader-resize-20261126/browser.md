# 长会话中打开和关闭查找栏

同一份 48 轮夹具，视口 960 × 820 CSS 像素，DPR 1。第 20 轮有 20 段推导，下接双行公式和短段落。相邻轮次中有逐条折叠和已收纳的区间。在双行公式位于视口中部时按 Ctrl+F，再按 Esc。功能回归还经过滚轮来回滚动、反复开关五次、右键进入批注模式、退出后的提示自动消失，以及搜索已收纳轮次的实际流程。用浏览器逐帧核对滚动位移、相邻轮次边界及命中对象，对照公式和续读段落在原位置实际绘出的像素。[夹具](../../../prototypes/ai-chat-v02/reader-chrome-browser.test.cjs)

Chromium 158.0.8127.38，同机依次运行 0.6.4、0.6.5、0.6.5、0.6.4。每批两次预热，七次采样，表中为每版每种操作 14 次的中位数。同步耗时从按键捕获到按键分派完成并读取布局。完成耗时包含两帧和额外 120 ms。校正值逐次扣除同一测量前的等长空等待，受帧相位影响。V8 以 256 B 间隔对 JS 堆分配进行抽样，包含测量期间已回收的对象。控件数按外框与窗口可见区域相交统计。[原始样本与页面指纹](browser.json)

| 版本 | 操作 | 同步 ms | 完成 ms | 校正 ms | 抽样分配 B | 控件 | 正文屏幕位移 px |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 0.6.4 | 打开 | 0.60 | 143.80 | 6.35 | 5,826 | 26 | +46.50 |
| 0.6.5 | 打开 | 0.70 | 137.85 | −0.55 | 5,762 | 26 | −0.50 |
| 0.6.4 | 关闭 | 0.40 | 138.80 | −2.55 | 5,986 | 23 | 0 |
| 0.6.5 | 关闭 | 0.50 | 136.90 | 3.60 | 6,408 | 23 | 0 |

0.6.5 在查找栏显示时保住视口中部的阅读行，两版使用相同数量的控件。关闭查找时多了恢复原键盘焦点和保住正文坐标的工作；关闭的 JS 抽样分配中位数增加 422 B。显示栏的高度是 46.5 px，当前浏览器对滚动偏移取整，开启时正文移动了半个像素，关闭后归零。每次开关都保留这次取整的余量，连续五次开关后没有累积偏差。

## 在 Windows 复测

在已有 Playwright 和 Chromium 的环境下，从仓库根目录运行。封存的历史页面使用 0.6.4 的源码提交。每次运行性能模式会输出一批独立数据；重做两批时更换输出文件名。运行普通功能回归时清除性能模式变量。

~~~powershell
New-Item -Type Directory -Force artifacts/reader-resize-bench | Out-Null
git archive --format=zip --output=artifacts/reader-resize-bench/064.zip 21d0099 prototypes/ai-chat-v02
Expand-Archive -LiteralPath artifacts/reader-resize-bench/064.zip -DestinationPath artifacts/reader-resize-bench/064 -Force
npm install --prefix artifacts/reader-resize-bench --no-save --ignore-scripts --no-audit --no-fund playwright@1.62.1
$env:TEXEED_PLAYWRIGHT_PATH = (Resolve-Path artifacts/reader-resize-bench/node_modules/playwright).Path
$env:TEXEED_BROWSER_PATH = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
$env:TEXEED_PROTOTYPE = (Resolve-Path artifacts/reader-resize-bench/064/prototypes/ai-chat-v02).Path
$env:TEXEED_READER_CHROME_PERF = 'artifacts/reader-resize-bench/064.json'
node prototypes/ai-chat-v02/reader-chrome-browser.test.cjs
$env:TEXEED_PROTOTYPE = (Resolve-Path prototypes/ai-chat-v02).Path
$env:TEXEED_READER_CHROME_PERF = 'artifacts/reader-resize-bench/065.json'
node prototypes/ai-chat-v02/reader-chrome-browser.test.cjs
Remove-Item Env:TEXEED_READER_CHROME_PERF
node prototypes/ai-chat-v02/reader-chrome-browser.test.cjs
~~~
