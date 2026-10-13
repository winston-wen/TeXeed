# AI 工作区（0.3.0）

## 使用

1. 打开项目，点标题栏 **AI**（Alt+A）。AI 是可关闭、可拖到任一栏的独立 tab；关闭再打开保留当前项目问题。
2. 展开“连接与模型”，点 **Continue with ChatGPT**，在系统浏览器完成授权。新增账号用“添加账号”；下拉列表可切换已保存账号/工作区。登录后从账号提供的模型列表选择模型。
3. API Key 是可选备用连接，勾选后单独使用 OpenAI API 额度，不会在 ChatGPT 用量耗尽时自动切换计费。Key 输入使用密码框，保存后清空输入框。
4. 源码普通选区或矩形选区右键 → **选取内容用于 AI 问答**。右键菜单还包括撤销/重做、剪切/复制/粘贴、全选、注释、缩进、查找/替换和格式化。
5. PDF 右键 → **框选 PDF 区块用于 AI 问答…**，左键拖框。靠近上下边缘自动滚动，可以跨页；松开后确认或取消。Esc 取消。缩放、换 PDF、关闭预览可见页会取消尚未确认的框选。
6. 在问题卡片写描述；可继续添加多个源码/PDF 问题，或点“＋ 问题”提不带选区的问题。勾选需提交的卡片，再点“提交勾选问题”。未勾选条目不发送。
7. 回复逐步显示，支持“停止”。断流、取消、权限或额度错误不会标为成功，保留条目供重试。重试是新请求，可能再次消耗额度。

## 选区和上下文

- 源码卡片保存收集时的文本、相对路径、行号、UTF-16 偏移和文件 SHA-256。矩形选区按实际选中行段保存，不自动包含整段矩形间隔外的文本。
- PDF 保存原始颜色 PNG、PDF 指纹、1 起始页码及以原始纸张左上为原点的 SyncTeX/PDF 点坐标。跨页分开保存；压缩的纵向空白分段反映射，旋转和 CropBox 通过现有坐标变换处理。
- 框选最大 1600 × 2400 DIP、3 页、16 个压缩区块；每个条目 PNG 合计不超过 8 MB。渲染使用每页 400 万像素预算，不生成整个文档截图。
- 单个源码条目最多 4 万字符；一批最多 20 个问题、8 万字符和约 18 MB PNG。每个项目最多保留 100 个条目、约 32 MB 截图。
- 点击提交后固定当前可编辑项目文件的内存快照，包含未保存修改。AI 可调用 `project.list_files`、`search_source`、`read_source` 来定位 PDF 对应源码，并被要求引用文件/行号。定位不保证成功，找不到时应明确说明。
- 工具只读这份快照，不能任意读取磁盘、执行 shell 或修改文件。检索结果与读取行数有界，每次回答最多 16 轮；源码/PDF/工具内容均作为资料对待。
- 当前批次携带最近至多 8 轮文字问答（合计 4.8 万字符）；旧图片不会自动重发。如需继续讨论旧选区，请重新勾选其条目。
- 问题和回答保存在 `%LOCALAPPDATA%\TeXeed\ai\projects`，以项目主文件完整路径的哈希隔离；`TEXEED_HOME` 设置时保存在其 `ai` 子目录。文本/截图收集本身不发送网络请求。登录、模型刷新以及提交会连接 OpenAI。

## 认证实现与限制

采用 OpenAI 公开的本地/open-source Sign in with ChatGPT 流程：系统浏览器、127.0.0.1 回调、state、PKCE S256 和 OIDC nonce；验证 OpenAI JWKS 签名、issuer、audience、时间、身份与额度授权 scope。每个安装保留稳定 host ID，各账号保留独立 issued client ID。登录依赖该账号/工作区允许第三方应用使用 ChatGPT 计划额度。

Access/refresh/ID token 与 API Key 使用 Windows DPAPI CurrentUser 加密，原子更新并通过文件锁串行化刷新。退出尝试撤销刷新令牌并清除本机令牌，保留非令牌账号/客户端映射供重新登录；网络原因未确认远端撤销时会说明，可在 ChatGPT 设置断开应用。不会读取 Codex、浏览器或其他应用的登录凭据。

HTTP Responses 使用 `store:false` / `stream:true` 和命名空间工具，客户端自行维护历史。仅收到 `response.completed` 才完成批次。API Key 为单独选择的备用模式。

协议与请求已通过隔离的假服务测试；发行前未使用用户账号做真实浏览器授权或付费推理。首次使用仍需用户点击登录完成授权。模型可用性及额度以账号实际返回为准。

官方参考：
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations

## 回归验证

`TeXeed.AppTests.exe --ai-only --output <隔离测试目录>` 覆盖身份令牌、加密存储、流式工具往返、断流失败、勾选条目批量提交、项目持久化、源代码快照、PDF 压缩带/跨页截图、AI 入口/拖动/关闭。测试不连接真实账号，也不发送用户项目。
