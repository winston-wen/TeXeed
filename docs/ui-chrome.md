# 分隔线、滚动条与编译输出

## 行为依据

0.1.24 实现前查阅了 VS Code 官方源码（2026-09-29）：

- [editorOptions.ts](https://github.com/microsoft/vscode/blob/main/src/vs/editor/common/config/editorOptions.ts)：编辑器默认纵向滚动条与滑块宽 14px，横向高 12px，默认不显示端头箭头。
- [scrollableElement.ts](https://github.com/microsoft/vscode/blob/main/src/vs/base/browser/ui/scrollbar/scrollableElement.ts)：鼠标、滚动和拖动控制显示，延迟隐藏为 500ms，拖动期间不隐藏。
- [scrollbars.css](https://github.com/microsoft/vscode/blob/main/src/vs/base/browser/ui/scrollbar/media/scrollbars.css)：显示过渡 100ms，隐藏过渡 800ms，隐藏时禁用命中；滑块区分普通、悬停、拖动颜色。
- [sash.css](https://github.com/microsoft/vscode/blob/main/src/vs/base/browser/ui/sash/sash.css)：分隔线的可见外观与命中区分离，悬停和拖动时高亮。

采用原生 WPF 实现上述交互，没有引入 WebView 或复制 TypeScript/CSS 实现。
按本项目用户要求，即使鼠标停留在区域内，只要没有新动作也会淡出；鼠标停在滚动条上、捕获拖动期间则保持可见。
WPF 使用 DIP 随 Windows DPI 缩放。为避免自动显隐导致文本重排，编辑器的滚动条槽位保持稳定，仅淡化外观并关闭隐藏滑块的命中。自 0.1.25 起，PDF 使用覆盖式滚动条，视口宽度不扣除单侧槽位；滚动条位于对称的页面外侧留白中，显隐不会改变页面居中或缩放。

自 0.1.28 起，编辑器视口取消右侧和底部的固定内边距，正文直接接到滚动条轨道，避免滚动时出现暗色空白带；左侧和顶部仍保留阅读留白。

## 实现约束

- `ScrollActivity` 监听本区域的鼠标移动、按键、文字输入及滚动；过滤 WPF 在布局／命中变化后合成的位置未变的鼠标移动，避免静止时反复唤出；尊重 Windows 关闭动画的设置。控件卸载时移除监听并停止计时器。
- 半透明小圆角矩形：滑块 2 DIP 圆角，普通／悬停／拖动不透明度分别为 35%／55%／75%，颜色来自当前主题。轨道不透明度 25%。
- 滑块最短 24 DIP，通过 WPF Track 使用的系统度量资源控制。不能再用 `Thumb.MinHeight/MinWidth` 硬撑大滑块，否则其显示尺寸与轨道拖动计算不一致。参见 [WPF Track.cs](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/Track.cs) 的 `ComputeScrollBarLengths`。
- 区域之间只留 2 DIP 分隔列，相邻面板不重复画边框。分隔器命中区宽 8 DIP，覆盖两侧各 3 DIP；悬停／拖动高亮不改变列宽。折叠左栏时隐藏分隔器，由图标轨道绘制单条右边框。
- 编译输出独立模板统一页签背景、选中下划线和内容区域。问题列表和完整日志的滚动条均贴齐编辑区右边缘；文本留白由内容负责，不给滚动条额外留白。

界面回归涵盖两套主题、输入显隐与区域隔离、捕获期间不淡出、窄分隔线命中、长文档展开日志后的滑块首尾边界，以及两个日志页签的滚动条对齐。
