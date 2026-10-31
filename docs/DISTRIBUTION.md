# 第三方发行指南 / Third-party distribution guide

本指南将 [OPEN_SOURCE.md](../OPEN_SOURCE.md) 的来源规则落实到可下载文件包。**不新增 AGPL 使用限制，不要求正常 fork 或发行先获批准。** 非官方不等于不合法；让下载者识别来源才是目标。

## 中文

### 修改、重编译或重新打包

普通文件下载采用醒目的非官方标识，例如：

```text
UNOFFICIAL-XCL2-Alice-2.2.26-fork.1-win-x64.zip
```

名称中的 `Alice` 和版本只是示例。发行者须使用自己的真实公开标识，不得填入一个无关作者的名称。以下位置应能清楚看到第三方身份：

1. **文件包或包管理器展示名称**：包含“非官方 / UNOFFICIAL / 第三方”等明显标识；合理等效形式可以替代示例。
2. **下载页或 Release 正文的显著位置**：标明独立发行者、非隶属及非背书关系、修改与日期，以及对应源码位置。
3. **压缩包顶层**：附 `DISTRIBUTION-NOTICE.txt`，使用[修改包模板](distribution/DISTRIBUTION-NOTICE.template.txt)填写；保留 `LICENSE`、`NOTICE` 和适用的第三方声明。将 `OPEN_SOURCE.md` 一并附上，使引用例外与来源条款可查。

如果你修改了程序的版本信息或“关于”页面，也应准确反映自己的发行身份。不要用完全相同的官方称谓、图标组合或签名描述制造误认；可以客观标注“基于 XCL2”。不强制为原本没有声明界面的程序新增界面，AGPL 关于交互界面通知的要求按其原文适用。

### 原样镜像

例如 `UNOFFICIAL-MIRROR-XCL2-2.2.26-win-x64.zip`。下载页面和同目录的 `MIRROR-NOTICE.txt` 标明镜像提供者，使用[镜像模板](distribution/MIRROR-NOTICE.template.txt)。保持上游压缩包字节不变，保留其原始来源与校验信息；仅改变下载文件名不改变其内容哈希。

不要说“这个软件不是原作者制作”；应说明“这个镜像渠道不是原作者维护或背书”。如果修改了压缩包内容，就应按修改／重打包处理。非官方分发不得借用原作者身份承诺技术支持、收费服务或担保。

### 对应源码与发布材料

二进制分发遵循 AGPL 第 6 条，而不是仅附一句“已开源”。对于常见的网络下载，可以在二进制旁清楚提供同一版本完整对应源码的等效免费下载入口；包括你的修改和必要构建脚本。别只链接一个与该包不对应的上游分支。

项目当前的构建需要 `XCL2_PRE_KEY` / `XCL2_REL_KEY`。实际发行时应确认所需构建材料、合法配置方式以及适用时的安装信息是否齐备，不能用“私有配置”作为不提供必要源码的理由，也不能将真实凭据直接泄露到公开仓库。如果尚无可合法提供的构建途径，先解决这一点再声称满足二进制源码交付义务。

源码 URL、提交或标签、修改日期、发行者、包校验值应与实际一致。SHA-256 有助于核对完整性，但不能单独证明官方身份；不要编造签名或上游背书。

建议打包内容：

| 文件或材料 | 用途 |
| --- | --- |
| 完整发布目录 | 包含程序及该构建所需的依赖，不能只放 EXE。 |
| `LICENSE`、`NOTICE`、`OPEN_SOURCE.md` | 基础许可、来源声明及额外许可。 |
| `THIRD_PARTY_NOTICES.md` 与依赖自己的许可 | 不用 XCL2 的许可覆盖第三方条款。 |
| `DISTRIBUTION-NOTICE.txt` | 修改包的实际发行者、身份、日期和来源。 |
| 对应源码与构建说明 | 与分发的二进制匹配，按 AGPL 的适用方式提供。 |

正常的一两条逻辑语句引用若符合小片段额外许可，只需致谢注释，不需要把以上整套发行包流程套在你的项目上。

## English

This guide applies the origin policy in [OPEN_SOURCE.md](../OPEN_SOURCE.md). It adds no restrictions to AGPL rights and requires no advance approval for a legitimate fork or distribution.

For modified, rebuilt, or repackaged releases, make the unofficial identity obvious in the package name or equivalent package-manager label, prominently on the download page, and in a top-level `DISTRIBUTION-NOTICE.txt`. A practical filename is `UNOFFICIAL-XCL2-Alice-2.2.26-fork.1-win-x64.zip`; substitute the actual distributor and version. Reasonable equally prominent labeling is acceptable. The [modified-package template](distribution/DISTRIBUTION-NOTICE.template.txt) includes publisher, non-affiliation, modification date, and matching source details.

Retain LICENSE, NOTICE, OPEN_SOURCE.md, and applicable third-party notices. Describe your own version information accurately; do not imply upstream authorship, approval, signing, support, or warranty for your modifications. “Based on XCL2” is a truthful attribution, not a claim to be the upstream release. This guide does not independently require a new notice UI where AGPL does not require one.

For an unchanged mirror, label the mirror rather than denying upstream authorship. Keep archive bytes unchanged and place the [mirror notice](distribution/MIRROR-NOTICE.template.txt) on the download page and beside the file. A descriptive download filename may change without changing the content hash. If archive contents change, treat the result as a repackaged distribution. Do not promise support or guarantees on the original maintainers' behalf.

For binaries, comply with AGPL section 6 using an applicable delivery method. For a typical network download, provide clear equivalent access to the complete matching Corresponding Source, including your changes and necessary build scripts, at no additional charge. A link to an unrelated upstream revision is insufficient. Supply Installation Information where the license requires it.

The existing build requires `XCL2_PRE_KEY` and `XCL2_REL_KEY`. Before releasing binaries, resolve and document a lawful build configuration and all required source/build materials; private configuration is not an excuse to omit Corresponding Source, and public disclosure of actual credentials is not the remedy. Do not claim compliance before a lawful source-delivery path is available.

Use real publisher details, dates, revisions, source locations, and hashes. Checksums help verify integrity; they do not establish official identity. Eligible tiny snippets need only the acknowledgment described in the additional permission, not this full packaging procedure.
