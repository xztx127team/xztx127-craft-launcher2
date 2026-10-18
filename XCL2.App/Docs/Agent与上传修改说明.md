# Agent、权限、Plan 与附件更新

## 已实现

- 弹窗先完成首帧布局，再播放淡入；不再等待空闲队列。插件扩展工厂和打开通知延后到弹窗显示后，避免首帧被插件同步调用阻塞。关闭、重开及多层弹窗继续保留原有结果回调。
- AI 工作模式新增 Agent。需要执行工具的任务，每轮先保存计划，再开始步骤；普通问答不强制生成计划。Build 保留直接执行流程，Plan 保持只读。
- 右侧 Plan 显示真实任务记录：○ 正在进行、✓ 已完成、– 未开始，并展示步骤证据和下一步。任务进度可以继续保存、恢复；模型通过 update_task 更新状态。
- AI 设置增加 Agent 动画预设：保持当前、低功耗（各类强度 20%）、高性能（各类强度 100%）。在 Agent 下选择预设会调整真实动画配置与设置页滑杆；用户已经关闭的分类和动画总开关继续有效。
- Shell 设置提供 Post、PowerShell、Shell、CMD、Python 大类，以及读取、新建、修改、删除、Python 执行、Shell、其他、窗口、鼠标与键盘小类，还支持完整命令的逐条允许/禁止规则。
- “全选”打开全部类别、操作和当前精确命令规则，并启用默许全部 Shell。之后仍可单独禁止某条命令。未识别命令和复合脚本归入“其他 / Shell 命令”，不按模型自报类别放行。
- 鼠标移动、点击、右键、键盘及窗口操作默认关闭；拒绝时提示前往 AI 设置开启相应权限。实际执行前会重新检查当前模式、目录和命令权限。
- 工作区内首次新建 `.py` 默认预授权；覆盖现有文件仍保留差异审阅。默认免确认的 Python 执行限数字计算、变量赋值和 print 等受限程序；import、动态代码和其它脚本需额外授权。脚本使用校验后的源文本快照运行，避免校验后换文件。
- 支持 PNG、JPEG、GIF、WebP 图片，作为多模态内容发送并保存在历史消息中；压缩上下文、重试和分支保留图片。图片分析需要当前模型支持视觉。
- 支持普通文件及 ZIP 上传；ZIP 通过托管代码解压到独立临时只读目录，不自动运行内容，不把该目录设为工作目录。工作目录为“无”时禁止命令与电脑操作，除非开启完全访问或默许全部 Shell。

## 附件限制

每条消息最多 8 个附件；单张图片最多 8 MB、每条消息图片总量最多 20 MB。单个文件最多 50 MB；ZIP 展开最多 200 MB、2000 个条目。拒绝越界路径、符号链接、设备文件名、重复目标及异常压缩比。RAR、7z 等目前不会自动解压，需转换为 ZIP。

临时文件可能被系统清理；文件已不可用时会要求重新上传。图片内容保存在对话中。

## 验证与运行

本次环境没有 .NET SDK，也不是 Windows 桌面，因此**没有执行 C# 编译、回归程序或 WPF 视觉验收**。已做 XML、XAML 命名与事件、源文件依赖和服务接线静态检查。源码包含新增和扩展的回归程序，需在 .NET 8 / Windows 环境执行。

最终静态检查覆盖 23 个项目文件、130 个 XAML、242 个目标事件处理器、348 个目标名称及 24 个服务调用契约，未发现错误。原有插件契约脚本通过（20 组 DLL/EXE 路由、7 项权限等）。PreviewInteraction 的检查脚本因环境缺少 tree_sitter 未能运行。

从 `XCL2.App` 目录运行：

```powershell
dotnet build XCL2.App.csproj -p:SkipNativePreview=true
dotnet run --project Tests/PopupAnimationRegression/PopupAnimationRegression.csproj
dotnet run --project Tests/AgentAnimationRegression/AgentAnimationRegression.csproj
dotnet run --project Tests/AiHttpRegression/AiHttpRegression.csproj
dotnet run --project Tests/AiCommandRegression/AiCommandRegression.csproj
dotnet run --project Tests/AiPermissionsRegression/AiPermissionsRegression.csproj
dotnet run --project Tests/AiAttachmentRegression/AiAttachmentRegression.csproj
dotnet run --project Tests/WorkbenchRegression/WorkbenchRegression.csproj
```

建议在 Windows 上重点验收：冷启动弹窗、关闭后快速重开、子弹窗恢复父层、慢插件；Agent 首次计划、步骤完成、暂停与恢复；动画预设与手动强度；全选后单条禁止、鼠标/键盘权限撤回；图片发送与历史恢复；无工作目录的 ZIP 读取及命令拒绝。
