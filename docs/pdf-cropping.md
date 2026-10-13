# PDF 自动裁边

自 0.1.27 起，横向裁量只取决于排版后的正文版心。文档类默认设置、`geometry`、装订偏移及奇偶页边距均参与计算；不从字迹外接矩形、最长行、表格宽度或段落缩进猜边距。正文两侧额外保留 6 PDF pt 的显示留白。超出版心的内容不会使页面自动变宽；关闭“裁边”即可查看完整页。

XeLaTeX、pdfLaTeX、LuaLaTeX 和 latexmk 使用 `.texeed/build/texeed-driver` 中的编译入口加载原始主文件，在 LaTeX `shipout/after` 钩子中逐页记录纸张大小、`hoffset`、奇偶页边距和 `textwidth`。入口与原文件同名，保持 jobname，源文件及行号不变。仅在整次编译成功后，将 `.texeed-layout` 随 PDF 和 SyncTeX 发布到预览目录；记录无需额外编译遍数。

Tectonic 必须保留原主文件作为入口，以保持本地样式、图片和参考文献的搜索路径；它从 SyncTeX 中读取标准 LaTeX 输出例程的整页正文容器。读取的是该容器声明的排版宽度，不是内部字形或行的实际占用范围。同一方案也用于缺少记录文件的兼容回退。

未识别的自定义输出例程、损坏或缺失的版心信息、页数不匹配，以及旋转 90°/270° 的页面保留整页宽度，不回退到按墨迹猜测。老 LaTeX 内核没有 shipout 钩子时，尝试上述 SyncTeX 容器；无法识别则同样保留整页宽度。

纵向保持基于原色 PDF 栅格的检测：去掉上下空白，保留 6 pt 安全留白及图块内部空间。自 0.2.1 起，独立的“压缩纵向空白”选项控制是否进一步压缩超过 36 pt 的整行空白；只在自动裁边开启时生效，关闭裁边保留其选择。单次分析同时缓存分段压缩映射和仅去掉上下白边的连续映射，切换不重新扫描 PDF。连续滚动、适合宽度、鼠标缩放锚点和 SyncTeX 使用同一裁切坐标映射。裁边仅影响预览，不改变导出的 PDF。

`--layout-only` 自动测试仅使用 Tectonic、XeLaTeX 和 pdfLaTeX，MiKTeX 测试进程通过 `--disable-installer` 禁止临时安装依赖和安装弹窗，不更改用户的 MiKTeX 全局配置。其他引擎不在默认的本机测试矩阵中。

依据：[LaTeX shipout 钩子定义](https://github.com/latex3/latex2e/blob/main/base/ltshipout.dtx)、[Tectonic 编译入口和搜索路径约束](https://tectonic-typesetting.github.io/book/latest/v2cli/compile.html)。
