using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class InstallClientLoaderWindow : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly ClientLoaderInstallService _loaderService;
    private readonly JavaService _javaService = new();
    /// <summary>原版直装用：跟 ClientLoaderInstallService 内部的 _vanillaDownloader 是同一套
    /// DownloadService，这里单独持有一份是因为 ClientLoaderInstallService 没有把它暴露出来，
    /// 复用同一个 AppConfig 构造，保证多线程下载/限速设置跟加载器安装时一致。</summary>
    private readonly DownloadService _vanillaDownloader;
    private VersionManifestRoot? _versionManifest;

    private ServerCoreType _selectedLoaderType = ServerCoreType.Vanilla;
    private readonly ObservableCollection<string> _mcVersions = new();
    private readonly ObservableCollection<ServerCoreBuild> _buildVersions = new();

    /// <summary>是否已经跑完构造函数里的 InitializeComponent()，跟 CreateServerWindow 同样的
    /// 时序问题：默认选中的 RadioButton 会在 InitializeComponent() 阶段同步触发 Checked 事件，
    /// 此时自动生成的字段还没赋值，直接读会 NullReferenceException。</summary>
    private bool _initialized;
    private bool _isInstalling;
    private bool _overlayClosed;

    /// <summary>安装完成后新版本的 id（供调用方刷新已安装版本列表/直接选中它）。</summary>
    public string? InstalledVersionId { get; private set; }

    private bool _instanceNameUserEdited;

    /// <summary>正在用代码往 InstanceNameBox 里写默认值，此时触发的 TextChanged 不应该被
    /// 当成"用户编辑"计入 _instanceNameUserEdited——否则第一次自动填充默认值那一下就会
    /// 立刻把标志位错误地置成 true，导致后续 MC 版本/加载器变化时默认值再也不会刷新。</summary>
    private bool _isUpdatingInstanceNameProgrammatically;

    /// <summary>预选的加载器类型/MC 版本，来自"下载中心"的加载器筛选行（用户已经在那边选好了
    /// 加载器类型+具体 MC 版本，这里不需要用户重新选一遍，跳过这两步直接定位到"选构建版本"这一步）。
    /// 为 null 表示走原来"从头选择"的完整流程（比如从「版本选择」页的"➕ 安装新版本"按钮进来）。</summary>
    private readonly ServerCoreType? _preselectedLoaderType;
    private readonly string? _preselectedMcVersion;

    public InstallClientLoaderWindow(MainWindow owner, ServerCoreType? preselectLoaderType = null, string? preselectMcVersion = null)
    {
        _owner = owner;
        _preselectedLoaderType = preselectLoaderType;
        _preselectedMcVersion = preselectMcVersion;
        // 用完整 AppConfig 的构造：加载器安装内部也要下载原版底座的 libraries/assets，
        // 应该跟"下载中心-游戏版本"面板一样吃到多线程下载/限速设置，而不是永远单线程。
        _loaderService = new ClientLoaderInstallService(owner.ConfigService.Config);
        _vanillaDownloader = DownloadService.CreateFromConfig(owner.ConfigService.Config);
        InitializeComponent();

        OverlayClosed += (_, _) =>
        {
            _overlayClosed = true;
            if (!_isInstalling) DisposeInstallServices();
        };

        McVersionCombo.ItemsSource = _mcVersions;
        BuildVersionCombo.ItemsSource = _buildVersions;
        BuildVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);

        var detected = _javaService.FindJava(_owner.ConfigService.Config.JavaPath, configService: _owner.ConfigService);
        JavaPathBox.Text = detected ?? "";

        // 应用预选的加载器类型：XAML 默认选中的是 Fabric（LoaderFabric IsChecked="True"），
        // 如果调用方要的是 Forge/NeoForge，这里手动切换一下对应 RadioButton 的选中状态——
        // 这会走正常的 Checked 事件（此时 _initialized 还是 false，事件会被跳过），
        // 所以选中状态本身要在下面手动设置 _selectedLoaderType 字段来保持一致。
        if (_preselectedLoaderType is { } loaderType && loaderType != ServerCoreType.Vanilla)
        {
            _selectedLoaderType = loaderType;
            switch (loaderType)
            {
                case ServerCoreType.Fabric: LoaderFabric.IsChecked = true; break;
                case ServerCoreType.Forge: LoaderForge.IsChecked = true; break;
                case ServerCoreType.NeoForge: LoaderNeoForge.IsChecked = true; break;
                case ServerCoreType.Quilt: LoaderQuilt.IsChecked = true; break;
                case ServerCoreType.OptiFine: LoaderOptiFine.IsChecked = true; break;
                case ServerCoreType.LegacyFabric: LoaderLegacyFabric.IsChecked = true; break;
            }
            BuildVersionPanel.Visibility = loaderType == ServerCoreType.NeoForge ? Visibility.Collapsed : Visibility.Visible;
            BuildVersionLabel.Text = loaderType == ServerCoreType.Forge ? "安装器版本" : "构建版本";
        }
        else
        {
            // 原版：没有独立的加载器/构建版本这一级，直接隐藏该面板。
            BuildVersionPanel.Visibility = Visibility.Collapsed;
        }

        FabricNoJavaHintText.Visibility = _selectedLoaderType is ServerCoreType.Fabric or ServerCoreType.LegacyFabric ? Visibility.Visible : Visibility.Collapsed;
        InstallFabricApiCheck.Visibility = _selectedLoaderType == ServerCoreType.Fabric ? Visibility.Visible : Visibility.Collapsed;
        InstallQslCheck.Visibility = _selectedLoaderType == ServerCoreType.Quilt ? Visibility.Visible : Visibility.Collapsed;
        // 原版/Fabric/Quilt/Legacy Fabric 都不需要本地 Java 来完成"安装"这一步，只有 Forge/NeoForge
        // 需要（游戏本身运行仍会在启动时自动匹配/下载 Java，跟这里的"安装期 Java"是两回事）。
        JavaPathPanel.Visibility = _selectedLoaderType is ServerCoreType.Vanilla or ServerCoreType.Quilt or ServerCoreType.LegacyFabric
            ? Visibility.Collapsed : Visibility.Visible;

        _initialized = true;
        _ = LoadMcVersionsAsync();
        UpdateDefaultInstanceName();
    }

    private async void LoaderType_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // InitializeComponent() 解析阶段的默认选中触发，跳过
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ServerCoreType>(tag, out var loaderType)) return;
        _selectedLoaderType = loaderType;

        // NeoForge 的"版本"下拉框里选的就是完整版本号本身，没有独立的"构建版本"这一级；
        // 原版同样没有"构建版本"这一级（原版一个 MC 版本就对应唯一一个 client jar），
        // 跟 CreateServerWindow 里对 NeoForge 的处理是同一个道理，Vanilla 直接照搬。
        // Quilt 跟 Fabric 一样只有"Loader 版本"这一级构建版本选择，没有 NeoForge/Vanilla 那种
        // "没有独立构建版本"的情况，所以只在 NeoForge/Vanilla 时隐藏该面板，Quilt 沿用默认可见。
        BuildVersionPanel.Visibility = loaderType is ServerCoreType.NeoForge or ServerCoreType.Vanilla
            ? Visibility.Collapsed : Visibility.Visible;
        BuildVersionLabel.Text = loaderType switch
        {
            ServerCoreType.Fabric => "Loader 版本",
            ServerCoreType.LegacyFabric => "Loader 版本",
            ServerCoreType.Quilt => "Loader 版本",
            ServerCoreType.Forge => "安装器版本",
            ServerCoreType.OptiFine => "OptiFine 版本",
            _ => "构建版本"
        };

        // Fabric/Quilt/Legacy Fabric 都走各自 Meta API 的现成 profile json，不需要本地跑安装器，
        // 不强制要求 Java；Forge/NeoForge 必须本地跑安装器，缺 Java 无法继续。
        FabricNoJavaHintText.Visibility = loaderType is ServerCoreType.Fabric or ServerCoreType.LegacyFabric ? Visibility.Visible : Visibility.Collapsed;
        // Fabric API 只对 Fabric 有意义，QSL 只对 Quilt 有意义（Forge/NeoForge/Legacy Fabric 生态
        // 没有这类概念），切换加载器类型时同步显示/隐藏各自对应的可选安装项。
        InstallFabricApiCheck.Visibility = loaderType == ServerCoreType.Fabric ? Visibility.Visible : Visibility.Collapsed;
        InstallQslCheck.Visibility = loaderType == ServerCoreType.Quilt ? Visibility.Visible : Visibility.Collapsed;
        JavaPathPanel.Visibility = loaderType is ServerCoreType.Vanilla or ServerCoreType.Quilt or ServerCoreType.LegacyFabric
            ? Visibility.Collapsed : Visibility.Visible;

        await LoadMcVersionsAsync();
        UpdateJavaRequirementHint();
        UpdateDefaultInstanceName();
    }

    private async Task LoadMcVersionsAsync()
    {
        _mcVersions.Clear();
        _buildVersions.Clear();
        McVersionCombo.IsEnabled = false;
        InstallBtn.IsEnabled = false;

        try
        {
            List<string> versions;
            if (_selectedLoaderType == ServerCoreType.Vanilla)
            {
                // 原版：直接用官方/BMCL 的 version manifest，只列正式版（同向导/下载中心其它
                // 地方"默认只看正式版"的口径保持一致），把清单缓存下来供 Install_Click 里
                // 反查具体 VersionManifestEntry 用。
                _versionManifest = await _vanillaDownloader.GetVersionManifestAsync();
                

                versions = _versionManifest.Versions
                    .Where(v => v.GetCategory() == VersionCategory.Release)
                    .SortNewestFirst()
                    .Select(v => v.Id)
                    .ToList();
            }
            else
            {
                versions = _selectedLoaderType switch
                {
                    ServerCoreType.Fabric => await _loaderService.GetFabricMcVersionsAsync(),
                    ServerCoreType.LegacyFabric => await _loaderService.GetLegacyFabricMcVersionsAsync(),
                    ServerCoreType.Forge => await _loaderService.GetForgeVersionsAsync(),
                    ServerCoreType.NeoForge => await _loaderService.GetNeoForgeVersionsAsync(),
                    ServerCoreType.Quilt => await _loaderService.GetQuiltMcVersionsAsync(),
                    // OptiFine 没有单独一份"支持哪些 MC 版本"的清单接口，BMCLAPI 是按具体 MC 版本
                    // 才能查到有没有构建（见 GetOptiFineVersionsAsync）。这里沿用官方正式版清单
                    // 给用户选，选中某个版本之后如果 OptiFine 恰好没有对应构建，下面
                    // McVersionCombo_SelectionChanged 里会拿到空列表并提示"该版本暂无 OptiFine 构建"，
                    // 而不是一开始就要遍历全部版本逐个探测（成本太高、也没有必要）。
                    ServerCoreType.OptiFine => (_versionManifest ??= await _vanillaDownloader.GetVersionManifestAsync())
                        .Versions.Where(v => v.GetCategory() == VersionCategory.Release).SortNewestFirst().Select(v => v.Id).ToList(),
                    _ => new List<string>()
                };
            }
            foreach (var v in versions) _mcVersions.Add(v);
            // 有预选 MC 版本时优先选它（下载中心场景：用户已经在版本列表里点了具体版本的
            // "下载安装"），选不中（比如该加载器根本没适配该版本）才退回选第一项。
            if (_preselectedMcVersion != null && _mcVersions.Contains(_preselectedMcVersion))
                McVersionCombo.SelectedItem = _preselectedMcVersion;
            else if (_mcVersions.Count > 0) McVersionCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Fetch_The_Version_List_This_Is_", "获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
        finally
        {
            McVersionCombo.IsEnabled = true;
            InstallBtn.IsEnabled = true;
        }
    }

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _buildVersions.Clear();
        BuildVersionHintText.Visibility = Visibility.Collapsed;
        UpdateJavaRequirementHint();
        UpdateDefaultInstanceName();
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        if (_selectedLoaderType is ServerCoreType.NeoForge or ServerCoreType.Vanilla) return; // 没有独立的第二级下拉框

        try
        {
            List<ServerCoreBuild> builds = _selectedLoaderType switch
            {

                ServerCoreType.Fabric => await _loaderService.GetFabricLoaderVersionsAsync(mcVersion),
                ServerCoreType.LegacyFabric => await _loaderService.GetLegacyFabricLoaderVersionsAsync(mcVersion),
                ServerCoreType.Forge => await _loaderService.GetForgeInstallerVersionsAsync(mcVersion),
                ServerCoreType.Quilt => await _loaderService.GetQuiltLoaderVersionsAsync(mcVersion),
                ServerCoreType.OptiFine => await _loaderService.GetOptiFineVersionsAsync(mcVersion),
                _ => new List<ServerCoreBuild>()
            };
            foreach (var b in builds) _buildVersions.Add(b);
            BuildVersionCombo.SelectedItem = builds.FirstOrDefault(b => b.IsRecommended) ?? builds.FirstOrDefault();
            UpdateDefaultInstanceName();
            if (builds.Count == 0 && _selectedLoaderType == ServerCoreType.OptiFine)
            {

                BuildVersionHintText.Text = $"Minecraft {mcVersion} 暂无 OptiFine 构建（不是所有版本 OptiFine 官方都发布过），请换一个版本。";
                BuildVersionHintText.Visibility = Visibility.Visible;
            }
        }
        catch (InvalidOperationException ex)
        {
            // 这一类是我们自己在 ClientLoaderInstallService 里抛出的"人话"异常
            // （比如"Fabric 还没有为 MC xxx 发布 Loader"），直接把原文给用户，
            // 不要再套一层笼统的"可能是网络连接问题"，那会把真正的原因盖掉。
            ErrorPresenter.ShowFriendlyError(ex.Message, $"[获取构建版本列表失败] {ex}", "获取构建版本列表失败");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Fetch_The_Build_List_This_Is_Us", "获取构建版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[获取构建版本列表失败] {ex}", "获取构建版本列表失败");
        }
    }

    /// <summary>「构建/加载器版本」下拉框变化时，默认实例名要跟着刷新（同 MC 版本变化同理）。</summary>
    private void BuildVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDefaultInstanceName();

    private void InstanceNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingInstanceNameProgrammatically) return; // 代码自己写入默认值触发的，不算用户编辑
        _instanceNameUserEdited = true;

        var raw = InstanceNameBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            

            InstanceNameHintText.Text = "留空：安装完成后使用默认命名（版本号+加载器+加载器版本）";
            return;
        }
        var sanitized = ModpackInstallService.SanitizeInstanceName(raw);
        InstanceNameHintText.Text = string.Equals(raw.Trim(), sanitized, StringComparison.Ordinal)
            ? "会在 versions 文件夹下建一个同名目录（重名时自动加编号）"
            : $"名称里有文件夹不允许的字符，实际会建成：{sanitized}";
    }

    /// <summary>
    /// 用当前选中的 MC 版本/加载器类型/构建版本，计算并回填「实例名称」输入框的默认值。
    /// 只有在用户还没手动编辑过该框时才会覆盖已有内容——避免用户输入到一半突然被清空。
    /// 默认格式跟改造前的"官方 id"风格保持一致（如 "fabric-loader-0.15.11-1.20.1"），
    /// 只是现在允许用户在安装前直接在这里改掉。
    /// </summary>
    private void UpdateDefaultInstanceName()
    {
        if (_instanceNameUserEdited) return;
        if (McVersionCombo.SelectedItem is not string mcVersion || string.IsNullOrWhiteSpace(mcVersion))
        {
            _isUpdatingInstanceNameProgrammatically = true;
            InstanceNameBox.Text = "";
            _isUpdatingInstanceNameProgrammatically = false;
            return;
        }

        var buildVersion = (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.DisplayVersion;
        string suggested = _selectedLoaderType switch
        {
            ServerCoreType.Vanilla => mcVersion,
            ServerCoreType.Fabric => string.IsNullOrEmpty(buildVersion) ? "" : $"fabric-loader-{buildVersion}-{mcVersion}",
            ServerCoreType.LegacyFabric => string.IsNullOrEmpty(buildVersion) ? "" : $"legacyfabric-loader-{buildVersion}-{mcVersion}",
            ServerCoreType.Quilt => string.IsNullOrEmpty(buildVersion) ? "" : $"quilt-loader-{buildVersion}-{mcVersion}",
            ServerCoreType.Forge => string.IsNullOrEmpty(buildVersion) ? "" : $"{mcVersion}-forge-{buildVersion}",
            ServerCoreType.NeoForge => $"neoforge-{mcVersion}",
            _ => mcVersion
        };

        _isUpdatingInstanceNameProgrammatically = true;
        InstanceNameBox.Text = suggested;
        _isUpdatingInstanceNameProgrammatically = false;
        InstanceNameHintText.Text = string.IsNullOrEmpty(suggested)
            ? "请先选择完整的版本信息"
            : "默认命名（版本号+加载器+加载器版本），可以直接修改";
    }

    /// <summary>
    /// 提示该版本大概需要的 Java 主版本号。Forge/NeoForge 复用服务端已经在用的
    /// ServerJavaRequirement 估算表（安装器本身需要用该版本的 Java 来跑，逻辑跟服务端安装
    /// Forge/NeoForge 核心时完全一致）；Fabric 客户端安装本身不需要本地 Java（见
    /// FabricNoJavaHintText），这里不显示 Java 要求提示，真正的 Java 版本要求要等装完之后
    /// 由 LauncherService.GetRequiredJavaMajorVersion 在启动时读 version json 决定。
    /// </summary>
    private void UpdateJavaRequirementHint()
    {
        

        if (_selectedLoaderType is ServerCoreType.Fabric or ServerCoreType.Quilt or ServerCoreType.LegacyFabric
            || McVersionCombo.SelectedItem is not string mcVersion)
        {
            JavaRequirementHintText.Visibility = Visibility.Collapsed;
            return;
        }

        var estimated = ServerJavaRequirement.EstimateMajorVersionForMcVersion(mcVersion);
        JavaRequirementHintText.Text = $"提示：MC {mcVersion} 预计需要 Java {estimated} 来运行安装器" +
            "（点击「自动检测」尝试匹配，找不到时需要先去「设置」页下载对应版本的 Java）。";
        JavaRequirementHintText.Visibility = Visibility.Visible;
    }

    private void AutoDetectJava_Click(object sender, RoutedEventArgs e)
    {
        int? preferMajor = null;
        if (_selectedLoaderType is not (ServerCoreType.Fabric or ServerCoreType.Quilt or ServerCoreType.LegacyFabric) && McVersionCombo.SelectedItem is string mcVersion)
            preferMajor = ServerJavaRequirement.EstimateMajorVersionForMcVersion(mcVersion);

        var found = _javaService.FindJava(null, preferMajor, _owner.ConfigService);
        if (found == null)
        {
            MessageBoxDialog.ShowWarning(Loc.T("Str_Cs_No_Usable_Java_Was_Found_Download_Or_Con", "没有检测到可用的 Java。请在「设置」页先下载/配置 Java，或者手动填写路径。"), Loc.T("Str_Cs_Java_Not_Found", "未找到 Java"));
            return;
        }
        JavaPathBox.Text = found;
    }

    private void DisposeInstallServices()
    {
        _loaderService.Dispose();
        _vanillaDownloader.Dispose();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling) return;
        if (McVersionCombo.SelectedItem is not string mcVersion)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Minecraft_Version", "请选择 Minecraft 版本。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        var buildVersion = (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.DisplayVersion;
        if (_selectedLoaderType is not (ServerCoreType.NeoForge or ServerCoreType.Vanilla) && string.IsNullOrEmpty(buildVersion))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Build_Or_Loader_Version", "请选择构建/加载器版本。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        if (_selectedLoaderType is not (ServerCoreType.Fabric or ServerCoreType.Quilt or ServerCoreType.LegacyFabric or ServerCoreType.Vanilla) &&
            (string.IsNullOrWhiteSpace(JavaPathBox.Text) || !File.Exists(JavaPathBox.Text)))
        {
            MessageBoxDialog.ShowInfo("请提供一个有效的 Java 路径（点击「自动检测」或手动填写），Forge/NeoForge 安装器需要本地 Java 才能运行。", Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        var minecraftDir = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)?.Path;
        if (string.IsNullOrEmpty(minecraftDir))
        {
            MessageBoxDialog.ShowWarning("没有找到当前选中的 .minecraft 文件夹，请先在「版本管理」页选择/添加一个文件夹。", Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        // OptiFine 兼容性提醒：很多 mod（尤其是需要 Fabric/Forge Mixin 注入、或者跟光影/渲染管线
        // 打交道的 mod）跟 OptiFine 自成一体的渲染实现冲突，装了容易直接崩溃或者部分效果失效；
        // 新版本上 OptiFine 的帧数表现也普遍不如 Sodium（社区俗称"钠"）这类基于 Fabric 的现代
        // 渲染优化 mod。这不是"能不能装"的技术限制（探测到有构建就真的能装），而是"装了值不值"
        // 的取舍问题，交给用户自己决定，安装前明确告知一次，用户选择继续就正常走安装流程。
        if (_selectedLoaderType == ServerCoreType.OptiFine)
        {
            var proceed = MessageBoxDialog.ShowConfirm(
                "OptiFine 对很多 Mod 的兼容性不太好，容易跟需要 Mixin 注入或者光影相关的 Mod 冲突；" +
                "在较新版本上，它的性能也往往不如 Sodium（俗称\"钠\"）这类基于 Fabric 的现代渲染优化 Mod。\n\n" +
                "如果只是单纯想要光影/高清材质支持，不一定非要 OptiFine 不可，可以考虑 Fabric + Sodium + Iris 该组合。\n\n" +
                "仍然要继续安装 OptiFine 吗？",
                "安装前提醒：OptiFine 兼容性");
            if (!proceed) return;
        }

        _isInstalling = true;
        InstallBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressStageText.Text = "已加入右上角下载任务栏";

        // 固定这次安装的选项，避免排队时界面选择变化影响已经登记的任务。
        var loaderType = _selectedLoaderType;
        var javaPath = JavaPathBox.Text;
        var rawIdentifier = (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.RawIdentifier ?? "";
        var installFabricApi = InstallFabricApiCheck.IsChecked == true;
        var installQsl = InstallQslCheck.IsChecked == true;
        var customName = string.IsNullOrWhiteSpace(InstanceNameBox.Text) ? null : InstanceNameBox.Text.Trim();

        try
        {
            var installedId = await DownloadQueueService.Instance.RunAndWaitAsync(
                $"安装 {mcVersion}（{loaderType}）", async (item, ct) =>
                {
                    var queueProgress = item.CreateProgress();
                    IProgress<ProgressInfo> progress = new Progress<ProgressInfo>(p =>
                    {
                        queueProgress.Report(p);
                        if (_overlayClosed) return;
                        ProgressStageText.Text = p.Stage;
                        ProgressDetailText.Text = p.CurrentFile;
                        ProgressBarCtl.Maximum = Math.Max(p.Total, 1);
                        ProgressBarCtl.Value = p.Done;
                    });
                    void RegisterTarget(string id) => item.RegisterInstallationTarget(id);
                    _loaderService.InstallationTargetResolved += RegisterTarget;
                    try
                    {
                        string versionId;
                        if (loaderType == ServerCoreType.Vanilla)
                        {
                            // 原版：从缓存的 manifest 里反查完整条目（含 url/sha1），直接调用
                            // DownloadService.InstallVersionAsync，跟"下载中心-游戏版本"面板走的是
                            // 完全同一套下载路径，行为、限速、多线程设置都一致。
                            var entry = _versionManifest?.Versions.FirstOrDefault(v => v.Id == mcVersion);
                            if (entry == null)
                            {
                                throw new InvalidOperationException("找不到该版本的清单信息，请重新打开该窗口再试一次。");
                            }
                            await _vanillaDownloader.InstallVersionAsync(minecraftDir, entry, progress, ct);
                            versionId = entry.Id;

                            // 原版默认直接落在 versions/{mcVersion}/ 下（entry.Id 就是 mcVersion），跟
                            // Fabric/Quilt/Forge/NeoForge 不同的是它没有独立的"加载器安装"步骤可以提前
                            // 指定目标目录名，只能在装完之后原地改名。改名逻辑复用 ClientLoaderInstallService
                            // 里 Forge/NeoForge 用的同一个重命名 helper（改成 internal 供这里调用），
                            // 保证"物理文件夹名 + json 内部 id + 主 jar/json 文件名"三者一起同步更新，
                            // 不会出现改完名字之后启动器反而找不到文件的问题。
                            if (!string.IsNullOrWhiteSpace(customName))
                            {
                                var renamed = ClientLoaderInstallService.TryRenameInstalledInstance(
                                    minecraftDir, Path.Combine(minecraftDir, "versions", versionId), customName);
                                if (renamed != null) versionId = renamed;
                                // 失败就沿用默认的 mcVersion 作为目录名，不影响原版本身已经装好该事实。
                            }
                        }
                        else if (loaderType == ServerCoreType.Fabric)
                        {
                            versionId = await _loaderService.InstallFabricClientAsync(
                                minecraftDir, mcVersion, buildVersion!, progress, ct,
                                installFabricApi: installFabricApi,
                                customInstanceName: customName);
                        }
                        else if (loaderType == ServerCoreType.LegacyFabric)
                        {
                            versionId = await _loaderService.InstallLegacyFabricClientAsync(
                                minecraftDir, mcVersion, buildVersion!, progress, ct, customInstanceName: customName);
                        }
                        else if (loaderType == ServerCoreType.Quilt)
                        {
                            // Quilt 走独立的 InstallQuiltClientAsync，不能落到下面 Forge/NeoForge 那个
                            // else 分支——那个分支调的是 InstallForgeOrNeoForgeClientAsync，会对 Quilt
                            // 走本地跑安装器那一套逻辑，Quilt 根本没有安装器 jar，会直接报错。
                            versionId = await _loaderService.InstallQuiltClientAsync(
                                minecraftDir, mcVersion, buildVersion!, progress, ct,
                                installQsl: installQsl,
                                customInstanceName: customName);
                        }
                        else if (loaderType == ServerCoreType.OptiFine)
                        {
                            var raw = rawIdentifier;
                            var parts = raw.Split('|', 2);
                            var ofType = parts.Length > 0 ? parts[0] : "";
                            var ofPatch = parts.Length > 1 ? parts[1] : "";
                            versionId = await _loaderService.InstallOptiFineClientAsync(
                                minecraftDir, mcVersion, ofType, ofPatch, javaPath, progress, ct,
                                customInstanceName: customName);
                        }
                        else
                        {
                            var fullVersion = loaderType == ServerCoreType.NeoForge ? mcVersion : buildVersion!;
                            versionId = await _loaderService.InstallForgeOrNeoForgeClientAsync(
                                minecraftDir, loaderType, fullVersion, javaPath, progress, ct,
                                customInstanceName: customName);
                        }
                        item.RegisterInstallationTarget(versionId);
                        return versionId;
                    }
                    finally
                    {
                        _loaderService.InstallationTargetResolved -= RegisterTarget;
                    }
                }, targetVersionId: loaderType == ServerCoreType.Vanilla ? mcVersion : null,
                targetFolderPath: minecraftDir);

            InstalledVersionId = installedId;
            _owner.RefreshVersionsPageIfActive();
            if (_overlayClosed) return;
            MessageBoxDialog.ShowInfo($"版本「{installedId}」安装完成！\n可以在「已安装版本」列表里选中它。", "成功");
            CloseWith(true);
        }
        catch (OperationCanceledException)
        {
            if (!_overlayClosed) ToastService.ShowInfo("安装已取消，已校验的完整文件会保留。");
        }
        catch (Exception ex)
        {
            if (_overlayClosed) return;
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Installation_Failed_This_Could_Be_A_Netw", "安装失败，可能是网络连接问题、下载源暂时不可用，或安装文件已损坏，请检查网络后重试。"), $"[安装失败] {ex}", "安装失败");
        }
        finally
        {
            _isInstalling = false;
            if (_overlayClosed) DisposeInstallServices();
            else
            {
                InstallBtn.IsEnabled = true;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
