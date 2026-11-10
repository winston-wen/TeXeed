# 引用与会话图 UI 性能记录

浏览器使用 Windows、Chrome 157.0.8103.40、1000×820，120 轮合成问答；历史 `cf7cb23`、改前 `b6fb887` 与新原型依次采样，每项热身 2 次、采 7 次。动作后等待两帧及 220 ms 并配对空操作，覆盖队列和延迟保存；CDP V8 以 1024 B 采样，扣除配对空操作估计。小额或负采样差、负时间是统计和调度波动，不代表真实负耗时／分配。控件计数检查视口及祖先裁剪。完整环境、逐次数据和资源指纹见 JSON。

## 追问原型

输入同步中位历史／改前／最终均约 0.1 ms，净采样约 597／1,318／1,581 B；滚动为 0／0.1／0 ms、约 4,290／−1,722／386 B，均为单批，不能判断小额分配是否稳定。最终输入滚动样本为 `branch-final.json`，历史和改前为 `samples.json`；最终 JS 指纹 `509f49d98de5bf83f2d11891276a455dcd779161a8a9696e632afc1ed6b1db41`。样本验证滚动位置写入各自的存储；三版可见控件为 4／5／8，新增三个控件与新界面有关，具体角色应按实际布局判断。

60 分支场景的会话图选中，历史／改前／最终同步 1.1／0.8／0.3 ms、净采样约 97,732／92,264／94,336 B、可见控件 13／14／16。最终版双击打开会话 0.9 ms／370,444 B／9 控件；60 条引用的列表打开 5.5 ms／20,740 B／26 控件，编辑入口 1.0 ms／−1,044 B／14 控件，右键菜单 0.1 ms／88 B／13 控件。后三种是新增动作，没有同功能旧版基准，不能称性能达标；单批采样无法确认小额差异。`operations-final.json` 记录数据，图操作加载的 vendor 资源指纹可对照相同版本的 `branch-final.json`。

## 原有 index 原型

初评采用 `7af94c2`、`b6fb887` 和最终页，120 轮，输入同步中位为 1.3／1.1／1.1 ms、净采样约 137／143／143 KB；滚动同步均约 0 ms、净采样 147／146／146 KB。120 轮重绘同步 17.9／18.5／22.9 ms，配对等待 1.5／1.3／17.0 ms；按原有 1.3 倍且增加 8 ms 条件，后者首次触发报警。未调整阈值，随后以相同资料和方法交错三批深评：同步中位历史／改前／最终为 16.5–17.1／17.1–18.2／16.8–17.4 ms，首次变慢未复现；排队校正结果各为 3.1–15.0／13.3–15.0／1.4–12.9 ms，帧调度波动较大。净采样约 850–860／913–937／910–913 KB；最终相对历史增加约 50–62 KB，历史版缺少后续功能，差额混合功能成本与可能的实现开销，尚未完全分离。可见控件 11／19／18。原始初评及深评见 `index-samples.json`、`index-render-deep.json`；最终 `index.html` 为 `eb18c9a6951b1118f95b275789ef1b4593baba4e1d221cf5985e48656a49b801`。

## 原生界面

采用保存的 `native-fixture.cs.txt`，900×760 DIP、120 轮、每轮 3 项过程记录，2 预热＋7 次，等待 ApplicationIdle 与 800 ms 自动保存，并以相邻空等待配对。历史 0.3.47 `b91d3df`／改前 `a0b50ba`（其至 `b6fb887` 原生源码无差别）／本次程序分别为：输入 UI 线程分配 253,864／258,864／258,976 B，滚动 176,328／184,080／180,048 B，编辑框高度 108,768／109,080–109,208／108,584 B，可见控件 40／41／41。相对历史最佳输入增加 5,112 B、滚动增加 3,720 B，历史缺少后续波浪强调等功能；小额差值不能全部归因于必须成本。单批等待耗时受调度影响，不能据此判断稳定快慢。

改前首次变宽仅 1,752 B／即时 0.09 ms，与既有同夹具记录约 550,864 B 明显不符；按要求保留并重跑，第二批为 550,864 B／18.90 ms，异常未复现，原因仍未确定。历史、本次分别为 704,936／550,296 B；不能将异常记录直接当可解释的历史性能最佳。原始数据为 `native-historic.json`、`native-before.json`、`native-before-repeat.json`、`native-after.json`。后者程序集 SHA `DD172380A1C5740DCE3BBBBAE8DD818511AE02E67A3A59D9C3EC6F72F093633D`；后续仅改动新增图片缩略图的增量复用，最终程序集 SHA `48E379DB2E48BF50F0338F2B39BF5E6FFA059475340978135AF97E8985265727`，普通输入／滚动未再次测量最终程序集。

新增图片操作：合成 512×512 BGRA 图，1／5 张缩略图编码加展示单批为 74,728／196,888 B，校正后 5.79／10.33 ms，见 `native-images.json`。随后对已有 5 张图再加第 6 张进行三批交错，优化前 `DD172…` 为 9.98–11.17 ms／234,280 B，增量复用后 `48E379…` 为 4.99–5.46 ms／70,352 B，可见控件均 6；仅为新增功能的自身前后比较。PNG 编码、WPF UI 线程及解码工作包含在内，不含真实剪贴板、复杂照片、非托管和 GPU 全部内存。源码快照分别为 `native-image-source.cs.txt` 和 `native-image-final-source.cs.txt`，测试为 `native-image-update-fixture.cs.txt`。

## 限制与复测

这些都是合成数据，不覆盖真实输入法、大量图片与复杂公式、真实 AI 请求及全部原生 UI。新增交互无可比历史最佳，不能以构建成功或单批样本声明整体性能验收通过。`samples.json`、`samples-final.json` 和 `operations-concurrent.json` 的工作树来源是未冻结中间版，只核对历史、改前资源及脚本；最终文件由 `branch-final.json`、`operations-final.json`、`index-samples.json` 和 `index-render-deep.json` 核对。原生优化前后和历史程序集已在结果中标识哈希，版本差异和复测方法参见 `tests/performance-baselines/ai-docked-composer-20261110/README.md`。`native-source.patch.txt` 是本次已跟踪源码相对 b6 的快照，新增图片类另存为上述两份源码。

```powershell
$env:TEXEED_PLAYWRIGHT_PATH = '<Playwright 包目录>'
$env:TEXEED_BROWSER_PATH = '<Chrome 可执行文件路径>'
$env:TEXEED_PERF_ROUNDS = '1'
node tests/performance-baselines/context-branch-ui-20261110/measure.cjs artifacts/context-perf.json
node tests/performance-baselines/context-branch-ui-20261110/operations.cjs artifacts/context-ops.json
node tests/performance-baselines/context-branch-ui-20261110/index-measure.cjs artifacts/index-perf.json
$env:TEXEED_PERF_ROUNDS = '3'
node tests/performance-baselines/context-branch-ui-20261110/index-render-deep.cjs artifacts/index-deep.json
node tests/performance-baselines/context-branch-ui-20261110/verify.cjs
```
