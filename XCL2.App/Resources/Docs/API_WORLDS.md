# 存档读写 API

通过 `ctx.Worlds` 调用。用户需开启设置中的存档 API，并授予 `worlds.read` / `worlds.write`。写入会经过宿主确认和自动备份；请先停止使用该存档的游戏或服务器。支持字段应从 `GetEditSchema()` 获取，不要硬编码未知 NBT 路径。

```csharp
var archives = await ctx.Worlds.GetSnapshotAsync();
var schema = ctx.Worlds.GetEditSchema();
ctx.Log($"发现 {archives.Count} 个存档，可编辑字段 {schema.Count} 个");
```

以下签名与内置 API 总览一致。

## PluginWorldService

```csharp
public sealed class PluginWorldService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `Task<IReadOnlyList<WorldArchive>> GetSnapshotAsync(CancellationToken ct = default)` | — | — |
| `IReadOnlyList<WorldEditField> GetEditSchema()` | — | — |
| `Task<WorldArchive> GetAsync(string worldPath, CancellationToken ct = default)` | — | — |
| `Task<string> UpdateAsync(string worldPath, WorldEdit edit, CancellationToken ct = default)` | — | — |
| `Task<string> GenerateCopyAsync(string worldPath, string destinationPath, WorldEdit edit, CancellationToken ct = default)` | — | — |


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

