using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class InstalledVersionComponentsDialog : OverlayDialogControl, IOverlayDialogDismissGuard, IOverlayHostCloseGuard
{
    private readonly MainWindow _owner;
    private readonly string _root;
    private readonly GameVersion _version;
    private readonly bool _deferModNavigation;
    private readonly DownloadService _catalog;
    private readonly ClientLoaderInstallService _loaders;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _query;
    private VersionManifestRoot? _manifest;
    private DownloadQueueItem? _operation;
    private bool _ready, _suppress, _busy, _loading, _refreshingContext, _inventoryReady;
    private bool _composite;
    private IReadOnlyList<InstalledComponent> _components = Array.Empty<InstalledComponent>();
    private long _generation;
    public bool Changed { get; private set; }
    public bool NavigateToModsRequested { get; private set; }
    public bool IsHostCloseBlocked => _busy;

    public InstalledVersionComponentsDialog(MainWindow owner, string minecraftRoot, GameVersion version, bool deferModNavigation = false)
    {
        _owner = owner;
        _root = minecraftRoot;
        _version = version;
        _deferModNavigation = deferModNavigation;
        _catalog = DownloadService.CreateFromConfig(owner.ConfigService.Config);
        _loaders = new ClientLoaderInstallService(owner.ConfigService.Config);
        InitializeComponent();
        InstanceTitle.Text = version.Id;
        CurrentDescription.Text = Describe(version.McVersion, ParseLoader(version.ModLoader), version.ModLoaderVersion);
        var loader = ParseLoader(version.ModLoader).ToString();
        LoaderCombo.SelectedItem = LoaderCombo.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == loader);
        JavaPathBox.Text = owner.ConfigService.Config.JavaPath ?? "";
        _ready = true;
        Loaded += OnLoaded;
        OverlayClosed += (_, _) =>
        {
            _ready = false;
            ++_generation;
            _lifetime.Cancel();
            _query?.Cancel();
            _catalog.Dispose();
            _loaders.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RefreshContextAsync();
    }

    private async Task RefreshContextAsync()
    {
        if (!_ready || _busy || _refreshingContext) return;
        _refreshingContext = true;
        _inventoryReady = false;
        ++_generation;
        _query?.Cancel();
        _loading = false;
        SelectionPanel.IsEnabled = false;
        ComponentList.IsEnabled = false;
        ReplaceCompositeCheck.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        StatusText.Text = "正在读取已安装版本与组件…";
        try
        {
            await InstalledVersionMaintenanceService.RecoverAsync(_root, _version.Id, EnsureIdleAsync, _lifetime.Token);
            await RefreshInventoryAsync();
            if (!_ready) return;
            _inventoryReady = true;
            await LoadManifestAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_ready) StatusText.Text = ex.Message + "\n处理后可点“刷新列表”重试。"; }
        finally
        {
            _refreshingContext = false;
            if (_ready)
            {
                SelectionPanel.IsEnabled = true;
                ComponentList.IsEnabled = true;
                ReplaceCompositeCheck.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                UpdateSummary();
            }
        }
    }

    private async Task RefreshInventoryAsync()
    {
        var config = _owner.ConfigService.Config;
        var isolate = InstanceIsolationPolicy.Resolve(_root, _version.Id, config.IsolateVersionsByDefault, config.VersionIsolationOverrides);
        var gameDirectory = isolate ? ComponentProfileTransaction.VersionDirectory(_root, _version.Id) : _root;
        _components = await Task.Run(() => InstalledComponentInventory.FromProfiles(InstalledComponentInventory.ReadProfiles(_root, _version.Id))
            .Concat(InstalledComponentInventory.FromMods(gameDirectory, _lifetime.Token)).ToArray(), _lifetime.Token);
        if (!_ready) return;
        ComponentList.ItemsSource = _components;
        _composite = InstalledComponentInventory.NeedsCompositeConfirmation(_components);
        ReplaceCompositeCheck.Visibility = _composite ? Visibility.Visible : Visibility.Collapsed;
        CompositeNotice.Visibility = ReplaceCompositeCheck.Visibility;
        ReplaceCompositeCheck.IsChecked = false;
        RemoveLoaderButton.Content = _composite ? "移除全部启动组件" : "移除加载器";
        BackupFolderButton.IsEnabled = (await Task.Run(() => InstanceBackupService.ListBackups(_root, _version.Id), _lifetime.Token)).Count > 0;
    }

    private static ServerCoreType ParseLoader(string? loader) => loader switch
    {
        "Fabric" => ServerCoreType.Fabric, "Legacy Fabric" or "LegacyFabric" => ServerCoreType.LegacyFabric,
        "Forge" => ServerCoreType.Forge, "NeoForge" => ServerCoreType.NeoForge,
        "Quilt" => ServerCoreType.Quilt, "OptiFine" => ServerCoreType.OptiFine, _ => ServerCoreType.Vanilla
    };
    private ServerCoreType SelectedLoader => LoaderCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
        Enum.TryParse<ServerCoreType>(tag, out var loader) ? loader : ServerCoreType.Vanilla;
    private static string Describe(string minecraft, ServerCoreType loader, string? build) =>
        "Minecraft " + minecraft + " · " + (loader == ServerCoreType.Vanilla ? "原版" :
            (loader == ServerCoreType.LegacyFabric ? "Legacy Fabric" : loader.ToString()) + " " + build);

    private async Task LoadManifestAsync()
    {
        if (_busy || !_ready) return;
        _loading = true;
        ApplyButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在获取 Minecraft 版本列表…";
        try
        {
            _manifest = await _catalog.GetVersionManifestAsync(_lifetime.Token);
            if (!_ready) return;
            PopulateMinecraftVersions();
            await LoadBuildsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_ready) StatusText.Text = "无法获取版本列表：" + ex.Message; }
        finally { if (_ready) { RefreshButton.IsEnabled = true; _loading = false; UpdateSummary(); } }
    }

    private void PopulateMinecraftVersions()
    {
        if (_manifest == null) return;
        var selected = MinecraftCombo.SelectedItem as string ?? _version.McVersion;
        _suppress = true;
        try
        {
            MinecraftCombo.ItemsSource = _manifest.Versions.Where(item => IncludeSnapshots.IsChecked == true ||
                item.GetCategory() == VersionCategory.Release || item.Id == selected || item.Id == _version.McVersion)
                .SortNewestFirst().Select(item => item.Id).ToArray();
            MinecraftCombo.SelectedItem = selected;
            // Do not silently choose an unrelated latest game release when the current version is unknown.
        }
        finally { _suppress = false; }
    }

    private async Task LoadBuildsAsync()
    {
        if (!_ready || _busy) return;
        var generation = ++_generation;
        _query?.Cancel();
        using var query = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _query = query;
        _loading = true;
        BuildCombo.ItemsSource = null;
        BuildCombo.IsEnabled = false;
        LatestButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        var loader = SelectedLoader;
        var minecraft = MinecraftCombo.SelectedItem as string;
        BuildPanel.Visibility = loader == ServerCoreType.Vanilla ? Visibility.Collapsed : Visibility.Visible;
        JavaPanel.Visibility = loader is ServerCoreType.Forge or ServerCoreType.NeoForge or ServerCoreType.OptiFine
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
        try
        {
            if (minecraft == null) { StatusText.Text = "请选择 Minecraft 版本。"; return; }
            if (loader == ServerCoreType.Vanilla) { StatusText.Text = "将使用纯净原版；已有模组文件保留。"; return; }
            StatusText.Text = "正在查询该 Minecraft 版本可用的组件…";
            var builds = loader switch
            {
                ServerCoreType.Fabric => await _loaders.GetFabricLoaderVersionsAsync(minecraft, query.Token),
                ServerCoreType.LegacyFabric => await _loaders.GetLegacyFabricLoaderVersionsAsync(minecraft, query.Token),
                ServerCoreType.Quilt => await _loaders.GetQuiltLoaderVersionsAsync(minecraft, query.Token),
                ServerCoreType.Forge => await _loaders.GetForgeInstallerVersionsAsync(minecraft, query.Token),
                ServerCoreType.NeoForge => (await _loaders.GetNeoForgeVersionsAsync(query.Token))
                    .Where(build => ComponentProfileComposer.MinecraftForNeoForge(build) == minecraft)
                    .Select(build => new ServerCoreBuild { DisplayVersion = build, IsRecommended = !build.Contains('-') }).ToList(),
                ServerCoreType.OptiFine => await _loaders.GetOptiFineVersionsAsync(minecraft, query.Token),
                _ => new List<ServerCoreBuild>()
            };
            if (!_ready || generation != _generation || query.IsCancellationRequested) return;
            BuildCombo.ItemsSource = builds;
            var existingBuild = loader == ServerCoreType.Forge && !(_version.ModLoaderVersion ?? "").StartsWith(minecraft + "-", StringComparison.Ordinal)
                ? minecraft + "-" + _version.ModLoaderVersion : _version.ModLoaderVersion;
            BuildCombo.SelectedItem = minecraft == _version.McVersion && loader == ParseLoader(_version.ModLoader)
                ? builds.FirstOrDefault(build => build.DisplayVersion == existingBuild) ?? builds.FirstOrDefault(build => build.IsRecommended) ?? builds.FirstOrDefault()
                : builds.FirstOrDefault(build => build.IsRecommended) ?? builds.FirstOrDefault();
            BuildCombo.IsEnabled = builds.Count > 0;
            LatestButton.IsEnabled = builds.Any(build => build.IsRecommended);
            StatusText.Text = builds.Count == 0 ? "该 Minecraft 版本暂时没有可用构建，请选择其他版本或组件。" :
                $"找到 {builds.Count} 个兼容构建。可选择升级、降级或重新安装当前版本。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_ready && generation == _generation) StatusText.Text = "无法获取组件版本：" + ex.Message; }
        finally
        {
            if (ReferenceEquals(_query, query)) _query = null;
            if (_ready && generation == _generation) { _loading = false; UpdateSummary(); }
        }
    }

    private void UpdateSummary()
    {
        if (!_ready) return;
        var minecraft = MinecraftCombo.SelectedItem as string;
        var build = BuildCombo.SelectedItem as ServerCoreBuild;
        ChangeSummary.Text = minecraft == null ? "选择目标版本后，这里会显示完整的修改内容。" :
            CurrentDescription.Text + (_composite ? "\n将替换：" + string.Join("、", _components.Where(component => !component.IsMod).Select(component => component.Name + " " + component.Version)) : "") +
            "\n→ " + Describe(minecraft, SelectedLoader, build?.DisplayVersion);
        ApplyButton.IsEnabled = _inventoryReady && !_refreshingContext && !_busy && !_loading && minecraft != null &&
            (SelectedLoader == ServerCoreType.Vanilla || build != null) && (!_composite || ReplaceCompositeCheck.IsChecked == true);
        var hasLoader = _components.Any(component => !component.IsMod);
        ApplyButton.Content = minecraft != null && minecraft != _version.McVersion ? "更改游戏版本" :
            SelectedLoader == ServerCoreType.Vanilla && hasLoader ? (_composite ? "移除全部组件" : "移除组件") :
            SelectedLoader != ServerCoreType.Vanilla && !hasLoader ? "添加组件" :
            SelectedLoader != ParseLoader(_version.ModLoader) ? "更换组件" :
            SelectedLoader != ServerCoreType.Vanilla && build?.DisplayVersion != _version.ModLoaderVersion ? "更新组件" : "重新安装";
    }

    private async void Selection_Changed(object sender, SelectionChangedEventArgs e) { if (_ready && _inventoryReady && !_refreshingContext && !_suppress) await LoadBuildsAsync(); }
    private void Build_Changed(object sender, SelectionChangedEventArgs e) => UpdateSummary();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshContextAsync();
    private async void IncludeSnapshots_Click(object sender, RoutedEventArgs e)
    {
        if (!_inventoryReady || _refreshingContext) return;
        PopulateMinecraftVersions();
        await LoadBuildsAsync();
    }
    private void Latest_Click(object sender, RoutedEventArgs e)
    {
        BuildCombo.SelectedItem = BuildCombo.Items.Cast<ServerCoreBuild>().FirstOrDefault(build => build.IsRecommended);
        UpdateSummary();
    }
    private void RemoveLoader_Click(object sender, RoutedEventArgs e) => LoaderCombo.SelectedIndex = 0;
    private void ReplaceComposite_Click(object sender, RoutedEventArgs e) => UpdateSummary();
    private void ManageComponent_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: InstalledComponent component }) return;
        if (component.IsMod) { ManageMods_Click(sender, e); return; }
        var selected = LoaderCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == component.Name);
        if (selected != null) LoaderCombo.SelectedItem = selected;
        if (_composite) ReplaceCompositeCheck.BringIntoView();
        else BuildCombo.Focus();
    }
    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Java", Filter = "Java 程序|java.exe;javaw.exe|所有文件|*.*" };
        if (dialog.ShowDialog() == true) JavaPathBox.Text = dialog.FileName;
    }

    private async Task EnsureIdleAsync()
    {
        var active = DownloadQueueService.Instance.FindActiveForVersion(_root, _version.Id);
        if (active != null && active != _operation && active != DownloadQueueService.Instance.CurrentItem)
            throw new InvalidOperationException("该实例已有下载或补全任务，请等待任务结束后再修改组件。");
        await _owner.ProcessManager.RefreshAsync();
        var target = ComponentProfileTransaction.VersionDirectory(_root, _version.Id);
        foreach (var game in _owner.ProcessManager.Running)
        {
            if (string.Equals(game.VersionId, _version.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该实例仍在运行。请先关闭游戏，再修改 Minecraft 或组件。");
            if (string.IsNullOrWhiteSpace(game.GameDir))
                throw new InvalidOperationException("检测到其他启动器打开的 Minecraft，无法确认它使用的游戏目录。请先关闭该游戏，再修改组件。");
            string gameDirectory;
            try { gameDirectory = Path.GetFullPath(game.GameDir).TrimEnd(Path.DirectorySeparatorChar); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            { throw new InvalidOperationException("无法确认正在运行的游戏使用哪个目录，请先关闭游戏再修改组件。", error); }
            if (string.Equals(gameDirectory, target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该实例仍在运行。请先关闭游戏，再修改 Minecraft 或组件。");
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ApplyButton.IsEnabled || MinecraftCombo.SelectedItem is not string minecraft) return;
        var build = BuildCombo.SelectedItem as ServerCoreBuild;
        var change = new InstalledVersionChange(minecraft, SelectedLoader, build?.DisplayVersion,
            JavaPathBox.Text.Trim(), build?.RawIdentifier, ReplaceCompositeCheck.IsChecked == true);
        if (!MessageBoxDialog.ShowConfirm(ChangeSummary.Text + "\n\n将自动备份当前实例并保留模组、存档和配置。确认应用这次修改？", "版本与组件")) return;
        _busy = true;
        SelectionPanel.IsEnabled = false;
        ReplaceCompositeCheck.IsEnabled = false;
        ComponentList.IsEnabled = false;
        ModsButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        CloseButton.Content = "取消操作";
        OperationProgress.Visibility = Visibility.Visible;
        OperationProgress.IsIndeterminate = true;
        try
        {
            await EnsureIdleAsync();
            Exception? deferredInstallerCleanup = null;
            _operation = DownloadQueueService.Instance.StartNew("修改版本与组件 · " + _version.Id, async (item, ct) =>
            {
                var queueProgress = item.CreateProgress();
                var progress = new Progress<ProgressInfo>(value =>
                {
                    queueProgress.Report(value);
                    if (!_ready) return;
                    StatusText.Text = value.Stage + (string.IsNullOrEmpty(value.CurrentFile) ? "" : "\n" + value.CurrentFile);
                    OperationProgress.IsIndeterminate = value.Total <= 0;
                    OperationProgress.Maximum = Math.Max(1, value.Total);
                    OperationProgress.Value = value.Done;
                });
                string backup;
                try
                {
                    backup = await new InstalledVersionMaintenanceService(_owner.ConfigService.Config)
                        .ApplyAsync(_root, _version.Id, change, progress, ct, EnsureIdleAsync, () =>
                        {
                            item.SupportsCancel = false;
                            CloseButton.IsEnabled = false;
                        });
                }
                catch (Exception error) when (InstallerProcessLifetime.RequiresPreservedDirectory(error))
                {
                    // The download queue normalizes cancellation exceptions. Retain the directory/PID
                    // warning locally so cancelling does not hide a still-quarantined installer tree.
                    deferredInstallerCleanup = error;
                    throw;
                }
                item.CompletionText = "版本与组件已更新，实例备份已保留";
                StatusText.Text = "修改完成。操作前备份：\n" + backup;
                BackupFolderButton.IsEnabled = true;
            }, _version.Id, _root, supportsPause: false, supportsRetry: false, supportsCancel: true, originPage: "版本与组件");
            var result = await _operation.Completion;
            if (result == DownloadQueueItemStatus.Completed)
            {
                Changed = true;
                _version.McVersion = minecraft;
                _version.ModLoader = change.Loader == ServerCoreType.Vanilla ? null : change.Loader == ServerCoreType.LegacyFabric ? "Legacy Fabric" : change.Loader.ToString();
                _version.ModLoaderVersion = change.LoaderVersion;
                CurrentDescription.Text = Describe(minecraft, change.Loader, change.LoaderVersion);
                _inventoryReady = false;
                try { await RefreshInventoryAsync(); _inventoryReady = true; }
                catch (Exception refreshError) { StatusText.Text = "组件已更新，清单刷新失败：" + refreshError.Message; }
                MessageBoxDialog.ShowSuccess("版本与组件已更新。存档、模组、配置和实例名称已保留；点“查看备份”可打开操作前完整备份所在的文件夹。", "修改完成");
            }
            else if (result == DownloadQueueItemStatus.Canceled)
                StatusText.Text = "操作已取消，原实例的版本文件保持不变。"
                    + (deferredInstallerCleanup == null ? "" : "\n" + deferredInstallerCleanup.Message);
            else StatusText.Text = "修改未完成：" + (_operation.LastError?.Message ?? _operation.StatusText);
        }
        catch (Exception ex) { StatusText.Text = "修改未完成：" + ex.Message; }
        finally
        {
            _operation = null;
            _busy = false;
            SelectionPanel.IsEnabled = true;
            ReplaceCompositeCheck.IsEnabled = true;
            ComponentList.IsEnabled = true;
            ModsButton.IsEnabled = true;
            CloseButton.Content = "关闭";
            CloseButton.IsEnabled = true;
            OperationProgress.Visibility = Visibility.Collapsed;
            UpdateSummary();
        }
    }

    public void RequestDismiss()
    {
        if (!_busy) { CloseWith(Changed); return; }
        if (_operation is { CanCancel: true } operation)
        {
            DownloadQueueService.Instance.Cancel(operation);
            StatusText.Text = "正在取消并清理，请等待操作安全结束…";
            CloseButton.IsEnabled = false;
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => RequestDismiss();
    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.Combine(_root, "versions", ".instance_backups");
            ArchiveImportSafety.CheckLinks(directory);
            if (!Directory.Exists(directory)) { StatusText.Text = "备份目录尚未创建。"; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception error) { StatusText.Text = "无法打开备份目录：" + error.Message; }
    }
    private void ManageMods_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _owner.ConfigService.Config.SelectedFolderPath = _root;
        _owner.ConfigService.Config.SelectedVersionId = _version.Id;
        _owner.ConfigService.Save();
        NavigateToModsRequested = true;
        CloseWith(Changed);
        if (!_deferModNavigation) _owner.NavigateToModManager();
    }
}
