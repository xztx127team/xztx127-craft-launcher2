# 本次 AI 与触屏修复说明

基于本次上传的 `XCL2-updated-source-v22_5.zip` 修改，保留原应用版本号及其它功能。交付完整源码，未生成 Windows 可执行文件。

## 1. 规划改为建议

- Agent 不再因为本轮未保存规划 JSON / 文本计划、旧计划尚未更新、没有正在进行的步骤或当前是非任务对话而拦截普通回答和已授权工具。
- 简单需求可以直接回答、查询或执行。复杂、多步骤和需要断点续做的任务仍建议保存计划。
- 移除仅因模型在正文承诺规划而反复强制补交计划的自动恢复流程。
- 自愿保存计划时保留 JSON 和 `<<XCL2_PLAN>>` 两种方式，以及格式、步骤 ID、证据、完整替换与移除校验。首次实际提交的 Agent 计划如果全为 pending，仍自动开始第一步。
- 只更新 notes / next_action 不再强制补齐初始步骤；无效的计划参数在扣除工具操作预算之前拒绝。
- AI 提示词、模式说明、设置说明及空计划面板文案同步更新。

## 2. 新建对话时保护原后台会话

定位到的主要原因是旧会话虽有独立取消令牌，仍读取共享工作模式；新对话切换为 Chat / Plan 会使旧任务被降级，并丢失相应工具能力。

- 每个运行会话独立维护工作模式、有效执行模式、取消令牌和提问卡片。
- 请求级模式作用域覆盖静态目录、文件、命令和 Shell 提示词判断；实际权限授予及撤销仍实时生效。
- 停止按钮直接定位当前会话，不再使用共享取消令牌别名。
- 新建会话选项改为明确的“返回当前会话 / 停止后新建 / 保留后台并新建”，默认保留后台。
- 用户显式收紧原会话的模式仍立即生效；命令自身超时、原会话显式停止及退出启动器的行为保留。

## 3. 未预先放行 Shell 时申请审批

旧逻辑在进入确认回调前就按总开关和类别权限直接返回失败。现在先检查不可覆盖的边界，再对未预授权的具体命令申请一次审批。

- 审批框显示实际命令、工作目录、超时和转换前命令（如有 Windows 命令转换）。
- 批准只产生本次命令的一次执行许可，不开启全局 Shell 或修改类别权限。
- 许可绑定固定命令和目录；执行前复检取消、模式、权限快照与目录状态，并且只能消费一次。
- 用户拒绝、取消、精确命令禁止、关闭的电脑控制能力、无有效工作目录和 Chat / Plan 边界仍会阻止执行。

## 4. 后台触屏选择通知

- 只在“每次启动游戏前询问操作模式”这一提示中启用系统通知。
- 启动器最小化、隐藏到托盘或不在前台时，Windows 通知中心显示“键鼠模式 / 触屏模式”按钮。
- 通知正文恢复启动器并暂停倒计时；按钮只影响本次启动，不保存“不再询问”。
- 沿用原弹窗倒计时；选择、超时、取消、关闭后清理通知。每次启动独立令牌，旧通知和关闭动画期间的延迟点击不会控制新请求。
- 通知被禁用、注册或发送失败时恢复原弹窗并暂停倒计时。
- 新增 `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 依赖，还原 NuGet 后构建。

## 验证结果与边界

- 已通过：修改和新增 C# 文件的 Tree-sitter 语法解析；修改的 XAML / 项目文件 XML 解析；修改项目的显式源码引用存在性检查；交付压缩包 CRC 与内容完整性检查。
- 已补充行为回归：可选规划、旧计划续作、工具预算、会话并发与模式隔离、Shell 单次审批与撤销，以及通知动作令牌隔离。
- 未运行通过的项目：C# 回归与完整 Windows 编译。本环境安装了经过官方 SHA-512 校验的 .NET SDK 8.0.426，但运行时初始化即返回 `Failed to create CoreCLR, HRESULT: 0x8007000E`（退出码 137），尚未进入编译或测试程序。这些项目不能视为已编译或测试通过。
- Windows 实机仍需核对：WPF 会话切换和命令 PID 保留；Shell 审批后的实际执行；通知中心桌面注册及按钮回调、管理员权限、勿扰/禁用通知、托盘与超时取消的交互。专项清单见各测试目录 README 和 `BackgroundSessionRegression.md`。

在 Windows 安装 .NET 8 SDK 后，从 `XCL2.App` 目录运行：

```powershell
dotnet run --project Tests/AiPlanRegression/AiPlanRegression.csproj
dotnet run --project Tests/AiHttpRegression/AiHttpRegression.csproj
dotnet run --project Tests/AiPermissionsRegression/AiPermissionsRegression.csproj
dotnet run --project Tests/AiCommandRegression/AiCommandRegression.csproj
dotnet run --project Tests/InputModeNotificationRegression/InputModeNotificationRegression.csproj
```

完整应用构建仍沿用原项目要求的 Windows 环境与 `XCL2_PRE_KEY` / `XCL2_REL_KEY` 配置。
