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

## 窗口与缩放（0.1.34）

最大化通过 `WM_GETMINMAXINFO` 使用当前显示器工作区的物理像素边界，不再同时对内容施加固定内边距；全屏使用同一显示器的完整边界。F11 与菜单共用切换逻辑，保存并恢复普通窗口位置、尺寸及最大化状态，隐藏标题栏时 Alt 菜单改在内容左上角弹出。切换提示在 180ms 内淡入、停留约两秒后淡出，不拦截输入。

侧栏最左侧固定 42 DIP 竖向功能轨道，五个功能按钮共用一组控件，折叠只隐藏右侧面板。面板宽度、搜索内容、文件及编辑状态不随折叠重建。

PDF 缩放滑块及百分比显示订阅预览的实际缩放变化，包含滚轮、版心裁切、窗口宽度改变后的自动宽度。百分比保留两位小数，可点击输入；手动操作取消待执行的源码跟随并关闭自动宽度。关闭自动宽度可冻结当前比例；裁边切换保持自动／手动模式，缩放范围为 20%–300%，裁边模式支持到 800%。

## 菜单、全屏与大纲（0.1.35）

菜单栏常驻，Alt 仅切换菜单焦点；窗口重新激活时重申当前宽／窄布局，退出菜单或最小化不隐藏菜单栏。倍率输入明确指定主题的普通文字、选中文字、选区与光标颜色。

全屏通过 [ITaskbarList2.MarkFullscreenWindow](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist2-markfullscreenwindow) 向 Windows Shell 明确声明；激活时任务栏退到窗口之后。没有隐藏系统任务栏、拦截 Win 键或强制始终置顶；系统仍控制开始菜单与任务栏的激活。

大纲的标题视口使用原有富文本与矢量公式，单行裁切并预留省略号，三角独立占据行尾。单击立即展开／收起，双击撤销第一击的折叠并跳转。右键捕获期间以 45 DIP/s 在完整标题两端往返，每端停留约 600ms；松开、捕获丢失或控件卸载停止移动并复位，内容不滚出原行。左侧五功能轨道统一单击开关面板，移除标题处的三枚折叠／展开图标。

## 大纲长按播放（0.1.36）

左键与右键均支持长按约 400ms 后播放完整标题。左键短按仍展开／收起，双击仍跳转；长按松开不会触发折叠。左键的等待计时器在松开、捕获丢失和控件卸载时取消，播放时暂时关闭标题提示，避免遮挡。

标题的裁切容器使用 Canvas，让富文本以完整自然宽度排版，静止的容器负责裁切；不再让标题自身携带随位移移动的窄布局裁切。尾部文字和行内矢量公式都能滚入视口，往返距离以完整标题宽度计算。回归检查实际渲染的标题末尾，同时覆盖短按、双击、两种按键长按，以及等待和播放期间的捕获丢失。
