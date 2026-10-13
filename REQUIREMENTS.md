仿照 [Texifier](https://www.texifier.com/mac) 做一个 Windows 上的 LaTeX 文档编辑器，名叫 TeXeed。

需求

一、打开主 tex 文件，编辑器加载相应的附属文件、文献引用、样式文件。

二、一边打 tex 一边渲染 pdf。

三、tex 语法高亮、注释、缩进、格式化。

四、pdf 预览跟随 tex，右键双击文档部件则跳转到相应的 tex。

五、语法自动补全，`\ref` 、`\eqref` 、`\cite` 目标自动补全。

六、左边栏：大纲、文件树、文字搜索、标签搜索、待办事项


## AI 工作区（0.3.0）

- 独立 AI tab 可关闭和跨栏拖动；标题栏 AI 无子菜单，Alt+A 打开。
- ChatGPT 官方浏览器 OAuth 为主，API Key 手动选择备用；凭据用 Windows 当前用户加密存储。
- 源码普通/矩形选区及 PDF 跨页限尺寸框选生成本地问题条目，用户描述、勾选后统一提交。
- PDF 记录原色截图、指纹和页内坐标，AI 使用只读项目快照检索并定位源码；支持流式回答、停止、失败重试。
- 具体限制、数据范围和验证方法见 docs/ai-workspace.md。
