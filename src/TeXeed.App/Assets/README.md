# TeXeed 应用图标

应用图标为透明背景的“绿芽＋书”。

- `TeXeed.png`：1024×1024 透明母版。
- `TeXeed.ico`：16、20、24、32、40、48、64、128、256 像素的透明帧。
- `scripts/build-icon.ps1`：从 PNG 生成 ICO。

EXE 通过 `ApplicationIcon` 内嵌 ICO，WPF 窗口样式引用同一资源。进程与发布快捷方式使用应用身份 `TeXeed.Desktop`。
