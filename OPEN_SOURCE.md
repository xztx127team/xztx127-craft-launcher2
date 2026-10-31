# XCL2 开源、来源标识与小片段引用规则

**版本：1.0 · 2026-10-29**  
**基础许可：GNU Affero General Public License, version 3 only（AGPL-3.0-only）**

这份文件说明 XCL2 的许可范围、第三方分发身份，以及普通少量代码引用的额外许可。基础许可的标准原文完整保存在 [LICENSE](LICENSE)，不在原文中插入自定义限制。[NOTICE](NOTICE) 是随项目提供的来源与授权声明。

中文部分便于阅读；下方英文部分是项目自拟条款的正式文本。AGPL 本身始终以 `LICENSE` 为准。其他语言 README 是说明，不会增加义务或减少权利。

## 1. 我们开放什么

本项目有权授权的原创源代码、文档与项目素材，除另有明确说明外，采用 AGPL-3.0-only。每位贡献者保留其贡献的权利。本文件不声称拥有或重新许可第三方代码、字体、游戏素材、依赖库或服务；它们继续适用原有条款，见 [第三方说明](THIRD_PARTY_NOTICES.md)。

允许学习、运行、修改、fork 和按照 AGPL 进行免费或收费分发。无需为了行使这些权利而逐次向 XCL2 维护者申请批准。个人使用未修改程序不会因此要求公开个人文件、游戏存档、账号或与程序无关的代码。

## 2. AGPL 的核心义务

- 分发受覆盖的软件时，保留适用的版权、许可和无担保声明，并向接收者提供许可文本。
- 发布修改版时，明确说明修改及日期，按 AGPL 对受覆盖的整体授权；普通致谢不能代替这些义务。
- 分发二进制时，按 AGPL 第 6 条选择合规方式提供**与该二进制对应的完整源码**。只链接未修改的上游仓库，不能替代你自己的修改和必要构建材料。
- 如果你修改了程序，并让用户通过网络与该修改版交互，按第 13 条向这些用户显著提供免费获取对应源码的机会。桌面程序仅调用一个远程 API，不等于自动要求公开那个独立服务的全部代码。
- 无担保及责任限制以 AGPL 第 15～17 条为准，并受适用法律约束。

## 3. 明显标识第三方发行，禁止冒充来源

对携带本声明的可授权原创材料，项目依据 AGPL 第 7(b)～(e) 条规定：保留合理来源声明；不得歪曲来源；修改版须以合理、明显的方式与原版区分；不得利用原作者姓名或标识虚构宣传背书；本许可不授予商标权。

使用 XCL2 名称宣传的非官方发行，应在用户下载前和拿到包后都能被辨认：

| 场景 | 身份标识与声明 |
| --- | --- |
| 修改、重新编译或重新打包 | 显著标明“非官方 / UNOFFICIAL / 第三方发行”，写明实际发行者、修改概况和日期；提供包内顶层声明，并在下载页显著展示同等声明。 |
| 完全未修改的镜像 | 标明“非官方镜像 / UNOFFICIAL MIRROR”及镜像提供者；保留原始作者和原始包归属，在下载页及文件旁提供镜像声明。不要为了声明而谎称原始包不是上游制作。 |
| 仅正常使用小片段 | 符合第 5 节额外许可时，一条致谢注释即可，不要求给你的整个项目加上 XCL2 非官方包标识。 |

普通下载文件使用 `UNOFFICIAL-XCL2-发行者-版本.zip`、`UNOFFICIAL-MIRROR-XCL2-版本.zip` 等名称最直观。其他包管理方式可使用同样醒目的包名、发行者标签与安装前说明；不强制某个语言、符号或平台格式。见[第三方发行指南](docs/DISTRIBUTION.md)。

修改包的推荐声明：

> 本包由【发行者】独立构建、修改或重新打包，与 XCL2 原项目维护者无隶属、代理或背书关系；本第三方发行包并非原项目维护者制作或发布。原始项目及代码来源为 XCL2，版权与贡献记录予以保留。

镜像的推荐声明：

> 本下载渠道由【镜像提供者】独立维护，属于非官方镜像，与 XCL2 原项目维护者无隶属、代理或背书关系。本次镜像分发不是原项目官方发布渠道；所镜像原始包的制作及版权归属保持不变。

“无关联”指没有隶属、授权代理或背书关系，**不否认代码源自 XCL2**。已获得原项目明确授权的官方发行可以如实说明授权范围，不能虚构不存在的承诺。以上标识规则用于真实呈现来源，不作为限制 AGPL 复制、修改或分发权利的额外许可审批。具有相同效果的合理醒目标识可以替代示例格式。

## 4. 目的与边界

这些安排旨在减少冒充、钓鱼、诈骗、侵权以及把第三方改动错误归咎于原作者的情况。**正常 fork、学习、修改、镜像和商业分发本身不是“盗版”。** 不以使用领域、用户身份、是否收费或是否获得维护者好评来限制 AGPL 已授予的权利。

AGPL 的版权许可不会自动许可冒用名称制造背书；与此同时，客观介绍、致谢、兼容性说明、教程及其他依法允许的指称使用不受禁止。名称或图标未必拥有注册商标，本文不声称已经注册。

若任何自拟非宽松条款被解释为 AGPL 第 10 条禁止的进一步限制，则按第 7 条处理，不因此改写或收窄 AGPL。不存在“加一句防盗版，就能取消开源自由”的例外。

## 5. 普通一两行代码：仅致谢的额外许可

**这是权利人主动授予的额外许可，不是“一两行一定不受版权保护”的法律判断。**

对于项目权利人拥有或有权另行授权的原创代码，在以下范围内，你可以选择此额外许可：

1. 引用的是独立、普通、非实质性的小片段，在接收作品中合计不超过**两条逻辑语句**；不是完整函数、类、模块或重要算法实现。仅改变换行、压缩成一行或拆到多个文件、多个提交，不会扩大范围。
2. 在引用处或紧邻位置保留一条可读致谢注释，说明 XCL2、原文件及可用的来源链接或版本。没有注释语法的格式，可在同目录说明文件中放等价致谢。
3. 该片段不是第三方代码。额外许可不授予游戏素材、凭据、服务使用权或品牌背书。

满足范围时，允许在其他许可的项目中使用、修改、合并、分发及商业使用该片段，**不因这个片段单独要求接收项目改用 AGPL，也不要求附整套 XCL2 分发声明或先申请批准**。只需上述致谢。示例中的占位内容应替换为真实来源：

```csharp
// Thanks to XCL2 contributors; source: <repository URL>/<file>@<commit>; XCL2 small-snippet permission.
```

超过范围时，适用正常 AGPL 或另行取得的许可。构思、通用知识、依法不受保护的表达，以及适用法律已经允许的使用，不因本文件增加限制；此时致谢是鼓励而非新增的版权条件。

本项是 AGPL 第 7 条意义上的额外许可；其下游保留或移除遵循该条。移除例外不会取消 AGPL 本身，也不会收回接收者已经依原权利人有效取得的许可。

## 6. 对贡献者和发行者

请仅提交你有权按本项目条款提供的内容；他人材料必须附原始来源及条款。涉及附加条款的新增或修改源码，应包含指向本文件和 `NOTICE` 的注释，例如：

```csharp
// Original XCL2 material: AGPL-3.0-only.
// Origin notices and additional permission: see NOTICE and OPEN_SOURCE.md at the repository root.
```

具体贡献流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。本源码当前仍要求外部构建配置；二进制发行者须解决并说明对应源码的合法可构建路径，不能用“私有配置”替代 AGPL 所需材料。任何不属于本项目授权范围的内容都不能靠修改 README 获得许可。

---

## Operative English terms

### A. Scope and base license

“Original Project Material” means original XCL2 code, documentation, and project assets that the relevant licensor owns or is entitled to license under these terms, supplied with the project NOTICE or a reference to it. Separately identified third-party material is excluded and retains its own license. Existing copyright and attribution notices are preserved; contributors retain their respective rights.

Except where otherwise expressly stated, Original Project Material is licensed under the GNU Affero General Public License, version 3 only (“AGPL”), as reproduced without alteration in LICENSE. NOTICE and these terms accompany that grant. The AGPL text controls the base license. The English terms in this section control the project-authored additions; translated explanations do not narrow AGPL rights.

### B. Limited origin and attribution terms under AGPL section 7

To the extent the licensor is entitled to impose them, and only within the scope permitted by AGPL section 7(b)-(e):

1. Preserve the reasonable copyright, attribution, license, and origin notices in NOTICE in copies of the covered material, or in the Appropriate Legal Notices of works containing that material as applicable. Equivalent accessible presentation is acceptable; this does not demand preservation of every promotional sentence or unrelated document.
2. Do not misrepresent the origin of the material. Mark modified versions prominently and reasonably as different from the original, identify the actual modifier or distributor, and state the relevant modification date. Do not imply that an independent build, modification, or repackaging was prepared, published, or endorsed by the original XCL2 maintainers.
3. No permission is granted to use a licensor's or author's name for publicity that falsely suggests affiliation or endorsement. No trademark rights in the XCL2 name or project marks are granted by the copyright license. Truthful attribution and legally permitted descriptive use are not prohibited.

A modified archive should carry a top-level distribution notice and clear identification at the point of download. An UNOFFICIAL filename is a straightforward way to make its identity visible, but an equally prominent, reasonable indication is acceptable. These provisions are not a requirement to obtain prior permission to exercise AGPL rights.

### C. Brand and distribution transparency policy

Every non-official distribution presented under the XCL2 name must clearly disclose that identity to its audience. For ordinary downloadable packages, use an obvious UNOFFICIAL or equivalent package label, an identified distributor, and a prominent non-affiliation statement. This is an origin and brand-use policy, not an additional restriction on copyright permissions granted by AGPL. Reasonable equivalent labeling is accepted; the specific filename, language, platform, or template is not mandatory.

For a modified or rebuilt package, a suitable statement is:

> This unofficial package is independently built, modified, or repackaged by [distributor]. The distributor is not affiliated with, acting as an agent for, or endorsed by the original XCL2 maintainers. This third-party package was not produced or published by those maintainers. Its upstream code originates from XCL2; original authorship and contribution notices are retained.

For an unchanged mirror, preserve the original package's authorship and distinguish the mirror operation:

> This is an unofficial mirror independently operated by [operator], not affiliated with, acting as an agent for, or endorsed by the original XCL2 maintainers. This mirror distribution is not an official upstream publishing channel. Authorship and rights in the unchanged upstream package remain with their original holders.

Do not falsely deny actual upstream authorship. Accurately documented official authorization may be described within its actual scope. “Non-affiliation” does not mean that the software has no source-code relationship to XCL2. These notices do not shift responsibility imposed by applicable law or make promises on behalf of other people.

### D. Purpose; no further restrictions

The purpose is to reduce impersonation, phishing, fraud, infringement, and confusion about responsibility. Legitimate copying, forks, modifications, mirrors, learning, and commercial distribution permitted by AGPL are not prohibited or characterized as piracy merely because they are independent.

No field-of-use restriction, noncommercial requirement, approval requirement, or additional royalty is imposed on AGPL rights. If any non-permissive project term would constitute a further restriction prohibited by AGPL section 10, it is subject to section 7 and must not be used to narrow those rights. Nothing here restricts statutory exceptions, unprotected ideas, or other uses that do not require the licensor's copyright permission.

### E. Small-snippet additional permission

The relevant licensors additionally permit an Eligible Snippet to be used, copied, modified, combined, distributed, sublicensed, and used commercially under terms of the recipient's choice, subject only to the acknowledgment requirement below. The receiving work need not adopt AGPL solely because of that snippet, and the full XCL2 distribution notices need not accompany it under this permission.

An “Eligible Snippet” is an ordinary, independently useful, non-substantial extract of Original Project Material consisting, in aggregate within the receiving work, of no more than two logical statements. It must not embody a complete function, class, module, or substantial algorithmic implementation. Formatting, minification, splitting across files or commits, or combining multiple extracts does not enlarge this scope. Third-party material is not covered.

Keep one readable acknowledgment comment at or near the extract identifying XCL2, its original file, and the available source link or revision. Where the format cannot carry comments, an accompanying notice in the same directory is sufficient. This is the sole condition of the additional snippet permission; it does not waive obligations owed to third parties or grant credentials, service access, or trademark endorsement.

Larger or substantial reuse remains subject to AGPL or separately obtained permission. This grant is an additional permission under AGPL section 7, including its rules on downstream removal. Removal does not revoke permissions already validly received from the relevant licensor or remove the underlying AGPL grant. Ordinary statutory rights remain unaffected; acknowledgment is encouraged rather than imposed where no copyright permission is needed.

### F. Contributions, warranty, and practical guidance

Contribute only material you are entitled to supply under the applicable terms. Add an appropriate source-file notice or pointer to NOTICE and this document when adding or modifying material covered by the section 7 additions. See CONTRIBUTING.md. Nothing here transfers ownership of existing third-party works or makes an independent service part of XCL2 merely because an API is called.

The AGPL warranty and liability provisions remain applicable. Package-identification examples and checklists in docs/DISTRIBUTION.md help apply these terms; they do not create additional copyright restrictions. Comply with AGPL's actual Corresponding Source and, where applicable, Installation Information requirements for the form of distribution you choose.

## 参考依据 / References

- [GNU AGPL v3，尤其第 4～7、10、13、15～17 条](https://www.gnu.org/licenses/agpl-3.0.html)
- [OSI 的 AGPL v3 文本](https://opensource.org/license/agpl-3-0)
- [SPDX 标识 AGPL-3.0-only](https://spdx.org/licenses/AGPL-3.0-only.html)
- [GitHub：仓库许可识别与标准 LICENSE 文件](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)

这些条款表达项目的授权安排与来源政策，不承诺能够阻止所有侵权或保证在所有司法辖区得到同样解释。涉及现有第三方材料或具体争议时，应依据实际权利和适用法律处理。
