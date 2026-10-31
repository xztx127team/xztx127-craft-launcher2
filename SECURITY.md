# 安全反馈 / Security reporting

## 中文

涉及凭据泄露、未授权访问、恶意下载、命令执行边界或可能影响其他用户的问题，请优先使用仓库维护者明确公布的私密反馈渠道。如果尚无可用私密渠道，可以先提交不含漏洞细节的联系请求，待维护者提供渠道后再发送详细材料；本文不虚构邮箱、响应时限或安全认证。

反馈应包含受影响版本或提交、复现条件、预期与实际行为，以及经过脱敏的最小证据。不要发送真实账号密码、API Key、访问 Token、私人聊天或受害者个人资料。已经泄露的凭据应在对应服务中撤销或轮换，删除公开文字不能使旧凭据自动失效。

发现冒充 XCL2 的分发页面时，请保留页面地址、发行者、文件哈希和公开可见的冒充证据，向维护者和相关平台的举报渠道反馈。不要为了取证运行不明程序。第三方发行者应使用清楚的[非官方分发声明](docs/DISTRIBUTION.md)；声明本身不等于安全审计或真实性认证。

当前源码交付未完成 Windows 实机验证；不将“开源”或“有校验值”等同于无漏洞。不要承诺未实际核验的受支持版本或安全修复状态。

## English

For exposed credentials, unauthorized access, malicious downloads, command-execution boundaries, or issues affecting other users, use a private channel explicitly published by the repository maintainers. If none is available, first request a private contact without publishing exploit details. This document invents no email address, response deadline, or security certification.

Include the affected revision or version, reproduction conditions, expected and actual behavior, and minimal redacted evidence. Do not send real passwords, API keys, tokens, private chats, or personal information about affected people. Revoke or rotate exposed credentials with the relevant provider; deleting a public message does not invalidate a credential.

For impersonating download pages, retain the URL, publisher, file hash, and publicly visible evidence and report through the maintainer's and platform's available channels. Do not execute unknown packages to gather evidence. A non-affiliation notice or checksum is not a security audit or proof of authenticity.

The current source delivery has not completed Windows runtime verification. No unverified security-support matrix or patch status is claimed.
