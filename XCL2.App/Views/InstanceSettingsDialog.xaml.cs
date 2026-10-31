using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class InstanceSettingsDialog : OverlayDialogControl
{
    private readonly AppConfig _config;
    private readonly string _folderPath;
    private readonly GameVersion _version;
    private readonly MainWindow _owner;
    private readonly DispatcherTimer _memoryPreviewTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? _javaPreviewCancellation;
    private bool _settingsSaveAccepted;
    private int? _previewJavaBitness;
    private string _previewJavaNote = "Java 尚未匹配；启动前会按实际 Java 位数复核内存上限。";

    public InstanceSettingsDialog(MainWindow owner, string folderPath, GameVersion version)
    {
        _owner = owner;
        _config = owner.ConfigService.Config;
        _folderPath = folderPath;
        _version = version;
        InitializeComponent();
        InputExperienceService.SetRole(InstanceMinMemoryBoxDlg, ExperienceInputRole.Memory);
        InputExperienceService.SetRole(InstanceMaxMemoryBoxDlg, ExperienceInputRole.Memory);

        TitleText.Text = $"「{version.Id}」的单独设置";
        LoadSettings();
        InstanceMinMemoryBoxDlg.TextChanged += MemoryPreview_Changed;
        InstanceMaxMemoryBoxDlg.TextChanged += MemoryPreview_Changed;
        InstanceCustomJvmArgsBoxDlg.TextChanged += MemoryPreview_Changed;
        InstanceMemOptComboDlg.SelectionChanged += MemoryPreview_Changed;
        VersionJavaListComboDlg.SelectionChanged += PreviewJava_Changed;
        VersionJavaOverrideBoxDlg.TextChanged += PreviewJava_Changed;
        VersionIsolationOverrideCheckDlg.Checked += PreviewJava_Changed;
        VersionIsolationOverrideCheckDlg.Unchecked += PreviewJava_Changed;
        SkipJavaMismatchPromptCheckDlg.Checked += PreviewJava_Changed;
        SkipJavaMismatchPromptCheckDlg.Unchecked += PreviewJava_Changed;
        _memoryPreviewTimer.Tick += (_, _) => RefreshMemoryPreview();
        Loaded += (_, _) => { _memoryPreviewTimer.Start(); RefreshPreviewJavaAsync(); };
        Unloaded += (_, _) => { _memoryPreviewTimer.Stop(); _javaPreviewCancellation?.Cancel(); };
        RefreshMemoryPreview();
    }

    private void LoadSettings()
    {
        var versionId = _version.Id;

        VersionJavaListComboDlg.Items.Clear();
        VersionJavaListComboDlg.Items.Add(new JavaListItem { Entry = null }); // "（不指定）"
        foreach (var j in _config.InstalledJavas) VersionJavaListComboDlg.Items.Add(new JavaListItem { Entry = j });
        var selectedJavaId = _config.VersionJavaIdOverrides.TryGetValue(versionId, out var jid) ? jid : null;
        VersionJavaListComboDlg.SelectedItem = VersionJavaListComboDlg.Items.Cast<JavaListItem>()
            .FirstOrDefault(i => i.Entry?.Id == selectedJavaId) ?? VersionJavaListComboDlg.Items[0];

        VersionJavaOverrideBoxDlg.Text = _config.VersionJavaOverrides.TryGetValue(versionId, out var javaOverride) && javaOverride > 0
            ? javaOverride.ToString()
            : "";

        SkipJavaMismatchPromptCheckDlg.IsChecked = _config.VersionSkipJavaMismatchPrompt.Contains(versionId);

        VersionIsolationOverrideCheckDlg.IsChecked = InstanceIsolationPolicy.Resolve(_folderPath, versionId,
            _config.IsolateVersionsByDefault, _config.VersionIsolationOverrides);

        VersionResourcePackIsolationOverrideCheckDlg.IsChecked = _config.VersionResourcePackIsolationOverrides.TryGetValue(versionId, out var resIsolate)
            ? resIsolate
            : _config.IsolateResourcePacksByDefault;

        var hasAutoJoin = _config.VersionAutoJoinServer.TryGetValue(versionId, out var autoJoinAddr)
            && !string.IsNullOrWhiteSpace(autoJoinAddr);
        AutoJoinServerCheckDlg.IsChecked = hasAutoJoin;
        AutoJoinServerAddressBoxDlg.Text = hasAutoJoin ? autoJoinAddr : "";
        AutoJoinServerAddressBoxDlg.IsEnabled = hasAutoJoin;

        var versionDir = Path.Combine(_folderPath, "versions", versionId);
        var instance = InstanceConfigService.LoadOrCreateDefault(versionDir);
        OptimizeBeforeLaunchCheck.IsChecked = instance.OptimizeMemoryBeforeLaunch;
        var gpuTag = instance.UseHighPerformanceGpuForGame switch
        {
            true => "Enabled",
            false => "Disabled",
            null => "Follow",
        };
        InstanceHighPerformanceGpuComboDlg.SelectedItem = InstanceHighPerformanceGpuComboDlg.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == gpuTag) ?? InstanceHighPerformanceGpuComboDlg.Items[0];

        InstanceMinMemoryBoxDlg.Text = instance.MinMemoryMb?.ToString() ?? "";
        InstanceMaxMemoryBoxDlg.Text = instance.MaxMemoryMb?.ToString() ?? "";
        InstanceCustomJvmArgsBoxDlg.Text = instance.CustomJvmArgs ?? "";

        // 内存优化：EnableMemoryOptimization 为 null 时代表"跟随全局"，非 null 时代表
        // 该实例显式选了开启(true)/关闭(false)。OnLauncherOpen 只额外预览，
        // 开启动态模式后每次实际启动仍重新计算，不把预览结果写回手动值。
        string memOptTag;
        if (instance.EnableMemoryOptimization == false)
        {
            memOptTag = "Disabled";
        }
        else if (instance.EnableMemoryOptimization == true)
        {
            memOptTag = instance.MemoryOptimizationTiming == MemoryOptimizationTiming.OnLauncherOpen
                ? "OnLauncherOpen"
                : "BeforeGameLaunch";
        }
        else
        {
            memOptTag = "Follow";
        }
        InstanceMemOptComboDlg.SelectedItem = InstanceMemOptComboDlg.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == memOptTag) ?? InstanceMemOptComboDlg.Items[0];

        var supportsGraphicsApi = SupportsGraphicsApiPreference(_version);
        GraphicsApiPanelDlg.Visibility = supportsGraphicsApi ? Visibility.Visible : Visibility.Collapsed;
        SelectGraphicsApiPreference(instance.GraphicsApiPreference);

        InstanceXclDirTextDlg.Text = InstanceConfigService.GetXclDir(versionDir);
    }

    private InstanceSettings ReadMemoryDraft(int? minimum, int? maximum)
    {
        var tag = (InstanceMemOptComboDlg.SelectedItem as ComboBoxItem)?.Tag as string ?? "Follow";
        return new InstanceSettings
        {
            MinMemoryMb = minimum,
            MaxMemoryMb = maximum,
            EnableMemoryOptimization = tag switch
            {
                "Disabled" => false,
                "BeforeGameLaunch" or "OnLauncherOpen" => true,
                _ => null,
            },
            MemoryOptimizationTiming = tag switch
            {
                "BeforeGameLaunch" => MemoryOptimizationTiming.BeforeGameLaunch,
                "OnLauncherOpen" => MemoryOptimizationTiming.OnLauncherOpen,
                _ => null,
            },
        };
    }

    private void MemoryPreview_Changed(object sender, RoutedEventArgs e) => RefreshMemoryPreview();

    private void RefreshMemoryPreview()
    {
        InstanceMemoryPreviewWarningDlg.Visibility = Visibility.Collapsed;
        try
        {
            var draft = ReadMemoryDraft(ParseNullableMemory(InstanceMinMemoryBoxDlg.Text),
                ParseNullableMemory(InstanceMaxMemoryBoxDlg.Text));
            if (draft.MinMemoryMb > draft.MaxMemoryMb)
                throw new InvalidOperationException("实例初始内存不能大于最大内存，请先调整再保存。");

            // 与实际启动完全相同的实例/全局优先级、物理内存采样、32 位限制及会话覆盖。
            var physical = MemoryOptimizerService.ReadPhysicalMemory();
            var allocation = MemoryOptimizerService.ConstrainForJava(_config,
                MemoryOptimizerService.ResolveLaunchMemory(_config, draft, physical), _previewJavaBitness);
            var hasManual = draft.MinMemoryMb.HasValue || draft.MaxMemoryMb.HasValue;
            var dynamicRequested = draft.EnableMemoryOptimization ?? (_config.EnableMemoryOptimization && !hasManual);
            var source = allocation.IsDynamic
                ? (_config.FixedMemoryAllocationMb is > 0 ? "动态模式中的固定分配值" : "按全局动态内存模式计算；启动前会重新计算")
                : hasManual ? "实例手动内存优先，留空的端点跟随全局" : "使用全局手动内存";
            if (dynamicRequested && physical == null)
                source += "；无法读取物理内存，已按启动逻辑回退到手动值";
            if (App.StartupArgs.MinMemoryMb.HasValue || App.StartupArgs.MaxMemoryMb.HasValue)
            {
                var session = CommandLineSessionPolicy.ResolveMemory(allocation.MinMemoryMb, allocation.MaxMemoryMb,
                    App.StartupArgs.MinMemoryMb, App.StartupArgs.MaxMemoryMb);
                allocation = allocation with { MinMemoryMb = session.MinMb, MaxMemoryMb = session.MaxMb, IsDynamic = false };
                source = "使用本次启动器命令行的内存覆盖";
            }
            var instanceJvm = string.IsNullOrWhiteSpace(InstanceCustomJvmArgsBoxDlg.Text)
                ? null : InstanceCustomJvmArgsBoxDlg.Text.Trim();
            var jvm = CommandLineSessionPolicy.CombineJvmArguments(_config.AdvancedMode,
                instanceJvm, _config.CustomJvmArgs, App.StartupArgs.ExtraJvmArgs);
            List<string> tokens = new();
            string? warning = null;
            try { if (jvm != null) tokens = LauncherService.SplitArgsRespectingQuotes(jvm); }
            catch (FormatException) { warning = "JVM 参数引号未闭合，启动器会忽略整段自定义参数。"; }
            var preview = MemoryLaunchPreviewPolicy.Apply(allocation, tokens);
            if (preview.Error != null) throw new InvalidOperationException(preview.Error);

            InstanceMemoryPreviewTextDlg.Text = $"初始 {preview.InitialMbText} MB · 最大 {preview.MaximumMbText} MB";
            if (preview.HasOverride) source += "；已计入自定义 JVM 堆内存覆盖";
            if (!_config.AdvancedMode && (instanceJvm != null || !string.IsNullOrWhiteSpace(_config.CustomJvmArgs)))
                source += "；普通模式下不应用已保存的自定义 JVM 参数";
            var memoryInfo = physical == null ? "" : $"系统总内存 {physical.TotalMb} MB，当前可用 {physical.AvailableMb} MB。";
            InstanceMemoryPreviewDetailDlg.Text = source + "。\n" + memoryInfo + _previewJavaNote;
            warning = string.Join(" ", new[] { warning, preview.Uncertainty }.Where(s => !string.IsNullOrEmpty(s)));
            if (_previewJavaBitness == 32 && preview.MaximumBytes > 1536L * 1024 * 1024)
                warning += " 当前使用 32 位 Java，超过 1536 MB 的手动堆配置可能无法启动。";
            if (!string.IsNullOrWhiteSpace(warning))
            {
                InstanceMemoryPreviewWarningDlg.Text = warning;
                InstanceMemoryPreviewWarningDlg.Visibility = Visibility.Visible;
            }
        }
        catch (InvalidOperationException ex)
        {
            InstanceMemoryPreviewTextDlg.Text = "当前输入无法生成有效的启动内存预览";
            InstanceMemoryPreviewDetailDlg.Text = "请检查初始内存、最大内存和自定义 JVM 参数。";
            InstanceMemoryPreviewWarningDlg.Text = ex.Message;
            InstanceMemoryPreviewWarningDlg.Visibility = Visibility.Visible;
        }
    }

    private void PreviewJava_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshPreviewJavaAsync();
    }

    private async void RefreshPreviewJavaAsync()
    {
        _javaPreviewCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _javaPreviewCancellation = cancellation;
        var token = cancellation.Token;
        _previewJavaBitness = null;
        _previewJavaNote = "正在匹配 Java；启动前会按实际 Java 位数复核内存上限。";
        RefreshMemoryPreview();
        var selected = (VersionJavaListComboDlg.SelectedItem as JavaListItem)?.Entry;
        var explicitPath = !string.IsNullOrWhiteSpace(App.StartupArgs.JavaPathOverride)
            ? App.StartupArgs.JavaPathOverride
            : _owner.ConfigService.ResolveJavaPath(selected?.Id ?? _config.SelectedJavaId);
        var registered = _owner.ConfigService.GetJavaListInPriorityOrder().Select(j => j.JavawPath).ToArray();
        var configuredPath = _config.JavaPath;
        var isolate = VersionIsolationOverrideCheckDlg.IsChecked == true;
        var explicitMajor = int.TryParse(VersionJavaOverrideBoxDlg.Text, out var major) && major > 0 ? major : (int?)null;
        int? globalMajor = _config.AdvancedMode ? _config.PreferredJavaMajorVersion : null;
        var skipMismatch = selected != null && SkipJavaMismatchPromptCheckDlg.IsChecked == true;
        var enforceMatch = _config.EnforceJavaVersionMatch && !skipMismatch &&
            string.IsNullOrWhiteSpace(App.StartupArgs.JavaPathOverride);
        try
        {
            var path = await Task.Run(() =>
            {
                var required = explicitMajor ?? LauncherService.GetRequiredJavaMajorVersion(_folderPath, _version.Id, isolate);
                var preferred = required is > 0 ? required : globalMajor;
                if (explicitPath != null && enforceMatch && preferred is > 0)
                {
                    var actual = JavaService.TryGetJavaMajorVersionSync(explicitPath, token);
                    if (actual is > 0 && !JavaService.IsJavaMajorCompatible(actual.Value, preferred.Value))
                    {
                        explicitPath = null;
                        // 实际启动的强制切换分支从 FindJava(null, ...) 重新匹配，
                        // 不再把全局手写路径当作额外候选。
                        configuredPath = null;
                    }
                }
                return explicitPath ?? new JavaService().FindJava(configuredPath, preferred, null, token, registered);
            }, token);
            if (token.IsCancellationRequested) return;
            _previewJavaBitness = JavaService.TryGetJavaBitness(path);
            _previewJavaNote = _previewJavaBitness switch
            {
                32 => "当前匹配 32 位 Java，已计入自动分配上限；启动时更换 Java 会重新计算。",
                64 => "当前匹配 64 位 Java；启动时更换 Java 会重新计算。",
                _ => "Java 位数暂未确定；如启动时匹配 32 位 Java，自动分配上限会调整为 1536 MB（显式固定值除外）。",
            };
            RefreshMemoryPreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _previewJavaNote = "暂时无法匹配 Java，预览未计入 32 位 Java 限制；启动时会重新检测。";
            System.Diagnostics.Debug.WriteLine($"[XCL2] 实例内存预览 Java 检测失败：{ex.Message}");
            RefreshMemoryPreview();
        }
        finally
        {
            if (ReferenceEquals(_javaPreviewCancellation, cancellation)) _javaPreviewCancellation = null;
        }
    }

    private static bool SupportsGraphicsApiPreference(GameVersion version)
    {
        return new[] { version.McVersion, version.Id }
            .Select(VersionInfoResolver.ExtractAnyVersion)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Any(v => IsAtLeast261(v!));
    }

    private static bool IsAtLeast261(string value)
    {
        var numeric = value.Split('-', 2)[0];
        var parts = numeric.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return false;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsedPatch) ? parsedPatch : 0;

        // 同时兼容项目现有的 26.1 命名与可能出现的 1.26.1 形式。
        if (major == 1)
            return minor > 26 || (minor == 26 && patch >= 1);
        return major > 26 || (major == 26 && minor >= 1);
    }

    private void SelectGraphicsApiPreference(string? preference)
    {
        var normalized = preference?.Trim().ToLowerInvariant();
        if (normalized != InstanceConfigService.GraphicsApiOpenGl && normalized != InstanceConfigService.GraphicsApiVulkan)
            normalized = InstanceConfigService.GraphicsApiGame;

        GraphicsApiPreferenceComboDlg.SelectedItem = GraphicsApiPreferenceComboDlg.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            ?? GraphicsApiPreferenceComboDlg.Items[0];
    }

    private string GetSelectedGraphicsApiPreference()
    {
        var selected = (GraphicsApiPreferenceComboDlg.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim().ToLowerInvariant();
        return selected is InstanceConfigService.GraphicsApiOpenGl or InstanceConfigService.GraphicsApiVulkan
            ? selected
            : InstanceConfigService.GraphicsApiGame;
    }

    private void AutoJoinServerCheckDlg_Changed(object sender, RoutedEventArgs e)
    {
        AutoJoinServerAddressBoxDlg.IsEnabled = AutoJoinServerCheckDlg.IsChecked == true;
    }

    /// <summary>把弹窗控件上的值写回配置并保存。</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settingsSaveAccepted = false;
        int? instanceMin, instanceMax;
        try
        {
            instanceMin = ParseNullableMemory(InstanceMinMemoryBoxDlg.Text);
            instanceMax = ParseNullableMemory(InstanceMaxMemoryBoxDlg.Text);
            if (instanceMin.HasValue && instanceMax.HasValue && instanceMin.Value > instanceMax.Value)
                throw new InvalidOperationException("实例最小内存不能大于最大内存。");
        }
        catch (InvalidOperationException ex)
        { MessageBoxDialog.ShowWarning(ex.Message, "内存配置有误"); return; }
        var versionId = _version.Id;

        var selectedJava = (VersionJavaListComboDlg.SelectedItem as JavaListItem)?.Entry;
        if (selectedJava != null)
            _config.VersionJavaIdOverrides[versionId] = selectedJava.Id;
        else
            _config.VersionJavaIdOverrides.Remove(versionId);

        // "不再提示切换"该开关只在确实选了某个具体 Java 时才有意义——没选具体 Java 的话
        // 启动时走的是自动搜索/下载逻辑，根本不会触发"手动指定的 Java 跟自动匹配不一致"该
        // 判断分支，勾了也不会有任何实际效果，这里直接不落盘，避免配置文件里留一条死数据、
        // 以后用户换掉具体 Java 选择后该开关却还残留着生效范围以外的旧状态。
        if (selectedJava != null && SkipJavaMismatchPromptCheckDlg.IsChecked == true)
            _config.VersionSkipJavaMismatchPrompt.Add(versionId);
        else
            _config.VersionSkipJavaMismatchPrompt.Remove(versionId);

        var javaText = VersionJavaOverrideBoxDlg.Text.Trim();
        if (javaText.Length == 0)
        {
            _config.VersionJavaOverrides.Remove(versionId);
        }
        else if (int.TryParse(javaText, out var javaMajor) && javaMajor is >= 8 and <= 99)
        {
            _config.VersionJavaOverrides[versionId] = javaMajor;
        }
        else
        {
            MessageBoxDialog.ShowWarning("Java 版本请填一个数字(如 8、17、21、25)，或留空使用自动匹配。",
                Loc.T("Str_Cs_Invalid_Input", "输入有误"));
            return;
        }

        // Isolation is persisted with this physical instance below, not in the legacy ID-only dictionary.
        _config.VersionResourcePackIsolationOverrides[versionId] = VersionResourcePackIsolationOverrideCheckDlg.IsChecked == true;

        if (AutoJoinServerCheckDlg.IsChecked == true)
        {
            var addr = AutoJoinServerAddressBoxDlg.Text.Trim();
            if (addr.Length == 0)
            {
                MessageBoxDialog.ShowWarning("已勾选「开启后进入某某某服务器」，请填写服务器地址（例如 play.example.com），或取消勾选。",
                    Loc.T("Str_Cs_Invalid_Input", "输入有误"));
                return;
            }
            _config.VersionAutoJoinServer[versionId] = addr;
        }
        else
        {
            _config.VersionAutoJoinServer.Remove(versionId);
        }

        _config.SelectedVersionId = versionId;

        var gpuTag = (InstanceHighPerformanceGpuComboDlg.SelectedItem as ComboBoxItem)?.Tag as string ?? "Follow";
        bool? instanceUseHighPerformanceGpu = gpuTag switch
        {
            "Enabled" => true,
            "Disabled" => false,
            _ => null,
        };

        var memoryDraft = ReadMemoryDraft(instanceMin, instanceMax);

        // 镜像写入实例目录：versions/<id>/xcl/settings.json（同 VersionSelectPage 那份逻辑）。
        var versionDir = Path.Combine(_folderPath, "versions", versionId);
        if (Directory.Exists(versionDir))
        {
            

            var instanceSettings = InstanceConfigService.TryLoad(versionDir) ?? new InstanceSettings();
            instanceSettings.IsolateVersion = VersionIsolationOverrideCheckDlg.IsChecked == true;
            instanceSettings.JavaId = selectedJava?.Id;
            instanceSettings.UseHighPerformanceGpuForGame = instanceUseHighPerformanceGpu;
            instanceSettings.MinMemoryMb = instanceMin;
            instanceSettings.MaxMemoryMb = instanceMax;
            instanceSettings.CustomJvmArgs = string.IsNullOrWhiteSpace(InstanceCustomJvmArgsBoxDlg.Text) ? null : InstanceCustomJvmArgsBoxDlg.Text.Trim();
            instanceSettings.GraphicsApiPreference = GraphicsApiPanelDlg.Visibility == Visibility.Visible
                ? GetSelectedGraphicsApiPreference()
                : null;
            instanceSettings.AutoJoinServerAddress = AutoJoinServerCheckDlg.IsChecked == true ? AutoJoinServerAddressBoxDlg.Text.Trim() : null;
            instanceSettings.OptimizeMemoryBeforeLaunch = OptimizeBeforeLaunchCheck.IsChecked == true;
            instanceSettings.EnableMemoryOptimization = memoryDraft.EnableMemoryOptimization;
            instanceSettings.MemoryOptimizationTiming = memoryDraft.MemoryOptimizationTiming;
            try
            {
                InstanceConfigService.Save(versionDir, instanceSettings);
            }
            catch (Exception ex)
            {
                ErrorPresenter.LogFallback($"写入实例设置失败：{versionDir}", ex);
                MessageBoxDialog.ShowWarning("实例设置未保存，请检查目录写入权限和可用磁盘空间后重试。\n" + ex.Message, "保存失败");
                return;
            }
        }

        _settingsSaveAccepted = true;
        CloseWith(true);
    }

    private static int? ParseNullableMemory(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (InputExperiencePolicy.TryParseMemoryMb(text, ConfigService.Active?.Config.Ux50InputMemoryUnits != false, out var value)) return value;
        throw new InvalidOperationException("实例内存请输入正整数 MB、完整 MB 的容量（如 2GB），或留空跟随全局设置。");
    }

    private async void BackupInstanceDlg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await InstanceBackupService.CreateBackupAsync(_folderPath, _version.Id, "手动备份");
            MessageBoxDialog.ShowSuccess($"实例备份已创建：\n{path}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"实例备份失败：{ex.Message}");
        }
    }

    private void OpenLoaderMaintenance_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InstalledVersionComponentsDialog(_owner, _folderPath, _version, deferModNavigation: true);
        dialog.ShowDialog();
        if (dialog.Changed) _owner.RefreshSidebar();
        if (!dialog.NavigateToModsRequested) return;
        var choice = MessageBoxDialog.ShowThreeChoice("前往 Mod 管理会关闭当前版本设置。要保留这里尚未保存的设置吗？", "前往 Mod 管理",
            "返回设置", "放弃并前往", "保存并前往");
        if (choice == XclMessageResult.Cancel) return;
        if (choice == XclMessageResult.Yes)
        {
            Save_Click(sender, e);
            if (!_settingsSaveAccepted) return;
            _owner.ConfigService.Save();
        }
        else CloseWith(false);
        _owner.NavigateToModManager();
    }

    private async void ExportInstanceLaunchScriptDlg_Click(object sender, RoutedEventArgs e) =>
        await ExportInstanceLaunchScriptAsync(includeAccessToken: false);

    private async void ExportInstanceTokenScriptDlg_Click(object sender, RoutedEventArgs e) =>
        await ExportInstanceLaunchScriptAsync(includeAccessToken: true);

    /// <summary>仅本次显式点击含令牌按钮时导出当前游戏访问令牌，不保存导出偏好。</summary>
    private async Task ExportInstanceLaunchScriptAsync(bool includeAccessToken)
    {
        if (!ExportInstanceLaunchScriptButtonDlg.IsEnabled) return;
        ExportInstanceLaunchScriptButtonDlg.IsEnabled = false;
        ExportInstanceTokenScriptButtonDlg.IsEnabled = false;
        try
        {
            var owner = _owner;
            var cfg = _config;
            var account = owner.ConfigService.GetSelectedAccount();
            if (account == null)
            {
                MessageBoxDialog.ShowInfo("请先在“账户管理”中登录或创建一个账户，再导出启动脚本。", Loc.T("Str_Status_Tip", "提示"));
                return;
            }
            if (includeAccessToken && account.Type == AccountType.Offline)
            {
                MessageBoxDialog.ShowInfo("当前为离线账户，没有可导出的登录令牌。可使用“导出该版本的启动脚本”。", Loc.T("Str_Status_Tip", "提示"));
                return;
            }
            if (includeAccessToken && string.IsNullOrWhiteSpace(account.MinecraftAccessToken))
            {
                MessageBoxDialog.ShowInfo("当前账户没有可用的游戏登录令牌，请先登录或正常启动一次游戏，再导出含令牌脚本。", Loc.T("Str_Status_Tip", "提示"));
                return;
            }

            if (includeAccessToken &&
                !await LaunchScriptExportPolicy.ConfirmTokenExportAsync(MessageBoxDialog.ShowSafetyConfirmAsync))
                return;

            var versionId = _version.Id;
            if (!includeAccessToken && account.Type != AccountType.Offline)
            {
                var path = new LauncherService().ExportLaunchScript(new LauncherService.LaunchOptions
                    { MinecraftDir = _folderPath, VersionId = versionId, Account = account });
                MessageBoxDialog.ShowSuccess(LaunchScriptExportPolicy.AccountNotice + $"\n{path}");
                return;
            }
            var javaId = cfg.VersionJavaIdOverrides.TryGetValue(versionId, out var vid) ? vid : cfg.SelectedJavaId;
            var javaPath = owner.ConfigService.ResolveJavaPath(javaId);
            if (javaPath == null)
            {
                var preferMajor = cfg.VersionJavaOverrides.TryGetValue(versionId, out var major) ? major : (int?)null;
                javaPath = new JavaService().FindJava(cfg.JavaPath, preferMajor, owner.ConfigService);
            }
            if (javaPath == null)
            {
                MessageBoxDialog.ShowWarning(
                    "没有找到可用的 Java，无法导出启动脚本。请先在设置页下载/添加一个 Java，或正常启动一次该版本。",
                    Loc.T("Str_Cs_Error", "错误"));
                return;
            }

            var isolateVersion = InstanceIsolationPolicy.Resolve(_folderPath, versionId,
                cfg.IsolateVersionsByDefault, cfg.VersionIsolationOverrides);
            var autoJoinServer = cfg.VersionAutoJoinServer.TryGetValue(versionId, out var joinAddr) && !string.IsNullOrWhiteSpace(joinAddr)
                ? joinAddr.Trim()
                : null;
            var instanceSettings = InstanceConfigService.TryLoad(Path.Combine(_folderPath, "versions", versionId));
            var launchMemory = MemoryOptimizerService.ConstrainForJava(cfg,
                MemoryOptimizerService.ResolveLaunchMemory(cfg, instanceSettings), JavaService.TryGetJavaBitness(javaPath));

            var options = new LauncherService.LaunchOptions
            {
                MinecraftDir = _folderPath,
                VersionId = versionId,
                JavaPath = javaPath,
                Account = account,
                MinMemoryMb = launchMemory.MinMemoryMb,
                MaxMemoryMb = launchMemory.MaxMemoryMb,
                WindowWidth = cfg.WindowWidth,
                WindowHeight = cfg.WindowHeight,
                ShowConsoleWindow = cfg.EnableGameConsoleWindow,
                IsolateVersion = isolateVersion,
                GameLanguage = cfg.GameLanguage,
                VersionTypeLabel = cfg.GameVersionTypeLabel,
                GraphicsApiPreference = instanceSettings?.GraphicsApiPreference,
                UseHighPerformanceGpu = instanceSettings?.ResolveUseHighPerformanceGpuForGame(cfg) ?? cfg.UseHighPerformanceGpuForGame,
                CustomJvmArgs = cfg.AdvancedMode ? instanceSettings?.CustomJvmArgs ?? cfg.CustomJvmArgs : null,
                PreLaunchCommand = cfg.PreLaunchCommand,
                AutoJoinServerAddress = autoJoinServer
            };

            if (includeAccessToken && account.Type == AccountType.AuthServer)
            {
                if (string.IsNullOrWhiteSpace(account.AuthServerApiRoot))
                    throw new InvalidOperationException("当前账户缺少认证服务器地址，请重新登录该账户后再导出。");
                var skinService = new SkinService();
                await DownloadQueueService.Instance.RunAndWaitAsync("准备认证服务器启动脚本",
                    (item, ct) => skinService.EnsureAuthlibInjectorAsync(item.CreateProgress(), ct));
                options.SkinJvmArgs = skinService.BuildSkinJvmArgs(account, cfg.SkinApiRoot);
                if (options.SkinJvmArgs.Count == 0)
                    throw new InvalidOperationException("认证服务器所需的 authlib-injector 未准备完成，无法导出启动脚本。");
            }

            var launcher = new LauncherService();
            await launcher.PrepareLaunchAsync(options);
            var exportedPath = launcher.ExportLaunchScript(options, includeAccessToken: includeAccessToken);
            MessageBoxDialog.ShowSuccess(includeAccessToken
                ? $"含令牌启动脚本已导出到：\n{exportedPath}\n令牌到期后需要重新导出。"
                : $"启动脚本已导出到：\n{exportedPath}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导出启动脚本失败：{DiagnosticRedactionService.Sanitize(ex.Message, 4000)}");
        }
        finally
        {
            ExportInstanceLaunchScriptButtonDlg.IsEnabled = true;
            ExportInstanceTokenScriptButtonDlg.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
