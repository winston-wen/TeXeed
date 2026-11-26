# AI 对话原型

需要现代浏览器。重建页面还需 Python 3、XeLaTeX（xeCJK、Microsoft YaHei 字体）和 dvisvgm；运行回归需要 Node.js。

阅读区使用随原型提供的 IBM Plex Sans。冬青黑体简体中文 W3 不随项目分发；可将自行取得的 `Hiragino_Sans_GB.ttc` 放在 `fonts/local/`，或使用系统已安装的对应字体。未提供时浏览器使用后备字体。`fonts/local/` 由 Git 忽略，不会随源码提交。

行内追问气泡取自项目的 [Noto Emoji 图集](../../src/TeXeed.App/Assets/Emoji/ATTRIBUTION.txt)，按 [Apache 2.0 许可证](../../src/TeXeed.App/Assets/Emoji/LICENSE-APACHE-2.0.txt) 使用。

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

在 `index.html` 中，右击正文并选「整理这一段」即可就地改写正文和标题。回车拆段，段首 Backspace 与段尾 Delete 合段。Esc 预览当前段，Ctrl+S 完成整理并回到正文。整理区右键菜单可以调整标题层级、批量删除段落或取消整理。行内公式可在段落源码中编辑。未完成的整理和主输入框草稿分别保存。阅读时右键选「编辑历史」可查看原答及各次修订的作者。「请 AI 改这一轮」用本地模拟演示指定替换和澄清。下游会话保留创建时的引文，返回原文时定位当前整理稿中的追问链接。

`branching.html` 使用预设答复，不运行模型或附件读取工具。加号菜单区分上传和文件、文件夹引用；输入 `@` 可选择固定示例路径。浏览器只保存引用名称的示例，不能保存或持续读取本机绝对路径，也无法引用空文件夹。原生程序可读取所选路径，具体行为见 `docs/ai-workspace.md`。

回答编辑与评论的演示可直接打开 `review-demo.html`。示例与常规入口使用独立存储，包含原件、修改稿和评论回复；回答与历史修改稿只读，可为同段选区添加评论。公式、代码、标题、列表和跨段编辑暂不支持。可逐轮选择不携带、长期携带或下一次发送时临时携带；新末轮默认长期携带，用户可更改。来源不可用时需恢复或取消后才能发送。Ctrl+G 搜索当前会话修改稿，增强搜索仅为本地模拟。若页面提示字体未生效，请检查浏览器网页字体设置。

共同文稿导师演示直接打开 `mentor.html`：点击「填入示例问题」并发送初稿，续问可使用「检查前提」或「请补充归纳法证明」。这是本地固定示例，未连接 AI，版本为本地快照而非 Git 仓库。正文采用 LaTeX 子集，支持普通段落、部分标题、公式与代码；正文及源码只读；其他语法不保证正确显示。

运行离线回归：

```powershell
Get-ChildItem prototypes/ai-chat-v02/*.test.cjs | ForEach-Object { node $_.FullName }
```

交互约定见 [AI 圈点与对话](../../docs/ai-annotations.md)，性能检查见 [排版与性能](../../docs/ui-layout-and-performance.md)。
