# AI 工作区

## 连接与会话

从 AI 菜单（Alt+A）新建对话、打开设置或管理历史。每个会话的源码、PDF 和评论标签可关闭、跨栏拖动，问题、草稿、评论和权限按会话保存。关闭最后一个会话标签前会停止并保存该会话；切换标签时其他会话继续输出。

会话 PDF 使用现有 PDF 预览，不能按标题折叠正文。关闭会话 PDF 标签只关闭该视图，不删除会话、文稿或评论；再次打开会话可恢复标签。

在 AI 设置中通过系统浏览器登录 ChatGPT，可添加和切换账号或工作区。模型及推理强度可在设置页和输入栏选择，支持选项来自账号模型目录。API Key 使用独立的 API 额度，需要手动启用。

附言编辑框中 Enter 与 Shift+Enter 换行，Ctrl+Enter 或发送按钮提交附言、当前共同文稿和评论。发送后可继续写下一条草稿；停止会取消当前请求，已完成的文件修改保留。

发送后，左侧显示本次附言和处理状态；请求保存后清空已发送的草稿。等待、生成和重试倒计时在状态栏显示，模型修改写入共同文稿，意见写入关联评论。

网络连接中断、TLS 连接失败、请求超时和服务过载会持续重试。等待按 1、2、4、8、16、30 秒递增，之后保持 30 秒，停止按钮随时取消等待。流读取连续两分钟没有收到新行时重新连接。已执行的工具结果保留在当前工作循环中，重连只重新请求尚未完成的响应。

只有最后一次请求中止时显示「重试上次请求」；用户中止、程序中止和旧的过载失败记录均适用。有原请求快照时，重试使用原附言、文稿与评论快照，并携带已完成操作记录；下一条草稿和评论输入保留。旧记录没有快照时会说明原因，用户可以检查原问题后选择用当前文稿和评论重新发送，已有草稿仍保留。登录、权限、参数等永久失败保留中止状态，排除原因后可手动重试。

历史管理使用分页工作区，支持标题、问题和回答搜索、多选删除、改名及页码跳转。已打开会话需先关闭再删除。会话标签双击打开改名窗口。共同文稿与评论见 [AI 共同文稿与评论](ai-annotations.md)。

## 文稿搜索

Ctrl+G 搜索当前会话共同文稿；增强搜索使用可用的小型模型辅助。旧用户书签数据保留。

## 项目工具与权限

AI 可以列出目录、读取和检索源码、替换或创建文件、保存及编译。开始时提供当前入口、引擎和目录资料；后续按需读取未引用的模板、样式与构建说明。

替换前读取文件并校验指纹和唯一匹配，保留读取之后发生的新编辑。首次替换前建立备份，编辑进入撤销栈。专用源码工具限定项目内路径并拒绝符号链接；`compile_project` 可使用当前入口或项目内已有 `.tex` 入口。

| 会话权限 | 行为 |
| --- | --- |
| 完全访问 | 使用当前 Windows 用户权限执行 PowerShell、文件操作和网络请求 |
| 执行前询问 | 源码编辑、保存和编译自动执行；Shell 等待批准或匹配会话已有授权 |
| 只读 | 提供读取工具，拒绝修改和命令执行 |

Shell 默认在项目根目录启动独立非交互进程，可访问当前 Windows 用户权限允许的路径。工作规则集中在 [AiAgentPrompt.cs](../src/TeXeed.App/AI/AiAgentPrompt.cs)，会话权限在请求时附加。

## 上下文与保存

发送时固定当前会话的文稿及评论快照，源码工具读取包含未保存修改的编辑内容。共同文稿不会为适应模型窗口自动截断或摘要。上下文百分比采用最近一次请求的服务端用量。

配置默认位于 `%LOCALAPPDATA%\TeXeed`，设置 `TEXEED_HOME` 后使用指定目录：

| 子目录 / 文件 | 内容 |
| --- | --- |
| `settings.json` | 应用设置及最近项目 |
| `ai/projects` | 按项目主文件路径隔离的会话 |
| `ai/backups` | AI 替换前的内容及原路径记录 |
| `ai` 下的凭据文件 | Windows DPAPI CurrentUser 加密的令牌和 API Key |

会话采用有序写入及独立恢复日志。草稿收集在本地完成；登录、模型刷新、发送及启用增强搜索会连接服务。

会话文稿、资源及版本历史按项目主文件路径隔离。项目存在会话文稿、资源或已打开的会话视图时，暂不支持通过文件树重命名主文件，以免失去原路径对应的数据。

源码编辑器按作者区间区分用户与 AI：用户文字使用随程序提供的文楷字体，字体不可用时通过编辑器字体的斜体或正常样式区分；固定字宽网格不变。一个字素跨越作者边界时保持完整，不拆分绘制。PDF 目前仍按文稿自身的 TeX 样式排版，不能自动区分用户和 AI 字体。

## 认证与渲染实现

登录使用系统浏览器和本机回调，校验 state、PKCE S256、OIDC nonce、签名、身份和授权范围。凭据刷新通过文件锁串行化，保存采用原子替换。退出清理本机令牌并请求撤销刷新令牌，远程撤销失败会显示结果。

Responses 请求设置 `store:false` 与 `stream:true`，客户端维护请求快照。接收与 JSON 处理在后台完成，模型初稿和后续改动写入会话 TeX 源码及关联评论，用户在源码编辑器与 PDF 预览中查看。

## 验证入口

```powershell
dotnet run --project tests/TeXeed.AppTests -c Release --no-build -- --ai-only --output artifacts/ai-check
dotnet run --project tests/TeXeed.AppTests -c Release --no-build -- --ai-history-only --output artifacts/ai-history
dotnet run --project tests/TeXeed.AppTests -c Release --no-build -- --ai-permissions-only --output artifacts/ai-permissions
dotnet run --project tests/TeXeed.AppTests -c Release --no-build -- --ai-reliability-only --output artifacts/ai-reliability
dotnet run --project tests/TeXeed.AppTests -c Release --no-build -- --ai-retry-only --output artifacts/ai-retry
```

这些回归使用隔离配置和模拟服务，覆盖流式接收、工具、取消、权限、存储和恢复。文稿与评论回归见 [AI 共同文稿与评论](ai-annotations.md)，性能复测见 [UI 性能](ui-layout-and-performance.md)。
