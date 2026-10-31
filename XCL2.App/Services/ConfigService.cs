using System.IO;
using System.Linq;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 负责 %APPDATA%\XCL2\config.json（全局配置主副本）、启动器目录 json/config.json（镜像）
/// 与 %APPDATA%\XCL2\accounts.json（账户资料）的读写。只保存用户名、UUID 等资料；
/// 认证令牌仅在本次运行内保留，重启后需要重新登录。
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly object SettingsBackupIoGate = new();
    private readonly object _saveGate = new();
    private string _previousLocalJson = "{}", _previousDiskJson = "{}";
    private bool _recoverInvalidPrimary;

    /// <summary>配置主副本：%APPDATA%\XCL2\config.json。所有保存先写这里。</summary>
    public string ConfigPath { get; }
    /// <summary>便携镜像：启动器目录\XCL2\Json\config.json。仅作镜像/人工恢复，不作为正常写入主目标。</summary>
    public string MirrorConfigPath { get; }
    /// <summary>旧版便携镜像路径（启动器目录\json\config.json），只用于一次性迁移读取，之后不再写入。</summary>
    public string LegacyMirrorConfigPath { get; }
    /// <summary>旧版配置路径（启动器目录\xcl2\config.json），只用于首次迁移/兜底读取。</summary>
    public string LegacyConfigPath { get; }
    public string AccountsPath { get; }

    public AppConfig Config { get; private set; } = new();
    public List<Account> Accounts { get; private set; } = new();

    /// <summary>
    /// 修复一处编译期就会报错的引用："App.xaml.cs 里给 ButtonBase 挂的高性能模式按压
    /// 微交互类处理器直接写了 ConfigService.Config.EnableHighPerformanceMode"——但
    /// Config 从来都只是实例属性，从没有对应的静态版本，那两行代码实际上无法通过编译。
    /// 这里补一个"当前生效配置"的静态入口：每次 new ConfigService() 都把自己记成
    /// 最新的 Active（应用里同一时刻只有一个真正代表"当前配置"的实例——MainWindow
    /// 构造时创建的那个，是最后一个 new 出来的，会覆盖 App.xaml.cs 启动阶段临时读取
    /// 配置用的 earlyConfig，正好符合"启动流程里临时读一次 vs 全程代表当前配置"这两种
    /// 用途各自的生命周期），不需要额外接线，任何持有静态引用、拿不到 MainWindow 实例
    /// 的地方（比如这里说的全局类处理器）都可以用 ConfigService.Active?.Config 读取。
    /// </summary>
    public static ConfigService? Active { get; private set; }

    /// <summary>
    /// 访客模式下本次会话的临时账户，只存在于内存中，不属于 <see cref="Accounts"/> 列表，
    /// 也从不写入 accounts.json。由 MainWindow 在检测到 cfg.GuestModeEnabled 时创建并赋值。
    /// GetSelectedAccount 会优先返回该账户（访客模式开启时），实现"访客模式下启动游戏
    /// 永远用该临时账户，不会用到真实保存的账户"。
    /// </summary>
    public Account? GuestAccount { get; set; }

    public ConfigService()
    {
        Active = this;

        // 2.2.9 起把详细全局设置的主副本放到当前 Windows 用户的 AppData：安装目录/便携目录
        // 即使只读、换盘或被替换，用户设置仍能稳定保留。启动器目录下 json/config.json 只做
        // 人工可见的镜像；读取优先级永远是 AppData > 便携镜像 > 旧 xcl2/config.json。
        var roamingConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2");
        Directory.CreateDirectory(roamingConfigDir);
        ConfigPath = Path.Combine(roamingConfigDir, "config.json");
        // 2.3 起把便携镜像收进 "XCL2/Json/config.json"（而不是散落在启动器目录下的 "json/config.json"），
        // 让启动器根目录更紧凑。旧镜像路径仍保留在 LegacyMirrorConfigPath 里，仅用于一次性迁移读取。
        MirrorConfigPath = Path.Combine(AppContext.BaseDirectory, "XCL2", "Json", "config.json");
        LegacyMirrorConfigPath = Path.Combine(AppContext.BaseDirectory, "json", "config.json");
        LegacyConfigPath = Path.Combine(App.DataDir, "config.json");

        // “appd”要求存的是路径字符串，因此不能用 REG_DWORD（DWORD 只能放数字）；这里按用户
        // 所说“32 位”落在 HKCU 的 32-bit registry view，并用 REG_SZ 保存 AppData config 路径。
        RegistryConfigService.SetAppDataConfigPath(ConfigPath);

        // 首次升级时把旧配置迁到 AppData；如果旧路径不存在但便携镜像存在，也可从镜像恢复。
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var seed = File.Exists(LegacyConfigPath) ? LegacyConfigPath
                    : File.Exists(MirrorConfigPath) ? MirrorConfigPath
                    : File.Exists(LegacyMirrorConfigPath) ? LegacyMirrorConfigPath
                    : null;
                if (seed != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                    File.Copy(seed, ConfigPath, overwrite: false);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("迁移 AppData 配置失败，将在 Load 时继续使用兜底副本", ex);
        }

        // 修复"登录好的账户，在另一个文件夹启动时就不会显示了"：
        // App.DataDir 是 AppContext.BaseDirectory（启动器自身 exe 所在目录）下的 "xcl2" 子目录，
        // 这是刻意的"便携版"设计——游戏文件、下载缓存都应该跟着 exe 走，拷到哪个文件夹/哪个盘
        // 都能带着一起用。但账户登录状态不应该跟着这条"便携"规则走：同一个人常见的用法是把
        // 启动器复制到好几个不同的文件夹（比如给不同的整合包/存档各建一份），如果账户缓存也
        // 存在 exe 旁边，每一份拷贝都会各自读到一个空的 accounts.json，表现就是"明明登录过，
        // 换个文件夹打开启动器账户又不见了"，还得重新登录一遍微软账户。
        //
        // 账户资料放在当前用户的固定路径；仅迁移用户名/UUID 等资料，
        // 不复制旧文件中的令牌，也不恢复旧 DPAPI 凭据。
        var roamingAccountsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2");
        Directory.CreateDirectory(roamingAccountsDir);
        AccountsPath = Path.Combine(roamingAccountsDir, "accounts.json");

        var legacyAccountsPath = Path.Combine(App.DataDir, "accounts.json");
        try
        {
            if (!string.Equals(Path.GetFullPath(legacyAccountsPath), Path.GetFullPath(AccountsPath), StringComparison.OrdinalIgnoreCase))
            {
                AccountSecretStore.RemoveLegacyVault(legacyAccountsPath);
                if (File.Exists(legacyAccountsPath))
                {
                    var legacyAccounts = AccountSecretStore.Load(legacyAccountsPath, JsonOpts);
                    if (!File.Exists(AccountsPath))
                    {
                        AccountSecretStore.Save(AccountsPath, legacyAccounts, JsonOpts);
                        File.Delete(legacyAccountsPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("清理旧账户凭据失败，请检查旧账户目录的写入权限。", ex);
        }
    }

    /// <summary>
    /// 「注册表功能」总开关：关闭后，Load/Save 完全跳过注册表读写，只用 config.json，
    /// 行为等同于该功能上线前的老版本。默认开启。见设置页"关闭注册表功能"。
    /// 该开关本身也存在 config.json 里（不放注册表——关掉注册表功能这件事本身
    /// 显然不能又依赖注册表才能记住）。
    /// </summary>
    public bool RegistryFeatureEnabled { get; private set; } = true;

    public void Load()
    {
        // 主副本损坏/不存在时才读取镜像；不会因为镜像时间更新就覆盖 AppData，
        // 从根本上保证“AppData 优先、运行目录仅镜像”。
        var loadCandidates = new[] { ConfigPath, MirrorConfigPath, LegacyMirrorConfigPath, LegacyConfigPath }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();
        Exception? lastConfigReadError = null;
        string? loadedFrom = null;
        string? loadedConfigJson = null;
        foreach (var candidate in loadCandidates)
        {
            try
            {
                var json = File.ReadAllText(candidate);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("配置主内容必须是 JSON 对象。");
                Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                loadedConfigJson = json;
                loadedFrom = candidate;
                break;
            }
            catch (Exception ex)
            {
                lastConfigReadError = ex;
                ErrorPresenter.LogFallback($"读取配置副本失败，尝试下一份：{candidate}", ex);
            }
        }
        if (loadedFrom == null)
        {
            Config = new AppConfig();
            if (lastConfigReadError != null)
                ErrorPresenter.LogFallback("所有配置副本均无法读取，已使用默认配置", lastConfigReadError);
        }

        _previousLocalJson = JsonSerializer.Serialize(Config, JsonOpts);
        _previousDiskJson = loadedFrom == ConfigPath ? loadedConfigJson! : _previousLocalJson;
        _recoverInvalidPrimary = loadedFrom != ConfigPath && lastConfigReadError != null;
        var inputModeMigrated = false;
        var aiRequestTimeoutMigrated = false;

        // ===== 一次性迁移：把"每次启动前询问操作模式"抬成默认开启 =====
        // 问题现象：用户反馈"每次使用时并没有弹出窗口主动询问"。根因不在弹窗代码，而在这里——
        // 旧版本 AskInputModeBeforeLaunch 的默认值是 false，只要启动器保存过一次配置，
        // config.json 里就固化了 false；后来把 C# 属性初始值改成 true 也没有任何作用，
        // 因为反序列化会拿磁盘上的 false 覆盖掉初始值。结果就是升级之后照样一次都不弹。
        // 这里用一个独立的迁移标记做且只做一次抬升：标记为 false 说明这份配置还是旧默认值
        // 时代写出来的，把询问开关打开；标记置位之后就完全听用户的，用户在设置里关掉之后
        // 不会被再次强行打开（迁移只跑一次，这是它和"每次启动都强制置 true"的关键区别）。
        if (!Config.InputModeAskDefaultMigrated)
        {
            Config.InputModeAskDefaultMigrated = true;
            Config.AskInputModeBeforeLaunch = true;
            inputModeMigrated = true; // 真正落盘放到 Load 末尾，避免在配置还没规范化完就写出去
        }

        RegistryFeatureEnabled = Config.RegistryFeatureEnabled;

        // AppData JSON 是主存储：只要任意 JSON 副本成功加载，就绝不再用注册表覆盖它。
        // 注册表中的同步字段现在仅作为“完全没有可读 JSON 时”的灾难恢复来源；正常保存时仍会
        // 写一份注册表镜像以兼容旧版。这样能严格满足“AppData 优先、运行目录/注册表仅镜像”。
        if (RegistryFeatureEnabled && loadedFrom == null)
            RegistrySyncedFields.LoadFromRegistry(Config);

        // 新动态策略不迁移/覆盖旧手动值或开关；只规范化动态字段的非法配置。
        MemoryOptimizerService.NormalizeConfig(Config);

        // 访客模式严格限定为当前进程会话状态。旧版本曾把 GuestModeEnabled 置为 true 并持久化，
        // 导致用户关闭启动器后再次打开仍停在访客模式。无论磁盘/注册表里的旧值是什么，
        // 每次新进程启动都先恢复普通模式；只有本轮会话里再次手动开启才进入访客模式。
        Config.GuestModeEnabled = false;
        GuestAccount = null;

        // 默认如有管理员权限，就把注册表的全局设置（全设备注册表）开启
        if (RegistryConfigService.IsRunningAsAdministrator())
        {
            Config.UseMachineWideRegistry = true;
        }

        // 反序列化后，若 JSON 中某属性显式写了 null（旧版本/手动编辑损坏的配置文件），
        // 该属性会被覆盖为 null 而不会走字段初始值，这里做兜底修复，避免后续 NullReferenceException。
        Config.Folders ??= new List<GameFolder>();
        Config.LastSelectedAccountId ??= "";
        Config.FavoriteVersionIds ??= new List<string>();
        Config.FavoriteItems ??= new List<FavoriteItem>();
        Config.FavoriteItems.RemoveAll(f => f == null);
        Config.VersionIsolationOverrides ??= new Dictionary<string, bool>();
        Config.VersionJavaOverrides ??= new Dictionary<string, int>();
        Config.InstalledJavas ??= new List<InstalledJava>();
        Config.InstalledJavas.RemoveAll(j => j == null); // 清理数组中可能存在的 null 元素

        // 老配置文件迁移：Priority 是新加的字段，旧版本写的 config.json 里没有该属性，
        // 反序列化后全部落到默认值 0，如果不处理，"Java 列表"里所有条目会一起并列最高优先级，
        // 排序在 UI 上/FindJava 匹配时都会变得不确定（Distinct/OrderBy 对相同 Priority 的元素
        // 相对顺序没有强保证）。这里检测到"存在 2 条以上、Priority 都是 0"这种典型的老配置
        // 特征时，按它们在数组里的原始顺序(等于当初注册/添加的先后顺序)重新编号，
        // 保持"老配置升级后，自动匹配顺序跟以前感觉一样(先添加的先用)"，不会因为该新字段
        // 突然打乱用户已经在用的 Java 优先级。
        if (Config.InstalledJavas.Count > 1 && Config.InstalledJavas.All(j => j.Priority == 0))
        {
            for (var i = 0; i < Config.InstalledJavas.Count; i++)
                Config.InstalledJavas[i].Priority = i;
        }

        Config.VersionJavaIdOverrides ??= new Dictionary<string, string>();

        // AI 助手旧配置迁移/空值修复。旧版只持久化悬浮球，完整 AI 配置以前每次启动都会丢失；
        // 新版统一存到 AppConfig.AiAssistant，同时保留 AiAssistantFloatingButton 做向后兼容。
        Config.AiAssistant ??= new AiAssistantConfig();
        if (!Config.AiRequestTimeout86400Migrated)
        {
            Config.AiAssistant.RequestTimeoutSeconds = 86400;
            Config.AiRequestTimeout86400Migrated = true;
            aiRequestTimeoutMigrated = true;
        }
        Config.AiAssistant.CustomModels ??= new List<AiModelDefinition>();
        Config.AiAssistant.CustomModels.RemoveAll(m => m == null || string.IsNullOrWhiteSpace(m.Id));
        Config.AiAssistant.ModelTierOverrides ??= new Dictionary<string, AiModelTier>();
        Config.AiAssistant.NormalizeToolActionLimit();
        AiShellPolicy.NormalizeGrantedShellPermission(Config.AiAssistant);
        AiEndpointRouting.NormalizeConfig(Config.AiAssistant);
        if (Config.AiAssistantFloatingButton && !Config.AiAssistant.ShowFloatingButton)
            Config.AiAssistant.ShowFloatingButton = true;
        if (Config.AiAssistant.RoutingMode == AiRoutingMode.Auto && !Config.AiAssistant.AutoModelRouting)
            Config.AiAssistant.RoutingMode = AiRoutingMode.SpecificModel;
        Config.AiAssistant.AutoModelRouting = Config.AiAssistant.RoutingMode == AiRoutingMode.Auto;

        // 老配置文件迁移：只要有 FavoriteVersionIds 里的旧版本收藏、并且还没搬进
        // FavoriteItems（避免每次启动重复搬运出现重复项），就补一条 Type=Version 的记录。
        // 迁移后不清空 FavoriteVersionIds（万一用户还开着旧版本启动器共用一份配置文件，
        // 保留旧字段能让旧版本继续正常读到收藏；新版本此后只读写 FavoriteItems）。
        foreach (var versionId in Config.FavoriteVersionIds)
        {
            if (!Config.FavoriteItems.Any(f => f.MatchesKey(FavoriteItemType.Version, versionId, ModSource.Combined)))
            {
                Config.FavoriteItems.Add(new FavoriteItem
                {
                    Type = FavoriteItemType.Version,
                    Source = ModSource.Combined,
                    SourceId = versionId
                });
            }
        }

        try
        {
            AccountSecretStore.RemoveLegacyVault(AccountsPath);
            if (File.Exists(AccountsPath))
            {
                Accounts = AccountSecretStore.Load(AccountsPath, JsonOpts);
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"读取账户缓存失败，已重置为空账户列表：{AccountsPath}", ex);
            Accounts = new List<Account>();
        }

        Accounts ??= new List<Account>();
        Accounts.RemoveAll(a => a == null); // 清理数组中可能存在的 null 元素

        EnsureDefaultFolder();

        // 如果本次是从便携镜像/旧路径恢复出来的，立即把规范化后的配置落到 AppData 主副本，
        // 后续启动便始终从主副本读取。EnsureDefaultFolder 可能已经触发过 Save，因此先检查文件。
        if (!string.Equals(loadedFrom, ConfigPath, StringComparison.OrdinalIgnoreCase) && loadedFrom != null)
        {
            // 即使 AppData 主文件“存在但已损坏”，也要用刚刚成功读取的镜像/旧配置修复它；
            // 不能只因为 File.Exists=true 就一直让以后每次启动都先撞一次坏主文件再回退。
            try
            {
                Save();
                inputModeMigrated = false;
                aiRequestTimeoutMigrated = false;
            }
            catch (Exception ex) { ErrorPresenter.LogFallback($"恢复配置到 AppData 主副本失败：{ConfigPath}", ex); }
        }

        // 把上面那次一次性迁移的结果落盘。写失败也不影响本次会话（本次内存里已经应用迁移），
        // 只是下次启动会再迁移一遍，行为仍然正确。
        if (inputModeMigrated || aiRequestTimeoutMigrated)
        {
            try { Save(); }
            catch (Exception ex) { ErrorPresenter.LogFallback("保存一次性设置迁移结果失败", ex); }
        }
    }

    /// <summary>
    /// 确保至少存在一个默认 .minecraft 目录：启动器运行目录根下的 .minecraft 文件夹
    /// （即"官启文件夹以及运行 exe 的文件根目录下"的要求）。
    /// </summary>
    private void EnsureDefaultFolder()
    {
        if (Config.Folders.Count == 0)
        {
            var defaultPath = Path.Combine(AppContext.BaseDirectory, ".minecraft");
            Directory.CreateDirectory(defaultPath);
            var folder = new GameFolder { Name = "当前文件夹", Path = defaultPath, IsDefault = true };
            Config.Folders.Add(folder);
            Config.SelectedFolderPath = defaultPath;
            Save();
        }
    }

    public event Action? Saved;
    public event Action? AccountsSaved;

    public void Save()
    {
        lock (_saveGate) SaveCore();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try
            {
                if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                    dispatcher.BeginInvoke(new Action(RaiseSaved));
            }
            catch (Exception ex) { ErrorPresenter.LogFallback("配置已保存，但无法调度设置刷新通知", ex); }
        }
        else RaiseSaved();
    }

    private void RaiseSaved()
    {
        // Saved is an observer of an already committed write. A broken view/plugin cannot
        // turn that success into a caller rollback or prevent the other views from refreshing.
        foreach (var handler in Saved?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action)handler)(); }
            catch (Exception ex) { ErrorPresenter.LogFallback("配置已保存，但一个设置刷新订阅执行失败", ex); }
    }

    private void SaveCore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        Config.AiAssistant ??= new AiAssistantConfig();
        Config.AiAssistant.NormalizeToolActionLimit();
        AiShellPolicy.NormalizeGrantedShellPermission(Config.AiAssistant);
        AiEndpointRouting.NormalizeConfig(Config.AiAssistant);
        Config.AiAssistant.AutoModelRouting = Config.AiAssistant.RoutingMode == AiRoutingMode.Auto;
        Config.AiAssistantFloatingButton = Config.AiAssistant.ShowFloatingButton;

        // GuestModeEnabled 是会话态，不是持久设置。保存其它配置时临时把它置为 false，
        // 所有持久副本都明确记录“下次普通模式”。
        var sessionGuestMode = Config.GuestModeEnabled;
        try
        {
            Config.GuestModeEnabled = false;
            var json = JsonSerializer.Serialize(Config, JsonOpts);

            // 主副本先写；只有主副本成功后才更新便携镜像，避免镜像比主副本“领先”。
            AppConfig? mergedConfig = null;
            var mergedJson = ConfigMergeStore.Commit(ConfigPath, json, _previousLocalJson, _previousDiskJson, _recoverInvalidPrimary,
                publishMirror: merged =>
                {
                    try { WriteTextAtomically(MirrorConfigPath, merged); }
                    catch (Exception ex) { ErrorPresenter.LogFallback($"更新便携配置镜像失败：{MirrorConfigPath}", ex); }
                    // Registry is another mirror. Publish it before releasing the same cross-process
                    // gate, using the committed global toggle rather than this instance's stale value.
                    RegistryFeatureEnabled = mergedConfig!.RegistryFeatureEnabled;
                    if (RegistryFeatureEnabled)
                    {
                        RegistryConfigService.SetAppDataConfigPath(ConfigPath);
                        RegistrySyncedFields.SaveToRegistry(mergedConfig);
                    }
                },
                onMirrorError: ex => ErrorPresenter.LogFallback("主配置已保存，但发布配置镜像失败", ex),
                validate: merged => mergedConfig = JsonSerializer.Deserialize<AppConfig>(merged)
                    ?? throw new JsonException("合并后的配置无法反序列化。"));
            _previousLocalJson = json;
            _previousDiskJson = mergedJson;
            _recoverInvalidPrimary = false;
            RegistryFeatureEnabled = mergedConfig!.RegistryFeatureEnabled;
        }
        finally
        {
            Config.GuestModeEnabled = sessionGuestMode;
        }
    }

    private static void WriteTextAtomically(string path, string content)
    {
        ConfigMergeStore.WriteAtomically(path, content);
    }

    /// <summary>每次进入设置页先备份当前 AppData 主配置。仅保留最近 20 份，防止无限增长。</summary>
    public string CreateSettingsOpenBackup()
    {
        return WriteSettingsOpenBackup(CreateSettingsBackupJson());
    }

    /// <summary>调用线程仅捕获配置快照；目录创建、写文件与备份清理放在后台。</summary>
    public Task<(string BackupPath, bool ShowRestoreTip)> CreateSettingsOpenBackupAsync()
    {
        var snapshot = CreateSettingsBackupJson();
        return Task.Run(() =>
        {
            lock (SettingsBackupIoGate)
                return (WriteSettingsOpenBackup(snapshot), RecordSettingsPageOpenAndShouldShowRestoreTip());
        });
    }

    private string CreateSettingsBackupJson()
    {
        var sessionGuestMode = Config.GuestModeEnabled;
        try
        {
            Config.GuestModeEnabled = false;
            return JsonSerializer.Serialize(Config, JsonOpts);
        }
        finally { Config.GuestModeEnabled = sessionGuestMode; }
    }

    private string WriteSettingsOpenBackup(string snapshot)
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "backups", "settings");
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, $"config-settings-open-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(backupPath, snapshot);

        try
        {
            foreach (var old in new DirectoryInfo(backupDir).GetFiles("config-settings-open-*.json")
                         .OrderByDescending(f => f.CreationTimeUtc).Skip(20))
                old.Delete();
        }
        catch { /* 清理旧备份失败不影响本次备份 */ }

        return backupPath;
    }

    /// <summary>记录设置页打开次数；只在第二次打开时返回 true，用于一次性的恢复提示。</summary>
    public bool RecordSettingsPageOpenAndShouldShowRestoreTip()
    {
        var statePath = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "settings-page-open-count.txt");
        var count = 0;
        try
        {
            if (File.Exists(statePath)) int.TryParse(File.ReadAllText(statePath), out count);
            count = Math.Min(count + 1, 1000000);
            File.WriteAllText(statePath, count.ToString());
        }
        catch { return false; }
        return count == 2;
    }

    /// <summary>
    /// 把 snapshot 里每一个可读写属性的值逐个复制到当前 Config 实例上（跟 PatchDefaults
    /// 用的是同一套反射遍历套路），而不是直接把 Config 属性整个替换成 snapshot 引用——
    /// Config 是 { get; private set; }，且项目里不少地方持有 Config 的引用长期使用，
    /// 直接换引用会导致那些地方还拿着旧对象。用于设置页"自动保存后点击回退"场景，
    /// 把配置整体还原到自动保存前的那一份 JSON 快照，见 SettingsPage.RollbackAutoSave。
    /// </summary>
    public void ReplaceConfigFieldsFrom(AppConfig snapshot)
    {
        foreach (var prop in typeof(AppConfig).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            prop.SetValue(Config, prop.GetValue(snapshot));
        }
    }

    public int PatchDefaults()
    {
        var fresh = new AppConfig();
        var patchedCount = 0;

        foreach (var prop in typeof(AppConfig).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var currentValue = prop.GetValue(Config);

            if (currentValue is null)
            {
                var freshValue = prop.GetValue(fresh);
                if (freshValue is null) continue;
                prop.SetValue(Config, freshValue);
                patchedCount++;
                continue;
            }

            if (PatchableScalarDefaults.TryGetValue(prop.Name, out var typeDefault) &&
                Equals(currentValue, typeDefault))
            {
                var freshValue = prop.GetValue(fresh);
                if (!Equals(freshValue, typeDefault))
                {
                    prop.SetValue(Config, freshValue);
                    patchedCount++;
                }
            }
        }

        if (patchedCount > 0) Save();
        return patchedCount;
    }

    /// <summary>
    /// <see cref="PatchDefaults"/> 使用的标量字段白名单：只有列在这里的字段，当它当前值
    /// 等于对应的"未设置占位值"时，才会被新默认值覆盖。不在该字典里的标量字段（尤其是
    /// 那些 0/false 本身就是用户可能主动选择的合法值的字段）一律跳过，绝不触碰。
    /// </summary>
    private static readonly Dictionary<string, object> PatchableScalarDefaults = new()
    {
        [nameof(AppConfig.PreferredJavaMajorVersion)] = 0,
        [nameof(AppConfig.MinMemoryMb)] = 0,
        [nameof(AppConfig.MaxMemoryMb)] = 0,
        [nameof(AppConfig.WindowWidth)] = 0,
        [nameof(AppConfig.WindowHeight)] = 0,
        [nameof(AppConfig.MaxDownloadThreads)] = 0,
        [nameof(AppConfig.MemoryOptimizationReserveMb)] = 0,
        [nameof(AppConfig.AutoThemeLightStartHour)] = 0,
        [nameof(AppConfig.AutoThemeDarkStartHour)] = 0,
    };

    public void SaveAccounts()
    {
        Directory.CreateDirectory(App.DataDir);
        AccountSecretStore.Save(AccountsPath, Accounts, JsonOpts);
        AccountsSaved?.Invoke();
    }

    /// <summary>
    /// 把一个 javaw.exe 路径登记进"Java 列表"(<see cref="AppConfig.InstalledJavas"/>)。
    /// 路径已存在(不区分大小写)则直接返回原有记录，不产生重复项；否则新建一条并自动生成一个
    /// 默认名字(如 "Java 21")，需要外部调用方自己 Save()。不在这里探测版本号——调用方通常
    /// 已经拿到了版本号(下载/扫描时都会附带)，避免重复调用一次外部进程。
    /// </summary>
    public InstalledJava RegisterJava(string javawPath, int? majorVersion, string source)
    {
        var existing = Config.InstalledJavas.FirstOrDefault(
            j => string.Equals(j.JavawPath, javawPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // 已存在：如果这次拿到了版本号而之前没有，顺便补全一下，不新建重复项。
            if (majorVersion is > 0 && existing.MajorVersion is null or 0)
                existing.MajorVersion = majorVersion;
            return existing;
        }

        var name = majorVersion is > 0 ? $"Java {majorVersion}" : "Java (版本未知)";
        // 避免同名：同名已存在时加个序号后缀，方便用户在下拉框里区分。
        if (Config.InstalledJavas.Any(j => j.Name == name))
        {
            var i = 2;
            while (Config.InstalledJavas.Any(j => j.Name == $"{name} ({i})")) i++;
            name = $"{name} ({i})";
        }

        var entry = new InstalledJava
        {
            Name = name,
            JavawPath = javawPath,
            MajorVersion = majorVersion,
            Source = source,
            // 新记录追加到优先级列表末尾(取当前最大 Priority + 1)，不打乱已有条目的顺序；
            // 列表为空时从 0 开始。
            Priority = Config.InstalledJavas.Count == 0 ? 0 : Config.InstalledJavas.Max(j => j.Priority) + 1
        };
        Config.InstalledJavas.Add(entry);
        return entry;
    }

    /// <summary>把 Java 列表按 Priority 升序排列（数值越小越靠前=优先级越高），
    /// UI 展示和 FindJava 自动匹配都用该顺序，保证"列表看到的顺序"就是"实际匹配顺序"。</summary>
    public List<InstalledJava> GetJavaListInPriorityOrder() =>
        Config.InstalledJavas.OrderBy(j => j.Priority).ToList();

    /// <summary>
    /// 上移/下移一条 Java 记录：跟相邻的那一条交换 Priority 值，而不是重新给整个列表编号——
    /// 这样每次移动只影响两条记录，逻辑简单且不会因为中途有记录被删除导致编号出现空洞后
    /// 排序错乱。moveUp=true 表示往列表前面移(优先级提高)，false 表示往后移(优先级降低)。
    /// 已经在最顶/最底时移动无效果(找不到可交换的相邻项)，调用方不需要额外判断边界。
    /// </summary>
    public void MoveJavaPriority(string javaId, bool moveUp)
    {
        var ordered = GetJavaListInPriorityOrder();
        var index = ordered.FindIndex(j => j.Id == javaId);
        if (index < 0) return;

        var swapIndex = moveUp ? index - 1 : index + 1;
        if (swapIndex < 0 || swapIndex >= ordered.Count) return;

        (ordered[index].Priority, ordered[swapIndex].Priority) = (ordered[swapIndex].Priority, ordered[index].Priority);
    }

    /// <summary>按 Id 在 Java 列表里查找一条记录，找不到(比如用户后来手动删除了这条记录)返回 null。</summary>
    public InstalledJava? FindJavaById(string? javaId) =>
        string.IsNullOrEmpty(javaId) ? null : Config.InstalledJavas.FirstOrDefault(j => j.Id == javaId);

    /// <summary>
    /// 解析一个 Java 列表条目实际能不能用：路径文件还存在就返回它的 javaw.exe 路径，
    /// 文件已经被移动/删除则返回 null，调用方应该回退到旧的自动探测逻辑，而不是直接崩溃。
    /// </summary>
    public string? ResolveJavaPath(string? javaId)
    {
        var entry = FindJavaById(javaId);
        if (entry == null || string.IsNullOrEmpty(entry.JavawPath) || !File.Exists(entry.JavawPath))
            return null;
        return entry.JavawPath;
    }

    /// <summary>
    /// 访客模式开启时(cfg.GuestModeEnabled)，永远优先返回 GuestAccount（本次会话的临时账户），
    /// 完全跳过真实保存的账户列表——这是访客模式"不使用/不暴露已保存账户"这一诉求的核心实现点。
    /// 访客模式关闭时行为不变：优先用上次选中记录的账户，其次第一个标记为选中的，最后兜底第一个。
    /// </summary>
    public Account? GetSelectedAccount()
    {
        if (Config.GuestModeEnabled && GuestAccount != null) return GuestAccount;

        return Accounts.FirstOrDefault(a => a.Id == Config.LastSelectedAccountId) ??
               Accounts.FirstOrDefault(a => a.IsSelected) ??
               Accounts.FirstOrDefault();
    }

    public void SelectAccount(string accountId)
    {
        foreach (var a in Accounts) a.IsSelected = a.Id == accountId;
        Config.LastSelectedAccountId = accountId;
        SaveAccounts();
        Save();
    }

    public void AddOrUpdateAccount(Account account)
    {
        // Local profile/skin edits are not an online verification. Only the authentication
        // service stamps LastVerifiedAtUtc after the complete login chain succeeds.
        var existing = AccountUpdatePolicy.FindExisting(Accounts, account);
        if (existing != null)
        {
            account.Id = existing.Id;

            AccountUpdatePolicy.PreserveLocalPreferences(account, existing);
            Accounts.Remove(existing);
        }
        Accounts.Add(account);
        SaveAccounts();
    }

    /// <summary>
    /// 取"最近创建"的账户（按 CreatedAtUtc 排序，不受列表存储顺序/刷新更新影响）。
    /// 供账户选择弹窗默认高亮使用——没有账户时返回 null。
    /// </summary>
    public Account? GetMostRecentlyCreatedAccount() =>
        Accounts.OrderByDescending(a => a.CreatedAtUtc).FirstOrDefault();

    public void RemoveAccount(string accountId)
    {
        Accounts.RemoveAll(a => a.Id == accountId);
        SaveAccounts();
        
        var skinService = new SkinService();
        skinService.RemoveCustomSkin(accountId);
        skinService.RemoveAvatarPhoto(accountId);
    }

    // ===================== 危险操作：需要业务层先做 "xztx127" 二次确认 =====================
    // 三个方法本身不再要求调用方传入确认码——确认码校验是 UI 层的责任（弹窗输入框比对），
    // 这里只提供"确认通过之后，具体做什么"的执行逻辑，保持职责分离：这层不关心确认码
    // 长什么样、从哪个控件读出来，只保证"一旦被调用，就正确地只做该做的那一件事，
    // 绝不波及范围之外的任何东西"。

    /// <summary>关闭注册表功能：只是把开关关掉、保存 config.json，不删除已写入的注册表内容
    /// （已有的注册表项原样留着，万一用户以后又重新打开该开关，还能读回来）。</summary>
    public void DisableRegistryFeature()
    {
        Config.RegistryFeatureEnabled = false;
        RegistryFeatureEnabled = false;
        Save(); // Save() 内部会因为 RegistryFeatureEnabled=false 而跳过写注册表，只落盘 config.json
    }

    /// <summary>删除所有新增的启动器注册表项（HKLM 和 HKCU 下的 SOFTWARE\XCL2 键本身，
    /// 不触碰该键以外的任何注册表内容）。不自动关闭注册表功能开关——删除后如果用户继续
    /// 使用，下次 Save() 只要 RegistryFeatureEnabled 还是 true，就会重新按当前 config.json
    /// 的值把这些键写回去；如果用户是想"彻底不再使用"，应该在删除前先调用
    /// <see cref="DisableRegistryFeature"/>。</summary>
    public (bool HklmDeleted, bool HkcuDeleted) DeleteAllRegistryEntries() =>
        RegistryConfigService.DeleteXcl2Key();

    /// <summary>
    /// 清除 XCL2 在本机存在的痕迹：删除注册表项 + 删除 xcl2 数据目录（config.json、
    /// accounts.json、日志、下载缓存的 Java 运行时、导出的启动脚本等全部内容）。
    ///
    /// **范围硬编码锁死**：只删除 <see cref="App.DataDir"/>（即程序运行目录下的 "xcl2" 文件夹）
    /// 和注册表里的 SOFTWARE\XCL2 键，这两个路径都是编译期常量，不接受任何外部传参覆盖——
    /// 这是"注意界限，不要把此电脑删了"这条要求的直接落实：以前的离谱 bug 就是删除范围
    /// 被做成了可配置/可传参，这里刻意反过来，把范围焊死在代码里，杜绝任何"传错参数就删掉
    /// 不该删的东西"的可能性。**绝不删除 .minecraft 游戏目录、不删除用户任何其它文件**。
    ///
    /// 调用该方法后，进程本身应该立刻退出（config.json 已经被删了，继续跑下去的任何
    /// Save() 调用都会把目录重新建出来，等于清理了个寂寞）——退出逻辑由 UI 层负责。
    /// </summary>
    public void ClearAllTraces()
    {
        RegistryConfigService.DeleteXcl2Key();

        try
        {
            if (Directory.Exists(App.DataDir))
                Directory.Delete(App.DataDir, recursive: true);
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"清除本机痕迹时删除数据目录失败：{App.DataDir}", ex);
            throw; // 向上抛给 UI 层：这一步失败了必须让用户知道，不能假装"清除成功"
        }
    }

    // ===================== 导出 / 导入 =====================

    /// <summary>"导出注册表 (.reg)"：见 <see cref="RegistryConfigService.ExportToRegFileContent"/>。
    /// 两支都没有 XCL2 键时返回 null。</summary>
    public string? ExportRegistryFile() => RegistryConfigService.ExportToRegFileContent();

    /// <summary>
    /// "导出所有配置"：把 xcl2/config.json + 注册表镜像字段的当前值 + 各实例的
    /// versions/&lt;id&gt;/xcl/settings.json（若存在）打包成一份 JSON 归档，
    /// 返回归档文件的完整文本内容，调用方（UI 层）负责写文件/弹保存对话框。
    /// accounts.json 里的 refresh token 属于敏感凭据，默认不打包进去（只导出账户的
    /// 用户名/UUID/账户类型这些非敏感展示信息），避免导出文件被别人拿到后能冒用登录状态。
    /// </summary>
    public string ExportAllConfig()
    {
        var archive = new ConfigArchive
        {
            ExportedAtUtc = DateTime.UtcNow,
            Config = Config,
            RegistrySnapshot = new Dictionary<string, string?>
            {
                ["AgreementsAccepted"] = Config.AgreementsAccepted.ToString(),
                ["AcceptedAgreementVersion"] = Config.AcceptedAgreementVersion.ToString(),
                ["RestrictedMode"] = Config.RestrictedMode.ToString(),
                ["UiSkin"] = Config.UiSkin,
                ["IsDarkMode"] = Config.IsDarkMode.ToString(),
                ["LauncherLanguage"] = Config.LauncherLanguage,
            },
            AccountSummaries = Accounts.Select(a => new AccountSummary
            {
                Id = a.Id,
                Type = a.Type.ToString(),
                Username = a.Username,
                Uuid = a.Uuid
            }).ToList(),
            InstanceSettings = CollectInstanceSettings()
        };
        return JsonSerializer.Serialize(archive, JsonOpts);
    }

    /// <summary>扫描所有已注册文件夹下 versions/*/xcl/settings.json，收集进导出归档。
    /// 找不到/损坏的实例设置文件直接跳过，不影响其它实例正常导出。</summary>
    private Dictionary<string, string> CollectInstanceSettings()
    {
        var result = new Dictionary<string, string>();
        foreach (var folder in Config.Folders)
        {
            var versionsDir = Path.Combine(folder.Path, "versions");
            string[] versionDirs;
            try
            {
                ArchiveImportSafety.CheckLinks(versionsDir);
                if (!Directory.Exists(versionsDir)) continue;
                versionDirs = Directory.GetDirectories(versionsDir);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
            foreach (var versionDir in versionDirs)
            {
                var settingsPath = InstanceConfigService.GetSettingsPath(versionDir);
                try
                {
                    ArchiveImportSafety.CheckLinks(settingsPath);
                    if (!File.Exists(settingsPath) || new FileInfo(settingsPath).Length > ArchiveImportSafety.MaximumManifestBytes) continue;
                    var key = $"{folder.Path}|{Path.GetFileName(versionDir)}";
                    result[key] = File.ReadAllText(settingsPath);
                }
                catch { /* 单个实例设置读取失败不影响整体导出 */ }
            }
        }
        return result;
    }

    /// <summary>
    /// "导入配置"：解析一份由 <see cref="ExportAllConfig"/> 产出的归档 JSON，写回 config.json、
    /// 注册表镜像字段、以及已注册实例的 xcl/settings.json。执行设置、AI 权限、网络来源、
    /// 安全选项和文件位置保留本机值，其余配置随后 Save() 落盘。accounts.json 不受导入影响——
    /// 归档里只有非敏感的账户展示信息，不含可用于登录的凭据，不会、也不能拿它覆盖真实账户列表。
    /// 返回成功导入的实例设置数量，供 UI 层提示。
    /// </summary>
    public int ImportAllConfig(string archiveJson)
    {
        var archive = JsonSerializer.Deserialize<ConfigArchive>(archiveJson)
                      ?? throw new InvalidOperationException("配置归档文件格式无法识别。");

        var registeredRoots = (Config.Folders ?? new()).Where(folder => folder != null).Select(folder => folder.Path).ToArray();
        var restore = new List<(string Path, InstanceSettings Settings)>();
        // Validate the whole archive before changing live settings or writing an instance.
        foreach (var (key, json) in archive.InstanceSettings ?? new())
        {
            var versionDir = ConfigArchivePolicy.ResolveInstanceDirectory(key, registeredRoots);
            if (versionDir == null || !Directory.Exists(versionDir)) continue;
            var settingsPath = InstanceConfigService.GetSettingsPath(versionDir);
            ArchiveImportSafety.CheckLinks(settingsPath);
            if (json == null || json.Length > ArchiveImportSafety.MaximumManifestBytes)
                throw new InvalidDataException("实例配置内容无效或过大。");
            var settings = JsonSerializer.Deserialize<InstanceSettings>(json)
                ?? throw new InvalidDataException("实例配置内容为空。");
            ConfigArchivePolicy.PreserveInstanceAuthority(settings, InstanceConfigService.TryLoad(versionDir));
            restore.Add((settingsPath, settings));
        }

        if (archive.Config != null)
        {
            ConfigArchivePolicy.PreserveLocalAuthority(archive.Config, Config);
            Config = archive.Config;
            Config.Folders ??= new List<GameFolder>();
            Config.InstalledJavas ??= new List<InstalledJava>();
            Config.FavoriteItems ??= new List<FavoriteItem>();
        }

        var restoredInstances = 0;
        foreach (var (settingsPath, settings) in restore)
        {
            try
            {
                ArchiveImportSafety.ExportAtomically(settingsPath,
                    temporary => File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOpts)));
                restoredInstances++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { ErrorPresenter.LogFallback("恢复实例设置失败", ex); }
        }

        Save();
        EnsureDefaultFolder();
        return restoredInstances;
    }
}

/// <summary>"导出所有配置"归档的顶层结构，见 ConfigService.ExportAllConfig。</summary>
public class ConfigArchive
{
    public DateTime ExportedAtUtc { get; set; }
    public Models.AppConfig? Config { get; set; }
    public Dictionary<string, string?> RegistrySnapshot { get; set; } = new();
    public List<AccountSummary> AccountSummaries { get; set; } = new();
    /// <summary>key 格式："&lt;文件夹路径&gt;|&lt;版本ID&gt;"，value 是该实例 xcl/settings.json 原始内容。</summary>
    public Dictionary<string, string> InstanceSettings { get; set; } = new();
}

/// <summary>导出归档里的账户信息——只含非敏感展示字段，不含任何可用于登录的凭据。</summary>
public class AccountSummary
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
}
