# 参与 XCL2 / Contributing to XCL2

感谢你帮助 XCL2 变得更清楚、更稳定。问题反馈、翻译、文档和代码改进都欢迎。

## 中文

### 反馈问题

在仓库 Issues 中说明问题、期望行为、复现步骤、Windows 版本、系统缩放及使用的源码提交或包版本。UI 问题可附经过隐私处理的截图。日志请移除账号、Token、API Key、个人路径和聊天内容中不应公开的信息。

安全漏洞或暴露的凭据按 [SECURITY.md](SECURITY.md) 处理，不把利用细节或秘密放进公开 Issue。不要将一个第三方发行包的问题直接标成已确认的上游问题；先说明包来源和修改情况。

### 提交代码与文档

1. 创建自己的分支，聚焦一个明确问题。提交说明写清触发条件、修改效果和验证情况。
2. 使用 [构建指南](docs/BUILDING.md)，选择与改动有关的回归项目。无法执行 Windows 测试时，明确写出限制，不写“测试通过”。纯文档修改检查链接、图标路径和代码示例即可。
3. 保留现有版权与第三方声明，不提交凭据、私有构建值、用户数据或编译缓存。
4. 只贡献你有权提供的内容。原创贡献以项目的 AGPL-3.0-only、来源条款及小片段额外许可提供；第三方贡献必须注明来源、许可和适用范围，不能直接改成自己的版权。
5. 对适用附加条款的新增或修改原创源码，添加指向根目录 `NOTICE` 和 `OPEN_SOURCE.md` 的源码注释。不要覆盖该文件已有的真实作者声明。

请在 PR 中确认：“我有权按本项目适用条款提交这些内容；第三方来源已明确列出。”这不要求把版权转让给维护者，也不允许代表其他贡献者授予你没有的权利。授权不清楚的材料应先说明来源再处理。

### 翻译

根目录 `README.md` 为简体中文；其余文档首页位于 `docs/i18n/`。更新时同步功能边界、构建要求和许可摘要，保留仓库相对链接。新增语言时更新所有 README 顶部的语言导航。

应用界面翻译另位于 `XCL2.App/Resources/Lang/`，语言清单在 `Services/LocalizationService.cs`。README 的翻译不会自动改变应用的语言列表或实验功能可见性。许可摘要不得把“允许收费分发”翻译成“禁止商用”，也不得把非官方标识翻译成“禁止 fork”。

## English

Open an issue with the observed and expected behavior, reproduction steps, Windows version, display scaling, and the exact source revision or package version. Identify third-party builds as such. Redact credentials, private chat content, account information, and sensitive paths from logs and screenshots. Follow [SECURITY.md](SECURITY.md) for vulnerabilities.

Keep a pull request focused. Describe its trigger, resulting behavior, and relevant verification. Follow the [build guide](docs/BUILDING.md); report unavailable Windows testing honestly. Documentation-only changes need working links, image references, and accurate examples, not a full application rebuild.

Contribute only material you are entitled to provide. Original contributions use the project's AGPL-3.0-only terms, permitted origin notices, and small-snippet additional permission. Identify all third-party material and preserve its license and authorship. For added or modified original source covered by additional terms, include a source comment pointing to root NOTICE and OPEN_SOURCE.md. Do not erase existing attribution.

In the PR, confirm: “I am entitled to contribute this material under the applicable project terms; third-party sources are identified.” No copyright assignment is requested. Do not commit secrets, private build values, user data, or build caches.

The Simplified Chinese README is at the root; other README languages are in `docs/i18n/`. Keep capabilities, limitations, licensing summaries, relative links, and the language navigation consistent. Application translations live separately in `XCL2.App/Resources/Lang/`, with registration in `Services/LocalizationService.cs`. Translating documentation does not alter application language support. Preserve the distinction between non-affiliation notices and restrictions on legitimate forks or commercial distribution.
