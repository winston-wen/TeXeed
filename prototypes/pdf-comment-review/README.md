# PDF 评论原型

Windows 上双击 `start.cmd`。需要 Node.js，以及安装在 `C:\texlive\2026\bin\windows` 的 XeLaTeX 和 SyncTeX。还需要 Codex 自带的 Poppler：`%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\poppler\Library\bin` 中的 `pdftoppm.exe` 和 `pdfinfo.exe`。程序会在本机 `127.0.0.1:4179` 启动后台服务并打开浏览器；端口被占用或依赖缺失时会显示错误。

本原型使用独立示例，不读取或修改 TeXeed 的真实项目。源码和评论保存在当前浏览器的本地存储中；换浏览器或清除站点数据后不会自动带过去。编译使用本机 XeLaTeX，禁用 shell-escape。会话与项目的 PDF、SyncTeX 和页面图像按源码指纹保存到 `%TEMP%\texeed-pdf-review-prototype`；编译失败时上一个 PDF 仍可访问。评论按源码关联并在预览侧只读显示，定位来自 SyncTeX 行级映射，不能保证逐字精确。

首页是 AI 会话窗口，点击「编辑文稿与评论」打开独立编辑窗口；如果浏览器禁止弹窗，可直接在新标签页打开 `http://127.0.0.1:4179/?view=editor`。编辑窗口负责源码及评论，项目文稿的 PDF 预览也在此窗口；会话 PDF 和总附言输入留在会话窗口。两窗在同一浏览器内同步源码、评论和成功编译的预览；AI 附言只是本地草稿，原型不连接模型。

也可在终端运行 `node prototypes/pdf-comment-review/server.cjs`，然后访问 `http://127.0.0.1:4179/`；在该终端按 Ctrl+C 停止。双击启动的后台服务可访问 `http://127.0.0.1:4179/api/health` 查看其 `pid`，然后在任务管理器中结束该进程；关闭浏览器页面不会停止后台服务。并行测试可在各自终端设置 `TEXEED_PDF_PROTOTYPE_PORT` 为不同端口，并设置 `TEXEED_PDF_PROTOTYPE_WORK_ROOT` 为各自的临时目录，以免争用端口和编译缓存。
