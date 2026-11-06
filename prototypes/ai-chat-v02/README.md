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

回答编辑与评论的演示可直接打开 `review-demo.html`。示例与常规入口使用独立存储，包含原件、修改稿和评论回复；可编辑普通纯文字段落，并为同段选区添加评论。公式、代码、标题、列表和跨段编辑暂不支持。可逐轮选择不携带、长期携带或下一次发送时临时携带；新末轮默认长期携带，用户可更改。来源不可用时需恢复或取消后才能发送。Ctrl+G 搜索当前会话修改稿，增强搜索仅为本地模拟。若页面提示字体未生效，请检查浏览器网页字体设置。

运行离线回归：

```powershell
Get-ChildItem prototypes/ai-chat-v02/*.test.cjs | ForEach-Object { node $_.FullName }
```

交互约定见 [AI 圈点与对话](../../docs/ai-annotations.md)，性能检查见 [排版与性能](../../docs/ui-layout-and-performance.md)。
