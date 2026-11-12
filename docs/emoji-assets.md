# Emoji 资源与索引

AI 回答阅读区、行内追问与源码编辑器使用离线 Noto Emoji，缺少对应图像时使用 Twemoji。原文仍保存 Unicode 字符；图像仅用于显示。普通原生标签和输入框目前仍由系统绘制。素材及归属、许可文本在 `src/TeXeed.App/Assets/Emoji/`，发布时一并携带。

`noto.png` 和 `twemoji.png` 分别取自 `emoji-datasource-google@16.0.0` 和 `emoji-datasource-twitter@16.0.0` 的 `img/.../sheets-clean/32.png`；两者按同一组坐标排布，每格边长 34 像素，外围各有一像素透明边。`index.json` 将一个完整 Unicode 字素映射为 `[横向格号, 纵向格号, 来源]`，来源 `0` 为 Noto、`1` 为 Twemoji。存在文字展示变体的非限定字符串默认仍按文字处理；emoji 默认展示的非限定别名保留。

核验当前两个原包中的共同 `emoji.json` 和两张图集：去重后 3,786 个完整序列均在两个图集中有非透明像素，因此当前 4,809 个完整序列及别名索引均优先指向 Noto，不存在真实的 Twemoji 独有项。程序和索引重建器仍保留 Twemoji 分支，模拟 Noto 缺图时分别测试了识别、图集选择和图像非透明像素。

从 `emoji-datasource` 对应版本的包中提取 `package/emoji.json` 和两个 `sheets-clean/32.png` 后，在仓库根目录核验：

```powershell
node scripts/emoji-index.cjs <emoji.json> src/TeXeed.App/Assets/Emoji/noto.png src/TeXeed.App/Assets/Emoji/twemoji.png
node scripts/emoji-index.test.cjs
```

传入第四个参数可以把根据元数据和图集中非透明像素重建的索引写入该路径，再检查差异后替换 `index.json`。脚本接受非交错、8 位 RGBA PNG 图集；升级资源时应同时检查图片尺寸、格距、元数据与许可，重新运行 WPF 和性能测试。默认核验不会修改现有索引。
