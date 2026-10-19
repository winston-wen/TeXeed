# TeXeed 主题

按 Alt → 视图 → 主题切换。两个内置主题是 `ristretto.json` 和 `github-light-default.json`。
GitHub Light Default 的颜色取自 GitHub Theme 6.3.5，许可见发行包的 `licenses/GitHub-Theme-LICENSE.txt`。

复制一个 JSON 文件，修改 `id` 和 `name` 后即可创建主题。文件名必须是 `<id>.json`；
`id` 只能使用小写英文字母、数字和连字符，最多 80 个字符。重新打开主题子菜单即可发现新文件。
选择“重新加载当前主题”应用修改。切换不影响源文件、撤销记录或字体设置。

```json
{
  "id": "my-theme",
  "name": "我的主题",
  "isDark": true,
  "colors": {
    "Accent": "#F9CC6C",
    "ScrollThumb": "#F9CC6C",
    "ScrollThumbHover": "#FCDEA0",
    "ScrollTrack": "#403838"
  }
}
```

颜色支持 `#RRGGBB` 和 `#AARRGGBB`（透明度在前，与 CSS 的八位顺序不同）。省略的颜色沿用 Ristretto；
创建浅色主题时建议复制完整的 GitHub Light Default。未知颜色键或格式错误会拒绝加载，保留当前配色；
启动时主题缺失或损坏会恢复内置默认色。两套内置主题也有嵌入备份。

| 颜色键 | 用途 |
| --- | --- |
| Ink、Muted、Disabled、Subtle | 界面正文、次要文字、弱化文字、辅助线 |
| Accent、Line、Guide | 强调色、边框、层级线 |
| EditorBackground、EditorForeground、CurrentLine | 编辑区底色、正文、当前行 |
| SidebarBackground、PanelBackground、PreviewBackground | 侧栏、面板、PDF 周围底色 |
| Hover、Selection、SearchMatch | 悬停、选中、搜索匹配 |
| TextSelectionBackground、TextSelectionForeground、InactiveTextSelectionBackground | 普通输入框、密码框及系统列表的文字选区底色、字色、失焦底色；与源码选区独立 |
| ChatQuestionBackground、ChatFocus | 对话问题卡片和输入栏底色、输入栏焦点边框和发送按钮 |
| ChatHeaderLine、ChatDivider、ChatThinking | 对话标题通栏金黄线、阅读提示与答复分隔灰线、思考中绿点 |
| SyntaxCommand、SyntaxComment、SyntaxMath、SyntaxBrace、SyntaxNumber、SyntaxField | LaTeX/BibTeX 语法颜色 |
| Todo、Badge | TODO 红点、层级徽标文字 |
| ScrollTrack、ScrollThumb、ScrollThumbHover | 滚动条轨道、滑块、悬停或拖动滑块 |

`isDark` 控制原生标题栏。切换主题时也用它选择 PDF 的深色反色或原色预览；之后仍可单独切换“反色”。
PDF 深色纸张保留现有暖棕、米白映射，主题不会改写 PDF 文件。
普通文本框使用 WPF 非 Adorner 选区绘制，使选中字色生效；富文本仍使用 Adorner，选区透明度固定为 0.35，防止色块盖住文字。
主题选择存于 `%LOCALAPPDATA%\TeXeed\settings.json`；升级到新程序目录时，自定义 JSON 文件需要一起复制。
