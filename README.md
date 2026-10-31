<p align="center">
  <img src="docs/assets/xcl2-mark.png" alt="XCL2：蓝青色 X 与启动箭头组成的项目图标" width="128" height="128">
</p>

<h1 align="center">XCL2</h1>
<p align="center"><strong>让游戏启动、实例管理与 AI 协作，回到一个清晰的桌面。</strong></p>
<p align="center">Windows · .NET 8 · WPF · AGPL-3.0</p>

<p align="center">
  <strong>简体中文</strong> ·
  <a href="docs/i18n/README.zh-Hant.md">繁體中文</a> ·
  <a href="docs/i18n/README.en.md">English</a> ·
  <a href="docs/i18n/README.ru.md">Русский</a> ·
  <a href="docs/i18n/README.ja.md">日本語</a>
</p>

---

XCL2 是一个面向 Windows 的独立游戏启动器，以 Minecraft 实例管理为中心，将版本下载、账户、模组与整合包、联机工具、性能信息和 AI 助手放在同一个应用里。你可以保留熟悉的经典布局，也可以开启新界面预览，调整主题、动画、快捷键与触屏操作。

**这是源码仓库。** GitHub 的 “Download ZIP” 下载的是源码，不是可直接运行的安装包。只有维护者在 Releases 中实际提供了构建产物时，才存在对应的程序下载；本 README 不预设尚未发布的版本或下载地址。

## 可以做什么

| 方向 | 已有能力 |
| --- | --- |
| 实例与账户 | 管理游戏实例、启动参数、Java 环境及账户；将不同游戏配置分开维护。 |
| 内容管理 | 下载版本，管理模组、整合包、资源包、光影和世界；查看任务队列与下载状态。 |
| AI 助手 | 对话、附件、历史记录和模型选择；通过不同工作模式辅助调查、规划与操作，展示执行过程。 |
| 联机与服务端 | 服务端管理、连接诊断和 Terracotta 接入；具体能力取决于服务和本机依赖。 |
| 桌面体验 | 深浅主题、多语言、可调动画、触屏按键、快捷键、性能悬浮窗与置顶设置。 |
| 实验入口 | 包括迷你世界相关入口和 CDK / 直链下载；需完成阅读确认，并使用发布者支持的有效来源。 |

AI 的“复制”和“重新生成”使用常驻透明按钮；执行过程支持展开与收起动画。新界面预览在现有功能基础上调整导航和卡片样式。界面语言与文档语言独立：部分实验功能目前仅在简体中文界面显示。

## 开始使用

1. 若维护者已发布程序，从你确认属于原项目的 Releases 获取完整发布包，核对发行者、版本说明和校验值。
2. 解压完整目录后运行 `XCL2.exe`。不要只复制 EXE；WebView2 等依赖文件也需要保留。
3. 按需要配置账户、游戏目录、实例和 Java，然后选择实例启动。
4. AI 功能需要自行配置支持的服务、模型与凭据；网络服务的可用性、价格和数据处理规则由对应提供方决定。

XCL2 不随源码提供游戏授权、付费资源使用权或通用 CDK。它与 Mojang、Microsoft 及其他游戏权利人没有官方隶属或背书关系。项目开源不改变游戏、服务或第三方素材的使用条件。

## 从源码构建

| 项目 | 要求与边界 |
| --- | --- |
| 平台 | Windows 桌面；WPF 不提供原生 Linux / macOS 界面。 |
| SDK | .NET 8 SDK。主工程目标为 `net8.0-windows10.0.19041.0`，最低系统声明为 Windows 10 1809；具体模块可能有额外要求。 |
| 架构 | 工程包含 x86、x64、ARM64 配置；并不代表每个外部组件都提供这三种架构。 |
| 现有构建配置 | `XCL2_PRE_KEY` 与 `XCL2_REL_KEY` 各需 96 位十六进制值。它们属于原有基岩版模块，本仓库不附带其值。 |

在 Windows 上准备合法可用的构建配置后，于仓库根目录执行：

```powershell
dotnet restore .\XCL2.App\XCL2.App.csproj
dotnet build .\XCL2.App\XCL2.App.csproj -c Release -p:SkipNativePreview=true
```

`SkipNativePreview=true` 跳过单独的 WinUI 预览程序构建，便于先编译 WPF 主程序；不会移除 WPF 内的新界面预览样式。完整构建、发布与已知限制见 [构建指南](docs/BUILDING.md)。当前源码仍存在上述外部构建配置要求，不能将“公开源码”理解为已完成无额外配置的可复现构建。

相关回归检查：

```powershell
powershell -ExecutionPolicy Bypass -File .\XCL2.App\Tests\Test-AiPresentation.ps1
powershell -ExecutionPolicy Bypass -File .\XCL2.App\Tests\Test-October29Changes.ps1 -IncludeWindowsUi
```

这份源码包此前做过静态检查，但尚未在交付环境中完成 Windows 编译与实机验证；不提供虚构的构建通过徽章。

## 文档导航

| 文档 | 内容 |
| --- | --- |
| [开源、分发与少量引用](OPEN_SOURCE.md) | AGPL 核心条款说明、来源标识、品牌边界和小片段额外许可。 |
| [第三方发行指南](docs/DISTRIBUTION.md) | 文件包标识、声明模板与对应源码交付。 |
| [贡献指南](CONTRIBUTING.md) | 问题反馈、翻译、代码贡献与授权要求。 |
| [安全反馈](SECURITY.md) | 漏洞、凭据泄露及冒充分发的反馈方式。 |
| [第三方说明](THIRD_PARTY_NOTICES.md) | 依赖、Terracotta 及资源的许可边界。 |
| [CDK 下载协议](docs/CDK-DOWNLOAD.md) | 发布者提供的直链与兑换接口约定。 |
| [API 开放文件](XCL2.App/Resources/Docs/API_REFERENCE.md) | 插件 API 完整参考（唯一一份，同时内置于启动器）。 |
| [API 完整样例](XCL2.App/Resources/Docs/API_EXAMPLES.md) | 完整插件样例与常见错误。 |
| [启动器控制 API](XCL2.App/Resources/Docs/API_LAUNCHER_CONTROL.md) | 页面导航、窗口布局、启动控制与设置写入。 |
| [组合启动器界面](XCL2.App/Resources/Docs/API_LAUNCHER_COMPOSITION.md) | 用组件 API 组合自己的启动器界面。 |
| [存档读写 API](XCL2.App/Resources/Docs/API_WORLDS.md) | 世界存档查询与读写。 |
| [EXE 插件协议](XCL2.App/Resources/Docs/PLUGIN_EXE_PROTOCOL.md) | 独立进程插件的通信协议。 |
| [插件开发指南](XCL2.App/Resources/Docs/PLUGIN_GUIDE.md) | 插件能力与接入说明。 |

## 开源与第三方分发

项目可授权的原创部分采用 **GNU AGPL 第 3 版，仅该版本（`AGPL-3.0-only`）**，并附 [来源声明与额外许可](OPEN_SOURCE.md)。[LICENSE](LICENSE) 保留标准英文全文，第三方组件继续遵循各自条款。

**第三方发布必须显著表明“非官方 / UNOFFICIAL”，说明发行者及其修改或分发并非原项目维护者制作、发布或背书。** 修改包应附包内声明；原样镜像须如实保留原作者归属，并在分发页面与下载文件旁标明镜像身份。可采用 `UNOFFICIAL-XCL2-发行者-版本.zip` 等明确名称；不要求把原始代码错误地说成与 XCL2 无关。具体适用范围与等效标识方式见发行指南。

这些规则用于减少冒充、钓鱼、诈骗和侵权，不把正常 fork、修改、收费分发或学习使用称为“盗版”。**普通的一两行非实质性原创代码引用，可以按小片段额外许可，仅附一条致谢注释使用**；完整功能、批量拆分复制与第三方代码不适用该例外。

欢迎改进，也请让使用者清楚知道：代码从哪里来，当前这个包由谁负责。
