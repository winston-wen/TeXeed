# 共同文稿侧栏原型性能对照

同机 Chrome、1000×820、120 段及 0／60 评论，对修改前工作树（before，来自 HEAD）、历史 74f01c7 和修改后工作树分别交错三批；每批预热两次、测七次，包含两帧调度。两种回放脚本分别记录耗时／可见控件和 V8 采样分配；采样包含回收对象及脚本、排队开销，并非精确总分配。该夹具用于只读排版重绘及程序化 Range 选择，不覆盖新源码选区、真实输入法、PDF 或原生界面。

同机 Edge 在沙箱中启动失败，因此本次三版均使用 Chrome，不直接比较既有 Edge 数值。历史版本缺少作者标签与现在的评论布局，不能将差异全部归于实现退化。结果和本次实际资源指纹记录在 JSON；before 与 after 文件夹冻结五个核心资源，74f01c7 使用相邻的 postscript-20261109 冻结资源。脚本的 after 读取工作树，因此复测应先核对 JSON 中 after 指纹或将冻结资源部署到对应原型路径。依赖 TEXEED_PLAYWRIGHT_PATH 和 TEXEED_BROWSER_PATH；输出保存到 artifacts/mentor-sidebar-performance。
