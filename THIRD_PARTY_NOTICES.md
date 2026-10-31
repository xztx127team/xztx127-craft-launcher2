# 第三方组件与素材 / Third-party components and assets

XCL2 的 AGPL 授权只覆盖项目有权授权的材料，不改变第三方许可证、商标或服务条件。本文件是依据当前源码入口整理的索引，**不是所有传递依赖与素材来源的完整审计**。发布者仍需针对实际构建版本保留各组件要求的许可及声明。

## 源码中的直接依赖

以下版本来自 [XCL2.App.csproj](XCL2.App/XCL2.App.csproj)。最终条件应以实际还原的 NuGet 包内许可文件、`.nuspec` 许可字段及其上游声明为准；本表不猜测或替代那些许可。

| 包 | 源码声明版本 |
| --- | --- |
| `System.Text.Json` | `8.0.5` |
| `Microsoft.Data.Sqlite` | `8.0.5` |
| `Microsoft.Web.WebView2` | `1.0.2792.45` |
| `Microsoft.Win32.Registry` | `5.0.0` |
| `Microsoft.Toolkit.Uwp.Notifications` | `7.1.3` |

[WinUI 预览子工程](XCL2.App/WinUI.Preview/XCL2.WinUI.Preview.csproj) 有自己的依赖。还原与发布时，还要核对 .NET、Windows App SDK、WebView2、本机 SQLite 等实际进入发行包的组件及其传递依赖，不能只凭本表判定完成许可交付。

## Terracotta / 陶瓦联机

- 原始版权归 burningtnt 与 Terracotta contributors。
- 当前随附 [NOTICE](XCL2.App/Resources/Terracotta/NOTICE.txt) 指向 v0.4.2，并说明上游采用 AGPL v3 或更新版本及其二进制分发／IPC 例外。
- 保留 [LICENSE.txt](XCL2.App/Resources/Terracotta/LICENSE.txt) 和原 NOTICE；若依赖上游例外，应从该版本上游取得并保留例外的确切适用文本，不能把 XCL2 的小片段额外许可套到 Terracotta 上。
- 本源码附件不包含 Terracotta 可执行文件。随附说明记录该上游版本提供 Windows x86_64、ARM64，而没有 Windows x86 发行包；自行附带本体时需另行核对来源、架构和条款。

## 游戏、资源与名称

Minecraft、Mojang、Microsoft、Mini World／迷你世界及其他产品名称属于各自权利人的识别标识；此处仅用于客观描述兼容或相关功能。XCL2 不据此获得背书或转授游戏授权。

既有 `Resources/Skins/`、`Resources/Avatars/`、图标、字体和其他美术素材的来源与再分发权利应由发行者逐项核实；本次新增根目录许可不自动重新许可这些材料。用户后来下载的模组、整合包、地图、资源包和插件也遵循其自身条款。

新 README 图标位于 [docs/assets/xcl2-mark.png](docs/assets/xcl2-mark.png)，生成方式及提示词记录在[素材说明](docs/assets/README.md)。它是本次文档专用的新素材，未替换应用原有 ICO；对可授权权利的授予和品牌使用边界见 `OPEN_SOURCE.md`。

## English

XCL2's license covers only material its licensors are entitled to license. Third-party copyrights, trademarks, component licenses, and service terms remain unchanged. This is an index of known source-tree entry points, not a complete audit of transitive dependencies or asset provenance.

The table lists direct package versions declared in the main project. Determine actual terms from restored package license files, `.nuspec` metadata, and upstream notices. The WinUI companion project has additional dependencies. Preserve the notices required by components actually distributed, including applicable runtime and native components.

Terracotta retains its own copyright and upstream AGPL-3.0-or-later terms and any applicable upstream exception. Keep the existing Terracotta LICENSE.txt and NOTICE.txt. Obtain the exact upstream exception text if relying on it; XCL2's additional snippet permission does not apply to Terracotta. The supplied source contains no Terracotta executable and records the upstream v0.4.2 Windows x86 limitation.

Game names and existing game-related imagery do not become project-owned or automatically relicensed by adding a root LICENSE. Verify provenance and redistribution rights for existing skins, avatars, fonts, icons, and other assets. User-downloaded content and plugins retain their own terms. The new README image has a separate provenance note linked above; no registration or exclusive trademark ownership is asserted.
