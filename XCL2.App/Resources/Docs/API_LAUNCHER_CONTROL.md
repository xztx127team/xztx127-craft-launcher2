# 启动器控制与界面定制 API

通过 `PluginContext.Control` 调用。页面导航需要 `ui.navigation`，窗口操作需要 `ui.window`，启动和实例选择需要 `launcher.control`，配置读写分别需要 `settings.read` / `settings.write`。权限以当前授权为准；启动接受结果不等于游戏已成功运行。

```csharp
// ctx 为 Initialize 收到的 PluginContext；先在插件清单声明并由用户授权 ui.navigation。
var pages = await ctx.Control.GetPagesAsync();
var downloads = pages.FirstOrDefault(page => page.Id == "downloads");
if (downloads != null) await ctx.Control.NavigateAsync(downloads.Id);
```

以下签名与内置 API 总览一致。

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


## PluginControlService

```csharp
public sealed class PluginControlService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<ImmutableArray<PluginPageInfo>> GetPagesAsync(CancellationToken ct = default)` | — | — |
| `Task<bool> NavigateAsync(string pageId, CancellationToken ct = default)` | — | 通过宿主正常页面导航，保留设置页的保存/放弃确认。 |
| `Task<PluginControlSnapshot> GetSnapshotAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginWindowSnapshot> GetWindowAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginWindowSnapshot> UpdateWindowAsync(PluginWindowPatch patch, CancellationToken ct = default)` | — | — |
| `Task<PluginWindowSnapshot> SetWindowStateAsync(PluginWindowAction action, CancellationToken ct = default)` | — | — |
| `Task<PluginAppearanceSnapshot> GetAppearanceAsync(CancellationToken ct = default)` | — | — |
| `Task<PluginAppearanceSnapshot> UpdateSettingsAsync(PluginSettingsPatch patch, CancellationToken ct = default)` | — | — |
| `Task<bool> SelectAccountAsync(string accountId, CancellationToken ct = default)` | — | — |
| `Task<bool> SelectInstanceAsync(string instanceId, CancellationToken ct = default)` | — | — |
| `Task<PluginLaunchAcceptance> LaunchSelectedAsync(CancellationToken ct = default)` | — | Accepted 只表示已进入宿主启动流程；成功与否订阅 PluginGameStarted/PluginGameExited。 |
| `Task<bool> CancelLaunchAsync(CancellationToken ct = default)` | — | — |


## PluginControlSnapshot

```csharp
public sealed record PluginControlSnapshot(PluginWindowSnapshot Window, bool IsLaunching, string? InstanceId, string? AccountId);
```


## PluginPageInfo

```csharp
public sealed record PluginPageInfo(string Id, string Title, bool IsPluginPage);
```


## PluginWindowPatch

```csharp
public sealed record PluginWindowPatch
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string? Title { get; init; }` | — | — |
| `double? Width { get; init; }` | — | — |
| `double? Height { get; init; }` | — | — |
| `bool? Topmost { get; init; }` | — | — |
| `bool? SidebarCollapsed { get; init; }` | — | — |


## PluginWindowSnapshot

```csharp
public sealed record PluginWindowSnapshot(string Title, string State, double Width, double Height, bool Topmost, bool SidebarCollapsed);
```


## PluginWindowAction

```csharp
public enum PluginWindowAction
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Activate` | — | — |
| `Minimize` | — | — |
| `Maximize` | — | — |
| `Restore` | — | — |


## PluginSettingsPatch

```csharp
public sealed record PluginSettingsPatch
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `string? UiSkin { get; init; }` | — | — |
| `bool? IsDarkMode { get; init; }` | — | — |
| `string? CustomAccentColor { get; init; }` | — | — |
| `bool? SimplifiedModeEnabled { get; init; }` | — | — |
| `bool? EnablePageAnimations { get; init; }` | — | — |
| `bool? EnableUiAnimations { get; init; }` | — | — |
| `int? UiZoomPercent { get; init; }` | — | — |
| `bool? EnableWindowTransparency { get; init; }` | — | — |
| `int? WindowOpacityPercent { get; init; }` | — | — |
| `bool? EnableGlobalWindowTransparency { get; init; }` | — | — |
| `int? GlobalWindowOpacityPercent { get; init; }` | — | — |
| `int? TextOpacityPercent { get; init; }` | — | — |
| `int? BrightnessPercent { get; init; }` | — | — |
| `int? MinMemoryMb { get; init; }` | — | — |
| `int? MaxMemoryMb { get; init; }` | — | — |
| `bool? IsolateVersionsByDefault { get; init; }` | — | — |
| `string? BuiltInDownloadSource { get; init; }` | — | — |
| `double? CallbackTimeoutSeconds { get; init; }` | — | — |


## PluginAppearanceSnapshot

```csharp
public sealed record PluginAppearanceSnapshot(string UiSkin, bool IsDarkMode, string? CustomAccentColor, int UiZoomPercent, bool EnableWindowTransparency, int WindowOpacityPercent, bool EnableGlobalWindowTransparency, int GlobalWindowOpacityPercent, int TextOpacityPercent, int BrightnessPercent, double CallbackTimeoutSeconds, ImmutableArray<string> AvailableSkins);
```


## PluginLaunchAcceptance

```csharp
public sealed record PluginLaunchAcceptance(bool Accepted, string Code);
```


## PluginPermissionNames

```csharp
public static class PluginPermissionNames
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `const string ProjectsRead = "projects.read", ProjectsWrite = "projects.write", TasksManage = "tasks.manage"` | — | — |
| `const string DownloadsControl = "downloads.control", InstancesWrite = "instances.write", ModsWrite = "mods.write", ResourcesRead = "resources.read", ResourcesInstall = "resources.install", JavaRead = "java.read", JavaSelect = "java.select"` | — | — |
| `const string LauncherControl = "launcher.control", SettingsWrite = "settings.write", UiWindow = "ui.window"` | — | — |
| `const string UiPreview = "ui.preview", WorldsRead = "worlds.read", WorldsWrite = "worlds.write"` | — | — |
| `const string AccountsRead = "accounts.read", InstancesRead = "instances.read", ModsRead = "mods.read", DownloadsRead = "downloads.read", SettingsRead = "settings.read", EnvironmentRead = "environment.read", Lifecycle = "events.lifecycle", LaunchModify = "launch.modify", DownloadIntercept = "downloads.intercept", InstanceValidate = "instances.validate", Network = "network", DownloadProvider = "providers.download", ModProvider = "providers.mod", LoaderProvider = "providers.loader", AuthProvider = "auth.provider", InstanceFilesRead = "files.instances.read", InstanceFilesWrite = "files.instances.write", ExternalFilesRead = "files.external.read", ExternalFilesWrite = "files.external.write", ProcessInstall = "process.install", UiBasic = "ui.basic", UiComponents = "ui.components", UiNavigation = "ui.navigation", UiInstanceActions = "ui.instanceActions", UiSettings = "ui.settings", UiBackground = "ui.background", Messaging = "plugin.messaging"` | — | — |
| `static ImmutableArray<string> All { get; }` | — | — |

