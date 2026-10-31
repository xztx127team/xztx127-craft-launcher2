# 组合自己的启动器界面

通过 `ctx.Ui` 注册按钮、面板、选项和页面。每次内容工厂必须返回新的 WPF 控件；保存返回的 `IDisposable`，卸载时释放。基础界面需要 `ui.basic`；组件与预览能力还有各自的授权。

```csharp
// 将注册对象保存在插件字段，并在 Dispose 时释放。
var registration = ctx.Ui.RegisterButton(new PluginButtonDefinition
{
    Id = "hello",
    Text = "记录一条日志",
    Target = PluginUiTargets.PluginSettings,
    OnClick = _ => { ctx.Log("按钮已点击"); return Task.CompletedTask; }
});
```

以下签名与内置 API 总览一致。

## PluginUiService

```csharp
public sealed partial class PluginUiService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable RegisterNavigationPage(PluginNavigationPageDefinition definition)` | — | — |
| `IDisposable RegisterInstanceAction(PluginInstanceActionDefinition definition)` | — | — |
| `IDisposable RegisterSetting(PluginOptionDefinition definition)` | — | — |
| `IDisposable RegisterBackground(PluginBackgroundDefinition definition)` | — | — |
| `Task<PluginBackgroundAsset?> ImportBackgroundImageAsync(CancellationToken ct = default)` | — | — |
| `CancellationToken Stopping { get; }` | — | — |
| `Task RunOnUiAsync(Action action)` | — | — |
| `IDisposable RegisterDialog(PluginDialogDefinition definition)` | — | — |
| `IDisposable RegisterButton(PluginButtonDefinition definition)` | — | — |
| `IDisposable RegisterOption(PluginOptionDefinition definition)` | — | — |
| `IDisposable RegisterPanel(PluginPanelDefinition definition)` | — | — |
| `IDisposable SubscribeDialogs(Action<PluginDialogEvent> observer)` | — | — |
| `string GetDialogTarget(string id)` | — | — |
| `Task<bool?> ShowDialogAsync(string id, object? parameter = null)` | — | — |
| `Task<bool?> ShowMessageAsync(string title, string message)` | — | — |
| `Task<bool> ConfirmAsync(string title, string message)` | — | — |
| `void Dispose()` | — | — |


## PluginUiTargets

```csharp
public static class PluginUiTargets
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `const string PluginSettings = "plugin.settings"` | — | — |
| `const string PluginManager = "dialog:PluginManagerDialog"` | — | — |
| `const string LoaderChoice = "dialog:LoaderChoiceDialog"` | — | — |
| `const string MultiLoaderInstall = "dialog:MultiLoaderInstallWindow"` | — | — |
| `const string CreateServer = "dialog:CreateServerWindow"` | — | — |
| `const string ExperimentalFeatures = "dialog:ExperimentalFeaturesWindow"` | — | — |
| `static string ForDialog<T>() where T : FrameworkElement, IOverlayDialog` | — | — |


## PluginDialogDefinition

```csharp
public sealed record PluginDialogDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `required string Title { get; init; }` | — | — |
| `required Func<PluginDialogContext, FrameworkElement> CreateContent { get; init; }` | — | — |
| `double Width { get; init; }` | — | — |
| `bool DismissOnEscape { get; init; }` | — | — |
| `bool DismissOnBackgroundClick { get; init; }` | — | — |


## PluginButtonDefinition

```csharp
public sealed record PluginButtonDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `required string Text { get; init; }` | — | — |
| `string Target { get; init; }` | — | — |
| `string? Description { get; init; }` | — | — |
| `int Order { get; init; }` | — | — |
| `required Func<PluginUiContext, Task> OnClick { get; init; }` | — | — |


## PluginPanelDefinition

```csharp
public sealed record PluginPanelDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `string Target { get; init; }` | — | — |
| `int Order { get; init; }` | — | — |
| `required Func<PluginUiContext, FrameworkElement> CreateContent { get; init; }` | — | 每次创建新控件，不可复用已挂在其他父控件下的 WPF 元素。 |


## PluginOptionDefinition

```csharp
public sealed record PluginOptionDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `required string Title { get; init; }` | — | — |
| `string Target { get; init; }` | — | — |
| `string? ConfigKey { get; init; }` | — | — |
| `string? Description { get; init; }` | — | — |
| `int Order { get; init; }` | — | — |
| `PluginOptionKind Kind { get; init; }` | — | — |
| `object? DefaultValue { get; init; }` | — | — |
| `double Minimum { get; init; }` | — | — |
| `double Maximum { get; init; }` | — | — |
| `IReadOnlyList<PluginChoice> Choices { get; init; }` | — | — |
| `Func<object?, string?>? Validate { get; init; }` | — | 返回 null/空字符串表示合法，否则在控件旁显示错误且不保存。 |
| `Action<object?>? OnChanged { get; init; }` | — | 成功保存之后调用；回调异常只记录日志，不撤销已经保存的配置。 |


## PluginNavigationPageDefinition

```csharp
public sealed record PluginNavigationPageDefinition(string Id, string Title, Func<PluginUiContext, FrameworkElement> CreateContent, bool ShowInSimplifiedMode = false, ImmutableArray<string> Languages = default, int Order = 0);
```


## PluginComponentService

```csharp
public sealed class PluginComponentService : IDisposable
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable Register(PluginComponentDefinition definition)` | — | — |
| `IReadOnlyList<PluginComponentInfo> Inspect(string target)` | — | 检查当前已经加载的视图。命名选择器须唯一；匿名控件使用返回的路径。 |
| `void Refresh()` | — | 宿主动态重建组件后可显式重新应用当前注册。 |
| `void Dispose()` | — | — |


## PluginComponentDefinition

```csharp
public sealed record PluginComponentDefinition
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `required string Id { get; init; }` | — | — |
| `required string Target { get; init; }` | — | view:MainWindow、view:SettingsPage 或 dialog:某弹窗类名。 |
| `PluginComponentAction Action { get; init; }` | — | — |
| `string? Selector { get; init; }` | — | 组件 Name，或 Inspect 返回的 $root/0/1 路径；Add 时不需要。 |
| `string? Parent { get; init; }` | — | Add/Move 的容器选择器；$overlay 为自动创建、覆盖整个视图的绝对定位画布。 |
| `int Index { get; init; }` | — | — |
| `int Order { get; init; }` | — | — |
| `PluginComponentLayout Layout { get; init; }` | — | — |
| `Func<PluginUiContext, FrameworkElement>? CreateContent { get; init; }` | — | Add/Replace 创建全新组件；可返回含自定义渲染后端的 WPF 宿主控件。 |


## PluginComponentLayout

```csharp
public sealed record PluginComponentLayout
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `int? Row { get; init; }` | — | — |
| `int? Column { get; init; }` | — | — |
| `int? RowSpan { get; init; }` | — | — |
| `int? ColumnSpan { get; init; }` | — | — |
| `int? ZIndex { get; init; }` | — | — |
| `double? Left { get; init; }` | — | — |
| `double? Top { get; init; }` | — | — |
| `double? Right { get; init; }` | — | — |
| `double? Bottom { get; init; }` | — | — |
| `double? Width { get; init; }` | — | — |
| `double? Height { get; init; }` | — | — |
| `Thickness? Margin { get; init; }` | — | — |
| `HorizontalAlignment? HorizontalAlignment { get; init; }` | — | — |
| `VerticalAlignment? VerticalAlignment { get; init; }` | — | — |
| `Dock? Dock { get; init; }` | — | — |
| `double? MinWidth { get; init; }` | — | — |
| `double? MinHeight { get; init; }` | — | — |
| `double? MaxWidth { get; init; }` | — | — |
| `double? MaxHeight { get; init; }` | — | — |
| `double? Opacity { get; init; }` | — | — |
| `Visibility? Visibility { get; init; }` | — | — |
| `bool? IsHitTestVisible { get; init; }` | — | — |
| `bool? ClipToBounds { get; init; }` | — | — |
| `Transform? RenderTransform { get; init; }` | — | — |
| `Point? RenderTransformOrigin { get; init; }` | — | — |


## PluginRuntimeService

```csharp
public sealed class PluginRuntimeService : IDisposable
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable RegisterLaunchMiddleware(string id, PluginLaunchMiddleware middleware, int order = 0)` | — | — |
| `IDisposable RegisterAvatarRenderer(string id, PluginAvatarRenderer renderer, int order = 0)` | — | — |
| `IDisposable RegisterSkinModelRenderer(string id, PluginSkinModelRendererFactory factory, int order = 0)` | — | 替换皮肤模型的整个渲染后端；可返回 WPF、D3DImage 或原生窗口宿主。 |
| `void Dispose()` | — | — |


## PluginPreviewService

```csharp
public sealed class PluginPreviewService
```

| API 签名 | EXE method | 权限 / 接口说明 |
| --- | --- | --- |
| `IDisposable RegisterCard(string id, string title, string description, string destination = "worlds", string accent = "#1BD96A")` | — | — |
| `void UpdateCard(string id, string title, string description, string accent = "#1BD96A")` | — | — |
| `IDisposable SetAppearance(string title, string accent = "#1BD96A", bool compact = false)` | — | — |

