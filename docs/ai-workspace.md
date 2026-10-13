# AI 工作区（0.3.2）

## 使用

1. 打开项目，点标题栏最末的 **AI**（Alt+A）进入 **AI 对话** tab。标题右边依次为设置齿轮与关闭按钮；两种 AI tab 都可关闭、跨栏拖动，关闭再打开保留问题和输入草稿。
2. 点对话 tab 的齿轮进入独立 **AI 设置** tab，点 **Continue with ChatGPT** 在系统浏览器授权。新增账号用“添加账号”；下拉列表可切换已保存账号/工作区。登录后从账号提供的模型列表选择模型。
3. API Key 是可选备用连接，勾选后单独使用 OpenAI API 额度，不会在 ChatGPT 用量耗尽时自动切换计费。Key 输入使用密码框，保存后清空输入框。
4. 源码/PDF 右键 → 勾选 **AI 圈点**。指针变成 Windows“帮助选择”。左键单击标记点位；长按或拖动选择源码句子/PDF 区域，松开后立即进入对话编辑栏，无须再次确认。右键菜单还包括常用编辑命令，颜色跟随主题。
5. PDF 框选可跨页，靠近上下边缘自动滚动。取消右键菜单中的勾选、右键双击、Esc 均可退出圈点；为保留右键双击，圈点状态下右键菜单会等待系统双击时间后打开。缩放、换 PDF、隐藏预览取消尚未完成的框选，已收集条目保留。
6. 对话页采用上方消息区、下方固定编辑栏。可直接输入问题，或在圈点条目上填写单独说明；空白条目可使用下方统一问题。Enter 发送、Shift+Enter 换行。发送输入内容和勾选条目，未勾选条目不发送。
7. 回复逐步显示，支持“停止”。断流、取消、权限或额度错误不会标为成功，保留条目供重试。重试是新请求，可能再次消耗额度。

8. 输入栏的模型/推理强度下拉框可直接切换，与 AI 设置同步并保存。打开已登录的 AI 页会自动读取模型目录；↻ 可手动刷新。推理选“默认”时省略 effort，让服务决定；其余选项写入请求的 `reasoning.effort`。账号目录的支持等级与公开 Responses 参数取交集，未知 API 模型仅提供默认。
9. 页首 ＋ 新建独立对话；“历史”按标题、问题或回答搜索，打开/删除整个对话。当前对话可通过查找按钮或 Ctrl+F 搜索，上/下按钮在匹配问答间跳转，Esc 收起。每轮右侧 × 删除该轮问题和回答，后续请求不再携带被删除的轮次。
10. 每个对话独立保存草稿、消息与圈点条目；切换/删除前结束正在发送的请求并保存草稿。删除最后一个对话后得到新的空对话。旧版单一对话自动迁移为历史会话，保留内容；显示时去掉旧版内部 ID 前缀，不自动合并重复问答。旧空回答标明“未保存回答”，并从后续请求历史中跳过。

## 选区和上下文

- 源码圈点保存收集时的文本、相对路径、行号、UTF-16 偏移和文件 SHA-256。点位文本为空，由 AI 按文件/行号读取上下文；长按或拖动按句子边界收集文本。
- PDF 保存原色 PNG、指纹、1 起始页码及纸张左上原点的 SyncTeX/PDF 点坐标。单击另保存精确点位与附近截图；跨页分开保存，压缩纵向空白分段反映射，旋转和 CropBox 通过现有坐标变换处理。
- 框选最大 1600 × 2400 DIP、3 页、16 个压缩区块；每个条目 PNG 合计不超过 8 MB。渲染使用每页 400 万像素预算，不生成整个文档截图。
- 单个源码条目最多 4 万字符；一批最多 20 个问题、8 万字符和约 18 MB PNG。每个项目最多保留 100 个条目、约 32 MB 截图。
- 点击提交后固定当前可编辑项目文件的内存快照，包含未保存修改。AI 可调用 `project.list_files`、`search_source`、`read_source` 来定位 PDF 对应源码，并被要求引用文件/行号。定位不保证成功，找不到时应明确说明。
- 工具只读这份快照，不能任意读取磁盘、执行 shell 或修改文件。检索结果与读取行数有界，每次回答最多 16 轮；源码/PDF/工具内容均作为资料对待。
- 当前批次携带最近至多 8 轮文字问答（合计 4.8 万字符）；旧图片不会自动重发。如需继续讨论旧选区，请重新勾选其条目。
- 只携带当前对话的历史，其他对话的消息/圈点不进入本次上下文。项目 JSON 使用 Conversations/ActiveConversationId 保存，不重复序列化活动会话媒体。
- 问题和回答保存在 `%LOCALAPPDATA%\TeXeed\ai\projects`，以项目主文件完整路径的哈希隔离；`TEXEED_HOME` 设置时保存在其 `ai` 子目录。文本/截图收集本身不发送网络请求。登录、模型刷新以及提交会连接 OpenAI。

## 认证实现与限制

采用 OpenAI 公开的本地/open-source Sign in with ChatGPT 流程：系统浏览器、127.0.0.1 回调、state、PKCE S256 和 OIDC nonce；验证 OpenAI JWKS 签名、issuer、audience、时间、身份与额度授权 scope。每个安装保留稳定 host ID，各账号保留独立 issued client ID。登录依赖该账号/工作区允许第三方应用使用 ChatGPT 计划额度。

Access/refresh/ID token 与 API Key 使用 Windows DPAPI CurrentUser 加密，原子更新并通过文件锁串行化刷新。退出尝试撤销刷新令牌并清除本机令牌，保留非令牌账号/客户端映射供重新登录；网络原因未确认远端撤销时会说明，可在 ChatGPT 设置断开应用。不会读取 Codex、浏览器或其他应用的登录凭据。

HTTP Responses 使用 `store:false` / `stream:true` 和命名空间工具，客户端自行维护历史。仅收到 `response.completed` 才完成批次。API Key 为单独选择的备用模式。

0.3.1 在用户授权下使用已保存的 ChatGPT 连接做了 6 次 Luna 推理请求（本版本上限 20），涵盖简单文本、历史与源码检索。确认并修复了终止事件 output 为空导致漏掉已完成工具调用的问题：收集 `response.output_item.done`，执行本地工具后续接，纯空响应不再标为已回答。错误显示 HTTP 状态、服务错误码和请求编号，并隐藏凭据形态的内容。

真实测试只发送生成的 hello 源码和简单问题，未发送用户文档。模型/额度以账号实际返回为准。查看本机 Claude 插件的可访问界面和公开样式作为布局参考；computer-use 截图采集超时，未向 DeepSeek 发送消息。

0.3.2 查询实际账号模型目录，读取 supported_reasoning_levels。使用 1 次 Luna 请求验证显式 low 推理参数，得到正常回答；未发送用户文档。配置、加密凭据和对话 JSON 的原子替换遇到 Windows 短时文件占用时最多等待 240 ms 后重试，保留原文件直到替换成功。

推理参数参考：[OpenAI reasoning guide](https://developers.openai.com/api/docs/guides/reasoning)、[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)。

官方参考：
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations

## 回归验证

`TeXeed.AppTests.exe --ai-only --output <隔离测试目录>` 覆盖身份令牌、加密存储、流式工具往返、终止数组为空、纯空响应、断流失败、批量提交、项目/会话/草稿持久化、旧数据迁移、逐轮及整段删除、历史/消息搜索、模型与推理参数实际写入请求、短时文件锁、源码与 PDF 选区、两个 AI tab、帮助选择指针和三种退出方式。该回归使用假服务，不连接真实账号。

显式授权后可运行 `--ai-live-probe` / `--ai-live-probe --full --tools` / `--ai-live-probe --full --low`。`--catalog-only` 仅读取模型目录，不发送问答。探针仅选 Luna，禁止 API Key 计费替代，输出协议字段名和简单回答，不输出令牌；所有推理请求按当前版本在 `%LOCALAPPDATA%\TeXeed\ai\live-test-<版本>.count` 累计，达到 20 拒绝继续。该模式不会自动运行。
