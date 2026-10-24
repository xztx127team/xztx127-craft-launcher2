# API 样例与常见错误

下面示例放在已持有 `PluginContext ctx` 的插件方法内使用。完整插件生命周期和项目生成入口见「插件开发指南」；在插件管理中生成模板可获得与本版本程序集匹配的项目引用。

```csharp
try
{
    var result = await ctx.Control.LaunchSelectedAsync();
    ctx.Log($"启动请求：{result.Code}，已接受：{result.Accepted}");
}
catch (OperationCanceledException) { ctx.Log("操作已取消"); }
catch (Exception ex) { ctx.Log(ex.Message); }
```

常见问题：

- 权限不足：先声明所需权限，再请用户在插件管理中授权；重试前检查插件是否被停止。
- 页面无法挂载：控件已存在父对象时不可复用，CreateContent 每次创建新实例。
- 回调超时：让长任务支持取消，按需配置等待时间，不要同步阻塞 UI。
- 启动已接受但游戏退出：订阅游戏生命周期并检查日志，不将 Accepted 当作成功。
- 文档打不开：本版本所有专题随程序嵌入；导出 MD 不需要浏览器。

以下签名与内置 API 总览一致。

## IPlugin

```csharp
public interface IPlugin
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string Id { get; }` | — | 插件唯一 ID，建议用"作者名.插件名"这种反向域名风格的短字符串， 例如 "someone.better-skin-tools"。这个 ID 同时也是配置文件、日志前缀的 key， 上线之后不要再改，否则等于变成了一个新插件（旧配置对不上）。 |
| `string DisplayName { get; }` | — | 展示给用户看的插件名称，随时可以改，不影响配置归属。 |
| `string Version { get; }` | — | 插件版本号，纯展示用，建议跟随语义化版本（1.0.0 这种）。 |
| `string Description { get; }` | — | 一两句话说明这个插件是干什么的，显示在插件管理列表里。 |
| `string Author { get; }` | — | 插件作者，展示用，可以留空字符串。 |
| `void Initialize(PluginContext ctx)` | — | 启动器加载插件时调用一次。ctx 提供了配置读写、日志、以及向"插件"页面 挂一个自定义设置面板的能力——具体每个成员的用法见 PluginContext 自己的注释。 这里面不应该做耗时的同步操作（网络请求、大文件 IO 等），耗时工作请自己开 Task.Run，不要卡住启动器主界面的启动过程。 |
| `void Shutdown()` | — | 插件被用户关闭，或者启动器正常退出时调用一次，用来释放插件自己占用的资源 （文件句柄、定时器、后台线程等）。如果插件没有需要清理的东西，留空实现即可。 |
| `UserControl? CreateSettingsPanel(PluginContext ctx)` | — | 可选：返回一个插件自己的配置界面（一个 UserControl），显示在"插件管理"里 该插件条目的"配置"按钮之后。不需要图形配置界面的插件（比如只是后台跑点逻辑的） 可以直接返回 null——这时"配置"按钮会改成显示一个通用的、基于 PluginContext.Config 里已保存键值对的只读 JSON 预览，方便用户至少能看到当前保存了什么。 |


## PluginContext

```csharp
public sealed class PluginContext : IDisposable
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `PluginConfigService Config { get; }` | — | 该插件独享的配置读写入口，落盘在 %AppData%/XCL2/Plugins/config/{PluginId}.json， 具体用法见 PluginConfigService 自己的注释。 |
| `string DataDirectory { get; }` | — | 该插件独享的数据目录（%AppData%/XCL2/Plugins/data/{PluginId}/）， 已在 Initialize 调用前确保存在。需要落盘缓存文件、下载文件的插件应该写在这里， 而不是写到 dll 自己所在目录（那个目录可能只读，或者被启动器整体当作只读区对待）。 |
| `Action<string> Log { get; }` | — | 把一行文字写进启动器日志（会自动带上"[Plugin:该插件Id]"前缀）， 排查问题时能直接在启动器"日志"页里看到，不需要插件自己另外弹窗/写文件。 |
| `Version ApiVersion { get; }` | — | UI 注册 API 版本；原有 IPlugin 契约保持兼容。 |
| `PluginMetadata Metadata { get; private set; }` | — | — |
| `PluginUiService Ui { get; }` | — | 注册弹窗、按钮、选项、面板和弹窗生命周期订阅。 |
| `PluginComponentService Components { get; }` | — | 任意已加载视图的组件定位、增加、移除、移动、替换与布局。 |
| `PluginRuntimeService Runtime { get; }` | — | 启动内核、头像和实时 3D 渲染后端扩展。 |
| `PluginPermissionService Permissions { get; }` | — | — |
| `PluginLauncherService Launcher { get; }` | — | — |
| `PluginControlService Control { get; }` | — | 页面导航、窗口布局、启动控制与经过校验的设置写入。 |
| `PluginEventService Events { get; }` | — | — |
| `PluginProviderService Providers { get; }` | — | — |
| `PluginNetworkService Network { get; }` | — | — |
| `PluginMessageService Messages { get; }` | — | — |
| `PluginPreviewService Preview { get; }` | — | — |
| `PluginWorldService Worlds { get; }` | — | — |
| `PluginManagementService Management { get; }` | — | API 2.2：实例、模组、Java 与下载队列管理。 |
| `PluginResourceService Resources { get; }` | — | API 2.2：宿主资源查询、文件安装及原版版本安装。 |
| `PluginProjectService Projects { get; }` | — | — |
| `PluginTaskService Tasks { get; }` | — | — |
| `PluginChangeService Changes { get; }` | — | — |
| `PluginQueryService Queries { get; }` | — | — |
| `void Dispose()` | — | — |
| `System.Threading.CancellationToken Stopping { get; }` | — | 插件停止时取消，后台任务应传入此令牌。 |
| `PluginContext(string pluginId, PluginConfigService config, string dataDirectory, Action<string> log)` | — | — |
| `string PluginId { get; }` | — | 原安装等于 IPlugin.Id，副本含独立实例后缀。使用此 Id 隔离注册项，方便插件代码里不用额外持有 IPlugin 引用 也能拿到自己的 Id（比如拼日志前缀、拼文件名）。 |


## PluginPermissionService

```csharp
public sealed class PluginPermissionService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `bool Has(string permission)` | — | — |
| `ImmutableArray<PluginPermissionGrant> GetSnapshot()` | — | — |
| `void Demand(string permission)` | — | — |


## PluginOperationException

```csharp
public class PluginOperationException : Exception
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string Code { get; }` | — | — |
| `string SafeMessage { get; }` | — | — |
| `PluginOperationException(string code, string safeMessage) : base(safeMessage)` | — | — |


## PluginFlowCanceledException

```csharp
public sealed class PluginFlowCanceledException : PluginOperationException
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string PluginId { get; }` | — | — |
| `string Reason { get; }` | — | — |


## PluginHostUnavailableException

```csharp
public sealed class PluginHostUnavailableException : PluginOperationException
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `PluginHostUnavailableException() : base("HostUnavailable", "此插件上下文未连接到运行中的启动器。")` | — | — |

