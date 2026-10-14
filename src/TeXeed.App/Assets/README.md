# TeXeed 应用图标

采用用户选定的 05「TE 合字」：暖白 T、玫瑰粉 E、右上折角与暖黑圆角底。

- `TeXeed.png`：独立透明底母版，由内置 imagegen 根据已选方案制作。
- `TeXeed.ico`：Windows 图标，包含 16、20、24、32、40、48、64、128、256 像素的 32 位透明帧。
- `scripts/build-icon.ps1`：从母版重新生成 ICO，只转换尺寸和文件格式。

生成说明：从候选板仅提取第 05 款，保持 TE 合字结构、颜色和折角；正视圆角方形、外部透明，不保留编号、文字说明或候选板。

EXE 使用 `ApplicationIcon` 内嵌 ICO，WPF 公共窗口样式引用同一资源。Windows 任务栏身份为固定的 `TeXeed.Desktop`，与发布快捷方式保持一致。
