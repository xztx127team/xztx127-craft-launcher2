# AI 文字菜单与复制回归

纯文本解析（任何安装 .NET 8 SDK 的系统）：

```sh
dotnet run --project Tests/AiMarkdownCopyRegression
```

WPF、菜单和真实剪贴板（Windows + .NET 8 SDK；会覆盖测试机器当前剪贴板内容）：

```powershell
dotnet run --project Tests/AiTextMenuRegression
```

手动验收：

1. AI 回复含普通邮件正文、链接、连续空行和代码块；分别拖选文字并右键，验证菜单颜色随主题切换。
2. 左键点击气泡复制按钮、代码复制按钮、AI 卡片的批准/拒绝按钮及链接，确认正常动作且不弹文字菜单。
3. 在代码内选中一部分，右键复制和 Ctrl+C 都应只复制该选区；“复制全部代码”和代码头部复制应保留原文缩进、空格、空行及实际换行。
4. 缩窄聊天窗口，让一行正文视觉换行，再复制到纯文本编辑器，确认不会额外插入行。
5. 点击菜单外部或按 Esc，应关闭菜单；未选中文字时“复制”禁用。

此任务的 Linux 环境不能启动 WPF；这些 Windows UI 检查需在 Windows 执行。
