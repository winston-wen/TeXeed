# AI 对话原型

需要现代浏览器。重建页面还需 Python 3、XeLaTeX（xeCJK、Microsoft YaHei 字体）和 dvisvgm；运行回归需要 Node.js。

在项目根目录重建：

```powershell
python prototypes/ai-chat-v02/build.py
```

直接打开 `index.html`，或在此目录启动本地服务器：

```powershell
python -m http.server 8766 --directory prototypes/ai-chat-v02
```

打开 http://127.0.0.1:8766/ 。`?test=1` 使用独立的测试存储。按 Ctrl+Alt+T 打开试验台，选择成功、校对或失败回放。

视觉预览打开 http://127.0.0.1:8766/?preview=visual 。`visual-preview.json` 提供纸飞机试飞手记和阅读清单，展示折叠轮次、过程详情、公式、表格和编辑草稿。常规入口与视觉预览分别保存会话和草稿。

回答编辑与评论的演示可直接打开 `review-demo.html`。示例与常规入口使用独立存储，包含原件、修改稿和评论回复；可编辑普通纯文字段落，并为同段选区添加评论。公式、代码、标题、列表和跨段编辑暂不支持。Ctrl+G 搜索当前会话修改稿，增强搜索仅为本地模拟。若页面提示字体未生效，请检查浏览器网页字体设置。

共同文稿演示直接打开 `mentor.html`：点击「填入示例问题」并发送初稿，续问可使用「检查前提」或「请补充归纳法证明」。编辑框用于填写本次随文稿发送的附言；模拟模型的简短附言写入文稿开头。已编辑或附有评论的旧附言得到保留，新摘要写入同一附言区。这是本地固定示例，未连接 AI，版本为本地快照而非 Git 仓库。正文采用 LaTeX 子集，支持普通段落、部分标题、公式与代码；其他语法不保证可视编辑。

运行离线回归：

```powershell
Get-ChildItem prototypes/ai-chat-v02/*.test.cjs | ForEach-Object { node $_.FullName }
```

交互约定见 [AI 圈点与对话](../../docs/ai-annotations.md)，性能检查见 [排版与性能](../../docs/ui-layout-and-performance.md)。
