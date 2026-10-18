# 配色、触屏、插件权限与 Plan 更新

## 插件升级

升级入口、弹窗标题和安装确认标题统一为“升级插件”，移除原来的“安全哨兵”“升级 / 新签名”等名称。

安装升级包后，弹窗右上角显示“插火”，提示为“检查并注册所需权限”。按钮重新校验已安装清单，保留仍需要的已有授权，只审核缺失的所需权限；保存后真正更新权限指纹、启用状态并重新扫描注册。取消、未选齐、功能权限不可用及插件加载失败都不会误报成功。

升级时不再固定补入 `ui.basic` / `ui.components`。权限来自明确清单和包内实际宿主 API 引用；静态分析不加载或执行插件。仅存在 `CreateSettingsPanel` 方法（包括 Debug / Release 的空返回）不能作为默认 UI 授权依据。动态调用、原生 EXE、管道请求和无法确定的自定义配置界面需由作者在清单明确声明；迁移报告会说明该限制。

## 权限列表跟随全局设置

AI 精确命令表格的背景、表头、行、编辑框、选中/悬停颜色、分隔线及勾选框使用全局主题资源；字体绑定全局字体资源。切换深浅色、配色和文字透明度时会同步变化，不再出现截图中的系统白底表格。保留排序、列宽调整、编辑及虚拟化。

## Plan 可使用文本协议

原有 `<<XCL2_ACCESS:{...update_task...}>>` JSON 方法继续可用，AI 也可以在回复末尾输出：

```text
<<XCL2_PLAN>>
○ [inspect] 检查问题与相关文件
– [implement] 修改实现
– [verify] 验证结果
next_action: 读取相关源码
<</XCL2_PLAN>>
```

`○` 表示正在进行，`✓` 表示已完成，`–` 表示未开始。也可用 `in_progress:` / `completed:` / `pending:` / `blocked:`，ID 可省略。完成步骤可写 `✓ [inspect] 检查问题 | evidence: 已定位原因`；支持 `notes:` 和 `next_action:`。

完整文本块会在内部转为原来的任务更新请求，保存后刷新右侧 Plan。模式、权限、任务绑定、工具预算和 Agent 的执行前计划检查继续有效。未闭合、混合控制标记、重复 ID、多个正在进行步骤及超限文本会拒绝并纠错；不保存流式截断的计划。每次提交更新全部步骤，保留已完成状态与证据。

## 26.3 T4 前兼容触屏

加载期间隐藏光标、开始连接服务器和服务端登录日志不再单独被当成进入世界。每个游戏进程独立收集已有输出，结合客户端世界数据、原生鼠标约束和连续光标样本判断。无客户端日志的旧版全屏可使用被动回中证据：用户真实菜单操作移离中心后，游戏自行隐藏并回中至少持续 100ms，才确认视角模式。没有主动发送探测输入，静态中心和触摸抑制不会建立新证据。

视角位移和菜单拖动按显示帧合并，保留小数位移；点击、最终位置和释放及时处理。模式切换、失焦、取消触点、断开及关闭会释放按键并清理队列。不新增日志文件轮询或截图；26.3 T4+ 的 Original 核心路径保持原实现。

若特定 Mod/后端既没有可识别输出，也没有可靠的抓取或回中证据，仍可用原有 MENU 按钮手动切换。尚未实测所有旧版、服务器及 Mod 组合。

## 配色与设置变更

新增樱花、森林、海盐、咖啡、葡萄、莓果、沙丘、午夜八种主题，各提供完整浅色和深色配色；主题选择、候选池和功能搜索均支持它们。

固定模式只保留一个颜色。实际替换为另一个颜色时，右下角气泡提示自动取消前色，并显示原颜色名或自定义色值。初始化、同色重复选择与轮换模式不误提示。

配色、候选、轮换以及背景轮换时间、视频声音/缩放等真正设置选项纳入统一保存，支持手动保存、放弃和自动保存回退。检测使用稳定逻辑控件及已提交配色，排除普通滚动、搜索、下拉浏览、展开自定义面板和未应用的 RGB 取色。合法自定义颜色输入会防抖提交，实际颜色变更会进入修改提示。

## 验证

已通过 XML/JSON、资源重复键、表格样式、事件处理器、测试依赖、API 映射及服务接线静态检查；现有插件 Python 契约检查通过。八种主题的十六套配色通过 160 项文字/背景对比度计算。

本环境没有 .NET SDK，也不是 Windows 桌面，**未执行 C# 编译、.NET 回归或 WPF/游戏实测**。旧 PreviewInteraction Python 检查还缺少 tree_sitter，不能算通过。源码附带回归项目和静态脚本，从 `XCL2.App` 目录运行：

```powershell
python Tests/FollowupSourceValidation/followup_static_check.py .
python Tests/PluginManagementRegression/validate_contracts.py
dotnet build XCL2.App.csproj -p:SkipNativePreview=true
dotnet run --project Tests/PluginUpgradeRegression/PluginUpgradeRegression.csproj
dotnet run --project Tests/AiHttpRegression/AiHttpRegression.csproj
dotnet run --project Tests/LegacyTouchRegression/LegacyTouchRegression.csproj
dotnet run --project Tests/SettingsAppearanceRegression/SettingsAppearanceRegression.csproj
dotnet run --project Tests/WorkbenchRegression/WorkbenchRegression.csproj
dotnet run --project Tests/CrashAiRegression/CrashAiRegression.csproj
```

Windows 验收重点：深浅主题下编辑权限列表；升级无默认权限的插件、取消新增授权和加载失败；文本/JSON 计划与停止生成；旧版启动、进世界、连接失败、重连、背包/死亡菜单和按键释放；固定配色替换提示、手动撤销、自动保存回退，以及只滚动/浏览时无修改提示。
