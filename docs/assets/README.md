# README 图标 / README artwork

- 文件：`xcl2-mark.png`。
- 设计：深蓝色圆角底板，蓝色与青色的 X 形轨迹，中间用负形形成向右的启动箭头。轮廓优先考虑小尺寸识别，与 Windows 桌面启动器的视觉方向一致。
- 用途：五个语言版本的 README 顶部以 128 × 128 展示。保留 PNG 透明通道，不依赖外部图床、脚本、CSS 或 GitHub 账号名称。
- 生成方式：内置 `image_gen` 工具，`logo-brand`，新图生成，透明背景；原始输出直接复制到仓库，没有改用 API / CLI，也没有替换 `XCL2.App/Resources/app.ico`。
- 许可和标识：可授权权利与品牌使用适用根目录 `OPEN_SOURCE.md`；生成素材的提供不构成商标注册或独占性保证。

## Generation prompt

```text
Use case: logo-brand. Asset type: a GitHub README brand icon for XCL2, an independent Windows game launcher with an integrated AI assistant. Create ONE polished square app emblem, large centered, on a truly transparent background. The emblem itself is a dark midnight-blue rounded square with a very subtle satin finish. Inside it is a bold geometric X formed by two interlocking angular launch trails: one vivid electric blue, one cool turquoise, with a crisp small forward-pointing triangle cut from the negative space at the center. Use a restrained Fluent-inspired modern software aesthetic, vector-like geometry, clean edges, strong readable silhouette even at 48 pixels, balanced proportions, subtle depth but mostly flat. Square canvas, emblem occupies roughly 86% of canvas. No lettering, no numbers, no words, no tiny details, no Minecraft grass cube, no Creeper face, no official game logos, no watermark, no mockup, no surrounding props, no background checkerboard.
```

The new PNG is the README-only project mark. It is stored within the repository so all language variants work after cloning or uploading to GitHub. The application ICO is unchanged. The image was generated using the built-in image tool, with no reference image and with a transparent background. The prompt above records the design specification.

