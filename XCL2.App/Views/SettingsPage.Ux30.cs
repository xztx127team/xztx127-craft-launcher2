using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    // Only explicit, reversible preference controls participate in reset. Actions, accounts,
    // installed Java records, registry permissions and file lists are intentionally excluded.
    private static readonly (string Control, string Property)[] UxDefaultBindings =
    {
        ("AccountTokenGraceDaysBox", "AccountTokenGracePeriodDays"),
        ("AdvancedModeCheck", "AdvancedMode"),
        ("AlwaysOnTopCheck", "AlwaysOnTop"),
        ("AnimationSpeedSlider", "AnimationSpeedPercent"),
        ("AppFontCombo", "AppFontFamily"),
        ("AskInputModeCheck", "AskInputModeBeforeLaunch"),
        ("AutoStartOnBootCheck", "AutoStartOnBoot"),
        ("AutoThemeDarkStartHourCombo", "AutoThemeDarkStartHour"),
        ("AutoThemeLightStartHourCombo", "AutoThemeLightStartHour"),
        ("BackdropMaterialCombo", "Win11BackdropMaterial"),
        ("BackgroundRotationIntervalBox", "BackgroundRotationIntervalMinutes"),
        ("BackgroundRotationModeCombo", "BackgroundRotationMode"),
        ("BackupOnCloseCheck", "BackupInstanceOnClose"),
        ("BackupOnStartupCheck", "BackupInstanceOnStartup"),
        ("BrightnessSlider", "BrightnessPercent"),
        ("CheckLauncherUpdatesOnStartupCheck", "CheckLauncherUpdatesOnStartup"),
        ("CommunityResourceNoPopupCheck", "CommunityResourceNoPopup"),
        ("CompletelyDisableAnimationsCheck", "CompletelyDisableAnimations"),
        ("ContentFontCombo", "AppFontFamily_Content"),
        ("CustomBackgroundFrostSlider", "CustomBackgroundFrostPercent"),
        ("CustomBackgroundVideoFitModeCombo", "CustomBackgroundVideoFitMode"),
        ("CustomBackgroundVideoSoundCheck", "CustomBackgroundVideoSoundEnabled"),
        ("DefaultJarDropCombo", "DefaultJarDropTarget"),
        ("DownloadPopupDetailCheck", "DownloadPopupShowDetailStats"),
        ("DrawerCustomAppearanceCheck", "DrawerUseCustomAppearance"),
        ("DrawerFrostSlider", "DrawerFrostPercent"),
        ("DrawerOpacitySlider", "DrawerOpacityPercent"),
        ("DrawerTextOpacitySlider", "DrawerTextOpacityPercent"),
        ("DynamicMaxMbBox", "DynamicMemoryMaximumMb"),
        ("DynamicMaxPercentBox", "DynamicMemoryMaximumPercent"),
        ("DynamicMemoryCheck", "EnableMemoryOptimization"),
        ("DynamicMinMbBox", "DynamicMemoryMinimumMb"),
        ("DynamicMinPercentBox", "DynamicMemoryMinimumPercent"),
        ("EnableLauncherUpdatesCheck", "EnableLauncherUpdates"),
        ("EnforceJavaVersionMatchCheck", "EnforceJavaVersionMatch"),
        ("FeatureSearchStyleCombo", "FeatureSearchStyle"),
        ("FixedMemoryMbBox", "FixedMemoryAllocationMb"),
        ("GameConsoleWindowCheck", "EnableGameConsoleWindow"),
        ("GameLanguageCombo", "GameLanguage"),
        ("GameVersionTypeLabelBox", "GameVersionTypeLabel"),
        ("BackgroundRotationFixedTimeBox", "BackgroundRotationFixedTime"),
        ("SkinApiRootBox", "SkinApiRoot"),
        ("GameVersionNoPopupCheck", "GameVersionNoPopup"),
        ("GlobalWindowOpacitySlider", "GlobalWindowOpacityPercent"),
        ("GlobalWindowTransparencyCheck", "EnableGlobalWindowTransparency"),
        ("HeightBox", "WindowHeight"),
        ("HighPerformanceGpuLaunchCheck", "UseHighPerformanceGpuForGame"),
        ("HighPerformanceModeCheck", "EnableHighPerformanceMode"),
        ("InjectionScanCheck", "EnableInjectionScan"),
        ("IsolateResourcePacksCheck", "IsolateResourcePacksByDefault"),
        ("IsolateVersionsCheck", "IsolateVersionsByDefault"),
        ("JavaArchCombo", "PreferredJavaArch"),
        ("JavaInstallModeCombo", "PreferredJavaInstallMode"),
        ("JavaVersionCombo", "PreferredJavaMajorVersion"),
        ("LaunchHealthCheck", "EnableLaunchHealthCheck"),
        ("LaunchHealthModeCombo", "IntegrityCheckMode"),
        ("LifecycleBackupTargetModeCombo", "LifecycleBackupTargetMode"),
        ("LifecycleBackupVersionIdsBox", "LifecycleBackupVersionIds"),
        ("LowPerformanceModeCheck", "LowPerformanceMode"),
        ("MaxMemBox", "MaxMemoryMb"),
        ("MemoryAllocationModeCombo", "MemoryAllocationMode"),
        ("MemoryDisplayBasisCombo", "MemoryDisplayBasis"),
        ("MinMemBox", "MinMemoryMb"),
        ("ModFileNamingStyleCombo", "ModFileNamingStyle"),
        ("ModSnapshotCheck", "SnapshotBeforeModUpdates"),
        ("ModSnapshotCountSlider", "ModUpdateSnapshotRetentionCount"),
        ("ModSnapshotSpaceSlider", "ModUpdateSnapshotMaxMb"),
        ("ModpackDropNewInstanceCheck", "ModpackDropCreatesNewInstance"),
        ("ModpackNoPopupCheck", "ModpackNoPopup"),
        ("MouseWheelSensitivitySlider", "MouseWheelSensitivityPercent"),
        ("MultiThreadDownloadCheck", "EnableMultiThreadDownload"),
        ("NoAnimationOptimizationCombo", "NoAnimationOptimization"),
        ("PageAnimationsCheck", "EnablePageAnimations"),
        ("PerformanceOverlayColorCombo", "PerformanceOverlayColorScheme"),
        ("PerformanceOverlayEnabledCheck", "PerformanceOverlayEnabled"),
        ("PerformanceOverlayFontSlider", "PerformanceOverlayFontSize"),
        ("PerformanceOverlayGameCpuCheck", "PerformanceOverlayShowGameCpu"),
        ("PerformanceOverlayGameMemoryCheck", "PerformanceOverlayShowGameMemory"),
        ("PerformanceOverlayLauncherCheck", "PerformanceOverlayShowLauncher"),
        ("PerformanceOverlayLockCheck", "PerformanceOverlayLockPosition"),
        ("PerformanceOverlayMemoryUnitCombo", "PerformanceOverlayMemoryUnit"),
        ("PerformanceOverlayOpacitySlider", "PerformanceOverlayOpacityPercent"),
        ("PerformanceOverlayPidCheck", "PerformanceOverlayShowProcessId"),
        ("PerformanceOverlayRefreshCombo", "PerformanceOverlayRefreshMilliseconds"),
        ("PerformanceOverlayStyleCombo", "PerformanceOverlayStyle"),
        ("PerformanceOverlaySystemCpuCheck", "PerformanceOverlayShowSystemCpu"),
        ("PerformanceOverlaySystemMemoryCheck", "PerformanceOverlayShowSystemMemory"),
        ("PerformanceOverlayTopmostCheck", "PerformanceOverlayAlwaysOnTop"),
        ("PopupCustomAppearanceCheck", "PopupUseCustomAppearance"),
        ("PopupFrostSlider", "PopupFrostPercent"),
        ("PopupOpacitySlider", "PopupOpacityPercent"),
        ("PopupTextOpacitySlider", "PopupTextOpacityPercent"),
        ("RememberDownloadsCheck", "RememberInterruptedDownloads"),
        ("ReminderBadgeCheck", "ShowReminderBadge"),
        ("ScheduledBackupCheck", "ScheduledInstanceBackupEnabled"),
        ("ScheduledBackupIntervalBox", "ScheduledInstanceBackupIntervalHours"),
        ("ScheduledBackupRetentionBox", "ScheduledInstanceBackupRetentionCount"),
        ("ServerJarDropCombo", "ServerPageJarDropTarget"),
        ("SettingsAutoSaveCheck", "SettingsAutoSaveWithoutConfirm"),
        ("ShowFeatureSearchCheck", "ShowFeatureSearch"),
        ("ShowModIconsCheck", "ShowModIcons"),
        ("ShowServerNetworkGuideCheck", "ShowServerNetworkGuideOnStart"),
        ("ShowUpdateChangelogPopupCheck", "ShowUpdateChangelogPopup"),
        ("SidebarFontCombo", "AppFontFamily_Sidebar"),
        ("SimpleJavaVersionCombo", "PreferredJavaMajorVersion"),
        ("SmartThrottleCheck", "SmartBandwidthThrottle"),
        ("SpeedLimitBox", "DownloadSpeedLimitKBps"),
        ("TextOpacitySlider", "TextOpacityPercent"),
        ("ThreadCountBox", "MaxDownloadThreads"),
        ("TitleBarFontCombo", "AppFontFamily_TitleBar"),
        ("TouchModeCheck", "TouchModeEnabled"),
        ("TouchOpacityBox", "TouchOverlayOpacityPercent"),
        ("TouchScaleBox", "TouchOverlayButtonScale"),
        ("TouchSensitivityBox", "TouchOverlayLookSensitivity"),
        ("UiSkinCombo", "UiSkin"),
        ("UiSkinRotationModeCombo", "UiSkinRotationMode"),
        ("UiZoomEnabledCheck", "EnableUiZoomShortcut"),
        ("UiZoomSlider", "UiZoomPercent"),
        ("WidthBox", "WindowWidth"),
        ("Win11EffectsCheck", "EnableWin11VisualEffects"),
        ("NativeApprovalsCheck", "NativeBackgroundApprovals"),
        ("WinUi3DesignCheck", "EnableWinUi3Design"),
        ("WindowAnimationsCheck", "EnableUiAnimations"),
        ("WindowOpacitySlider", "WindowOpacityPercent"),
        ("WindowTransparencyCheck", "EnableWindowTransparency"),
        ("ZipDropDefaultCombo", "ZipDropDefault"),
        ("UxRecentHistoryCheck", "EnableRecentInstanceHistory"),
        ("UxRecentToolsCheck", "EnableRecentToolHistory"),
        ("UxRememberBrowseCheck", "RememberInstanceBrowsePosition"),
        ("UxRememberResultsCheck", "RememberTaskResults"),
        ("UxHideServerAddressesCheck", "HideServerAddresses"),
        ("UxRememberWindowCheck", "RememberWindowPlacement"),
        ("UxFollowReducedMotionCheck", "FollowSystemReducedMotion"),
        ("UxTimeFormatCombo", "PreferredTimeFormat"),
        ("UxCapacityUnitCombo", "PreferredCapacityUnit"),
        ("UxExactCapacityCheck", "ShowExactCapacityValues"),
    };

    private sealed record UxResetEntry(FrameworkElement Control, string Label, string Group, Action Reset);
    private sealed record UxSettingValue(string Label, string Value, string Display);
    private readonly Dictionary<string, UxResetEntry> _uxResetEntries = new(StringComparer.Ordinal);
    private Dictionary<string, UxSettingValue> _uxSavedValues = new(StringComparer.Ordinal);
    private List<string> _uxLastSavedChanges = new();
    private bool _uxSavingSettings;
    private bool _uxResetInProgress;
    private bool _uxSettingsReady;
    private bool _uxSummaryIsLatestSave;

    private void InitializeUxOptions(AppConfig cfg)
    {
        UxRecentHistoryCheck.IsChecked = cfg.EnableRecentInstanceHistory;
        UxRecentToolsCheck.IsChecked = cfg.EnableRecentToolHistory;
        UxRememberBrowseCheck.IsChecked = cfg.RememberInstanceBrowsePosition;
        UxRememberResultsCheck.IsChecked = cfg.RememberTaskResults;
        UxHideServerAddressesCheck.IsChecked = cfg.HideServerAddresses;
        UxRememberWindowCheck.IsChecked = cfg.RememberWindowPlacement;
        UxFollowReducedMotionCheck.IsChecked = cfg.FollowSystemReducedMotion;
        SelectComboByTag(UxTimeFormatCombo, cfg.PreferredTimeFormat.ToString());
        SelectComboByTag(UxCapacityUnitCombo, cfg.PreferredCapacityUnit.ToString());
        UxExactCapacityCheck.IsChecked = cfg.ShowExactCapacityValues;
    }

    private void SaveUxOptions(AppConfig cfg)
    {
        cfg.EnableRecentInstanceHistory = UxRecentHistoryCheck.IsChecked == true;
        cfg.EnableRecentToolHistory = UxRecentToolsCheck.IsChecked == true;
        cfg.RememberInstanceBrowsePosition = UxRememberBrowseCheck.IsChecked == true;
        if (!cfg.RememberInstanceBrowsePosition) VersionSelectPage.ClearRememberedBrowsePositions();
        cfg.RememberTaskResults = UxRememberResultsCheck.IsChecked == true;
        if (!cfg.RememberTaskResults) TaskResultHistoryService.Clear();
        cfg.HideServerAddresses = UxHideServerAddressesCheck.IsChecked == true;
        cfg.RememberWindowPlacement = UxRememberWindowCheck.IsChecked == true;
        cfg.FollowSystemReducedMotion = UxFollowReducedMotionCheck.IsChecked == true;
        if (Enum.TryParse<LauncherTimeFormat>(TagOf(UxTimeFormatCombo), out var time)) cfg.PreferredTimeFormat = time;
        if (Enum.TryParse<LauncherCapacityUnit>(TagOf(UxCapacityUnitCombo), out var unit)) cfg.PreferredCapacityUnit = unit;
        cfg.ShowExactCapacityValues = UxExactCapacityCheck.IsChecked == true;
    }

    private void UxClearRecent_Click(object sender, RoutedEventArgs e)
    {
        InstanceUxService.ClearRecent(_owner.ConfigService.Config);
        _owner.ConfigService.Config.RecentToolTabs.Clear();
        if (_preAutoSaveSnapshotJson != null)
        {
            var saved = JsonSerializer.Deserialize<AppConfig>(_preAutoSaveSnapshotJson);
            if (saved != null) { InstanceUxService.ClearRecent(saved); saved.RecentToolTabs.Clear(); _preAutoSaveSnapshotJson = JsonSerializer.Serialize(saved); }
        }
        _owner.ConfigService.Save();
        ToastService.ShowInfo("最近使用记录已清空。");
    }

    private void UxClearResults_Click(object sender, RoutedEventArgs e)
    {
        TaskResultHistoryService.Clear();
        ToastService.ShowInfo("本次任务结果已清空。");
    }

    private void UxResetLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!_owner.ResetLauncherLayout()) return;
        RunWithoutDirtyTracking(() => FeatureSearchEditCheck.IsChecked = false);
        var resetLayout = _owner.ReadFeatureSearchLayout();
        // Layout reset changes geometry only; keep a separately edited opacity draft.
        _pendingFeatureSearchLayout = resetLayout with { OpacityPercent = _pendingFeatureSearchLayout.OpacityPercent };
        SyncFeatureSearchControls();
        PreviewFeatureSearchSettings();
        var controls = new HashSet<string>(new[] { nameof(FeatureSearchWidthSlider), nameof(FeatureSearchHeightSlider), nameof(FeatureSearchOffsetXBox), nameof(FeatureSearchOffsetYBox) }, StringComparer.Ordinal);
        var current = BuildSettingsUiFingerprint();
        bool Affected(string line) { var fields = line.Split('|'); return fields.Length > 1 && controls.Contains(fields[1]); }
        _lastSavedUiFingerprint = string.Join("\n", _lastSavedUiFingerprint.Split('\n').Where(line => !Affected(line)).Concat(current.Split('\n').Where(Affected)).OrderBy(line => line, StringComparer.Ordinal));
        _lastPolledFingerprint = current;
        _hasUnsavedChanges = !_owner.ConfigService.Config.SettingsAutoSaveWithoutConfirm && !string.Equals(current, _lastSavedUiFingerprint, StringComparison.Ordinal);
        if (!_hasUnsavedChanges) ToastService.DismissActionPrompt("settings-dirty");
        SyncUxExternallySavedControls(controls);
        if (_preAutoSaveSnapshotJson != null)
        {
            var snapshot = JsonSerializer.Deserialize<AppConfig>(_preAutoSaveSnapshotJson);
            if (snapshot != null)
            {
                snapshot.LauncherWindowPlacement = _owner.ConfigService.Config.LauncherWindowPlacement;
                snapshot.FeatureSearchWidth = _pendingFeatureSearchLayout.Width;
                snapshot.FeatureSearchHeight = _pendingFeatureSearchLayout.Height;
                snapshot.FeatureSearchOffsetX = _pendingFeatureSearchLayout.OffsetX;
                snapshot.FeatureSearchOffsetY = _pendingFeatureSearchLayout.OffsetY;
                _preAutoSaveSnapshotJson = JsonSerializer.Serialize(snapshot);
            }
        }
    }

    private void SyncUxExternallySavedControls(IEnumerable<string> controls)
    {
        if (!_uxSettingsReady) return;
        var current = CaptureUxSettingsValues();
        foreach (var key in controls)
            if (current.TryGetValue(key, out var value)) _uxSavedValues[key] = value;
        UpdateUxChangeSummary();
    }

    private static readonly (string Property, string Control)[] UxExternalConfigBindings =
    {
        (nameof(AppConfig.DownloadSpeedLimitKBps), nameof(SpeedLimitBox)),
        (nameof(AppConfig.RememberTaskResults), nameof(UxRememberResultsCheck)),
        (nameof(AppConfig.EnableRecentToolHistory), nameof(UxRecentToolsCheck)),
        (nameof(AppConfig.EnableRecentInstanceHistory), nameof(UxRecentHistoryCheck))
    };
    private Dictionary<string, string> _uxExternalConfigSnapshot = new(StringComparer.Ordinal);

    private Dictionary<string, string> CaptureUxExternalConfig()
        => UxExternalConfigBindings.ToDictionary(binding => binding.Property,
            binding => JsonSerializer.Serialize(typeof(AppConfig).GetProperty(binding.Property)!.GetValue(_owner.ConfigService.Config)), StringComparer.Ordinal);

    private void UxExternalConfigSaved()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(UxExternalConfigSaved)); return; }
        var next = CaptureUxExternalConfig();
        var changed = UxExternalConfigBindings.Where(binding => !_uxExternalConfigSnapshot.TryGetValue(binding.Property, out var previous) || next[binding.Property] != previous).ToArray();
        _uxExternalConfigSnapshot = next;
        if (_uxSavingSettings || !_uxSettingsReady || changed.Length == 0) return;
        var wasSuppressed = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        try
        {
            foreach (var binding in changed)
                if (FindName(binding.Control) is FrameworkElement control)
                    ApplyUxDefault(control, typeof(AppConfig).GetProperty(binding.Property)!.GetValue(_owner.ConfigService.Config));
        }
        finally { _suppressDirtyTracking = wasSuppressed; }
        var names = changed.Select(binding => binding.Control).ToHashSet(StringComparer.Ordinal);
        var current = BuildSettingsUiFingerprint();
        bool Affected(string line) { var fields = line.Split('|'); return fields.Length > 1 && names.Contains(fields[1]); }
        _lastSavedUiFingerprint = string.Join("\n", _lastSavedUiFingerprint.Split('\n').Where(line => !Affected(line)).Concat(current.Split('\n').Where(Affected)).OrderBy(line => line, StringComparer.Ordinal));
        _lastPolledFingerprint = current;
        if (_preAutoSaveSnapshotJson != null)
        {
            var snapshot = JsonSerializer.Deserialize<AppConfig>(_preAutoSaveSnapshotJson);
            if (snapshot != null)
            {
                foreach (var binding in changed)
                {
                    var property = typeof(AppConfig).GetProperty(binding.Property)!;
                    property.SetValue(snapshot, property.GetValue(_owner.ConfigService.Config));
                }
                _preAutoSaveSnapshotJson = JsonSerializer.Serialize(snapshot);
            }
        }
        _hasUnsavedChanges = !_owner.ConfigService.Config.SettingsAutoSaveWithoutConfirm && !string.Equals(current, _lastSavedUiFingerprint, StringComparison.Ordinal);
        if (!_hasUnsavedChanges) ToastService.DismissActionPrompt("settings-dirty");
        SyncUxExternallySavedControls(names);
    }

    private void InitializeUxSettings()
    {
        InitializeUxOptions(_owner.ConfigService.Config);
        var defaults = new AppConfig();
        foreach (var (name, property) in UxDefaultBindings)
        {
            if (FindName(name) is not FrameworkElement control) continue;
            var info = typeof(AppConfig).GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            if (info == null) continue;
            RegisterUxReset(control, () => ApplyUxDefault(control, info.GetValue(defaults)));
        }
        RegisterUxReset(CloseActionCombo, () => CloseActionCombo.SelectedIndex = defaults.DefaultCloseAction switch
        { CloseButtonAction.MinimizeToTray => 1, CloseButtonAction.Minimize => 2, CloseButtonAction.AskEachTime => 3, _ => 0 });
        RegisterUxReset(PostGameLaunchActionCombo, () => PostGameLaunchActionCombo.SelectedIndex = (int)defaults.PostGameLaunchAction);
        RegisterUxReset(AutoStartLaunchBehaviorCombo, () => AutoStartLaunchBehaviorCombo.SelectedIndex = (int)defaults.AutoStartBehavior);
        RegisterUxReset(DefaultJavaCombo, () => DefaultJavaCombo.SelectedItem = DefaultJavaCombo.Items.OfType<JavaListItem>().FirstOrDefault(item => item.Entry == null));
        RegisterUxReset(SourceCombo, () => SourceCombo.SelectedIndex = defaults.Source == DownloadSource.Official ? 1 : 0);
        RegisterUxReset(DownloadNotifyModeCombo, () => DownloadNotifyModeCombo.SelectedIndex = defaults.DownloadNotifyMode);
        RegisterUxReset(DownloadPopupSizeModeCombo, () => DownloadPopupSizeModeCombo.SelectedIndex = defaults.DownloadPopupSizeDisplayMode);
        RegisterUxReset(LaunchHealthModeCombo, () => SelectComboByTag(LaunchHealthModeCombo, defaults.IntegrityCheckMode == IntegrityCheckMode.Strict ? "Strict" : "Simple"));
        RegisterHardwarePerformanceResets(defaults);
        RegisterUxReset(PerformanceOverlayScopeCombo, () => SelectComboByTag(PerformanceOverlayScopeCombo, defaults.PerformanceOverlayAllInstances ? "All" : "Selected"));
        RegisterUxReset(UiZoomWheelCheck, () => UiZoomWheelCheck.IsChecked = defaults.UiZoomShortcutBindings.Contains(UiZoomShortcutMode.CtrlWheel));
        RegisterUxReset(UiZoomArrowCheck, () => UiZoomArrowCheck.IsChecked = defaults.UiZoomShortcutBindings.Contains(UiZoomShortcutMode.CtrlArrow));
        RegisterUxReset(FeatureSearchWidthSlider, () => FeatureSearchWidthSlider.Value = FeatureSearchLayout.Default.Width);
        RegisterUxReset(FeatureSearchHeightSlider, () => FeatureSearchHeightSlider.Value = FeatureSearchLayout.Default.Height);
        RegisterUxReset(FeatureSearchOpacitySlider, () => FeatureSearchOpacitySlider.Value = FeatureSearchLayout.Default.OpacityPercent);
        RegisterUxReset(FeatureSearchOffsetXBox, () => FeatureSearchOffsetXBox.Text = FeatureSearchLayout.Default.OffsetX.ToString(CultureInfo.InvariantCulture));
        RegisterUxReset(FeatureSearchOffsetYBox, () => FeatureSearchOffsetYBox.Text = FeatureSearchLayout.Default.OffsetY.ToString(CultureInfo.InvariantCulture));
        RegisterUxReset(UiSkinCombo, () =>
        {
            RunWithoutDirtyTracking(() => SelectComboByTag(UiSkinCombo, defaults.UiSkin));
            CommitThemeChoice(defaults.UiSkin, showReplacement: false);
        });
        RegisterUxReset(SidebarPlacementCombo, () => SidebarPlacementCombo.SelectedIndex = 0);
        RegisterUxReset(SidebarAutomaticWidthCheck, () => SidebarAutomaticWidthCheck.IsChecked = true);
        RegisterUxReset(SidebarWidthSlider, () => SidebarWidthSlider.Value = 180);
        RegisterUxReset(SidebarAutomaticFontSizeCheck, () => SidebarAutomaticFontSizeCheck.IsChecked = true);
        RegisterUxReset(SidebarFontSizeSlider, () => SidebarFontSizeSlider.Value = 14);
        RegisterUxReset(SidebarShowIconsCheck, () => SidebarShowIconsCheck.IsChecked = true);
        RegisterUxReset(SidebarStartCollapsedCheck, () => SidebarStartCollapsedCheck.IsChecked = false);
        foreach (var input in _sidebarLabelControls.Values) RegisterUxReset(input, () => input.Text = "");
        foreach (var (kind, controls) in _animationControls)
        {
            var option = AnimationSettingsService.GetOption(defaults, kind);
            RegisterUxReset(controls.Enabled, () => controls.Enabled.IsChecked = option.Enabled);
            RegisterUxReset(controls.Intensity, () => controls.Intensity.Value = option.IntensityPercent);
            if (_notificationDurationControls.TryGetValue(kind, out var duration))
                RegisterUxReset(duration, () => duration.Value = option.DurationSeconds);
        }
        RegisterExperienceDefaults(defaults);
        InstallUxGroupResetMenus();
        _uxSavedValues = CaptureUxSettingsValues();
        _uxSettingsReady = true;
        UpdateUxChangeSummary();
        _uxExternalConfigSnapshot = CaptureUxExternalConfig();
        Loaded += (_, _) =>
        {
            _owner.ConfigService.Saved -= UxExternalConfigSaved;
            _owner.ConfigService.Saved += UxExternalConfigSaved;
            UxExternalConfigSaved();
        };
        PreviewKeyDown += UxSettings_PreviewKeyDown;
        Unloaded += (_, _) =>
        {
            _owner.ConfigService.Saved -= UxExternalConfigSaved;
            _searchDebounceTimer?.Stop();
            ClearSettingsSearchHighlight();
        };
    }

    private void RegisterUxReset(FrameworkElement control, Action reset)
    {
        var key = UxControlKey(control);
        if (string.IsNullOrEmpty(key)) return;
        var entry = new UxResetEntry(control, CompactUxLabel(UxSettingLabel(control)), CompactUxLabel(UxSettingGroup(control)), reset);
        _uxResetEntries[key] = entry;
        var context = control.ContextMenu ?? new ContextMenu();
        if (control is TextBox && context.Items.Count == 0)
        {
            foreach (var (label, command) in new[] { ("撤销", ApplicationCommands.Undo), ("重做", ApplicationCommands.Redo), ("剪切", ApplicationCommands.Cut), ("复制", ApplicationCommands.Copy), ("粘贴", ApplicationCommands.Paste), ("全选", ApplicationCommands.SelectAll) })
                context.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = control });
        }
        foreach (var old in context.Items.OfType<FrameworkElement>().Where(item => Equals(item.Tag, "UxDefaultAction")).ToArray()) context.Items.Remove(old);
        if (context.Items.Count > 0) context.Items.Add(new Separator { Tag = "UxDefaultAction" });
        var single = new MenuItem { Header = "恢复此项默认值", Tag = "UxDefaultAction" };
        single.Click += (_, _) => ResetUxEntries(new[] { entry });
        context.Items.Add(single);
        var group = new MenuItem { Header = $"恢复“{entry.Group}”组默认值…", Tag = "UxDefaultAction" };
        group.Click += (_, _) => ConfirmUxGroupReset(entry.Group);
        context.Items.Add(group);
        control.ContextMenu = context;
    }

    private void InstallUxGroupResetMenus()
    {
        foreach (var header in SettingsRootPanel.Children.OfType<TextBlock>().Where(text => text.FontSize >= 14 && text.FontSize < 20))
        {
            var group = header.Text;
            if (!_uxResetEntries.Values.Any(entry => entry.Group == group)) continue;
            var reset = new MenuItem { Header = "恢复此组默认值…" };
            reset.Click += (_, _) => ConfirmUxGroupReset(group);
            header.ContextMenu = new ContextMenu();
            header.ContextMenu.Items.Add(reset);
        }
        foreach (var expander in FindSettingsControls<Expander>(SettingsRootPanel))
        {
            var group = expander.Header as string;
            if (string.IsNullOrWhiteSpace(group) || !_uxResetEntries.Values.Any(entry => entry.Group == group)) continue;
            var reset = new MenuItem { Header = "恢复此组默认值…" };
            reset.Click += (_, _) => ConfirmUxGroupReset(group);
            expander.ContextMenu = new ContextMenu();
            expander.ContextMenu.Items.Add(reset);
        }
    }

    private void ConfirmUxGroupReset(string group)
    {
        var entries = _uxResetEntries.Values.Where(entry => entry.Group == group).ToArray();
        if (entries.Length == 0) return;
        var labels = entries.Select(entry => entry.Label).Distinct(StringComparer.Ordinal).ToArray();
        var preview = string.Join("\n", labels.Take(10).Select(label => "• " + label));
        if (labels.Length > 10) preview += $"\n…共 {labels.Length} 项";
        if (!MessageBoxDialog.ShowConfirm($"将恢复“{group}”组内以下设置的默认值：\n\n{preview}\n\n遵循当前的保存方式；不会删除账号、实例或文件。", "恢复本组默认值")) return;
        ResetUxEntries(entries);
    }

    private void ResetUxEntries(IEnumerable<UxResetEntry> entries)
    {
        // Compose one edit while keeping dependent visibility and preview handlers active.
        _uxResetInProgress = true;
        _editDebounceTimer?.Stop();
        try { foreach (var entry in entries) entry.Reset(); }
        finally { _uxResetInProgress = false; }
        RefreshMemorySettingsPreview();
        SyncFeatureSearchLayoutAfterReset();
        PreviewAnimationSettings();
        PreviewFeatureSearchSettings();
        UpdateUxChangeSummary();
        OnSettingsEdited();
    }

    private void SyncFeatureSearchLayoutAfterReset()
    {
        var x = double.TryParse(FeatureSearchOffsetXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) ? px : _pendingFeatureSearchLayout.OffsetX;
        var y = double.TryParse(FeatureSearchOffsetYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var py) ? py : _pendingFeatureSearchLayout.OffsetY;
        _pendingFeatureSearchLayout = new FeatureSearchLayout(FeatureSearchWidthSlider.Value, FeatureSearchHeightSlider.Value, x, y, FeatureSearchOpacitySlider.Value).Normalize();
        SyncFeatureSearchControls();
    }

    private static void ApplyUxDefault(FrameworkElement control, object? value)
    {
        switch (control)
        {
            case CheckBox check when value is bool flag: check.IsChecked = flag; break;
            case TextBox text:
                text.Text = value is IEnumerable<string> lines ? string.Join(Environment.NewLine, lines) : Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
                break;
            case Slider slider when value != null: slider.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case ComboBox combo:
                var expected = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                foreach (var item in combo.Items.OfType<ComboBoxItem>())
                    if (string.Equals(Convert.ToString(item.Tag, CultureInfo.InvariantCulture) ?? "", expected, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
                break;
        }
    }

    private static string UxControlKey(FrameworkElement control)
        => !string.IsNullOrWhiteSpace(control.Name) ? control.Name : control.Tag as string ?? "";

    private static string CompactUxLabel(string label)
    {
        var value = string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length > 70 ? value[..70] + "…" : value;
    }

    private string UxSettingLabel(FrameworkElement control)
    {
        var explicitLabel = control.Name switch
        {
            nameof(WidthBox) => "游戏窗口宽度",
            nameof(HeightBox) => "游戏窗口高度",
            nameof(FeatureSearchWidthSlider) => "搜索框宽度",
            nameof(FeatureSearchHeightSlider) => "搜索框高度",
            nameof(FeatureSearchOpacitySlider) => "搜索框不透明度",
            nameof(FeatureSearchOffsetXBox) => "搜索框水平偏移",
            nameof(FeatureSearchOffsetYBox) => "搜索框垂直偏移",
            nameof(UiZoomSlider) => "界面缩放比例",
            nameof(ScheduledBackupIntervalBox) => "自动备份间隔",
            nameof(ScheduledBackupRetentionBox) => "自动备份保留数量",
            nameof(TouchScaleBox) => "触屏按钮大小",
            nameof(TouchOpacityBox) => "触屏按钮不透明度",
            nameof(TouchSensitivityBox) => "触屏视角灵敏度",
            nameof(SidebarPlacementCombo) => "侧边栏显示位置",
            nameof(SidebarWidthSlider) => "侧边栏展开宽度",
            nameof(SidebarFontSizeSlider) => "导航文字大小",
            _ => null
        };
        if (explicitLabel != null) return explicitLabel;
        foreach (var (kind, duration) in _notificationDurationControls)
            if (ReferenceEquals(control, duration))
                return AnimationSettingsService.Options.First(option => option.Kind == kind).Label + "动画时长";
        foreach (var (key, input) in _sidebarLabelControls)
            if (ReferenceEquals(control, input))
                return SidebarPreferences.Labels.First(item => item.Key == key).DefaultText + "导航文字";
        foreach (var (kind, controls) in _animationControls)
            if (ReferenceEquals(control, controls.Intensity))
                return AnimationSettingsService.Options.First(option => option.Kind == kind).Label + "动画强度";
        if (control is ContentControl content && content.Content is string label && !string.IsNullOrWhiteSpace(label)) return label;
        foreach (var group in SettingsVisibilityService.Groups)
            foreach (var item in group.Items)
                if (item.Names.Contains(control.Name, StringComparer.Ordinal)) return item.Label;
        DependencyObject? current = control;
        for (var depth = 0; depth < 10 && current != null; depth++)
        {
            var parent = LogicalTreeHelper.GetParent(current);
            if (parent is Panel panel)
            {
                var index = panel.Children.IndexOf(current as UIElement);
                for (var i = index - 1; i >= 0; i--)
                    if (panel.Children[i] is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)) return text.Text;
            }
            current = parent;
        }
        return string.IsNullOrWhiteSpace(control.Name) ? "动画设置" : control.Name;
    }

    private string UxSettingGroup(FrameworkElement control)
    {
        for (DependencyObject? current = control; current != null && !ReferenceEquals(current, SettingsRootPanel); current = LogicalTreeHelper.GetParent(current))
            if (current is Expander { Header: string title }) return title;
        var group = "基础与模式";
        foreach (UIElement child in SettingsRootPanel.Children)
        {
            if (child is TextBlock text && text.FontSize >= 14 && text.FontSize < 20 && !string.IsNullOrWhiteSpace(text.Text)) group = text.Text;
            if (ReferenceEquals(child, control) || FindSettingsControls<FrameworkElement>(child).Any(item => ReferenceEquals(item, control))) return group;
        }
        return group;
    }

    private Dictionary<string, UxSettingValue> CaptureUxSettingsValues()
    {
        var result = new Dictionary<string, UxSettingValue>(StringComparer.Ordinal);
        void Add(FrameworkElement control, object? value, string display)
        {
            var key = UxControlKey(control);
            if (key.Length > 0) result[key] = new(CompactUxLabel(UxSettingLabel(control)), JsonSerializer.Serialize(value), display);
        }
        foreach (var text in FindSettingsControls<TextBox>(SettingsRootPanel))
            if (!text.IsReadOnly && !InputExperienceService.GetIsAuxiliary(text) && !ReferenceEquals(text, SettingsSearchBox) && !ReferenceEquals(text, CustomAccentColorBox)) Add(text, MeaningfulInputValue(text), "文本已修改");
        foreach (var check in FindSettingsControls<CheckBox>(SettingsRootPanel))
            if (!ReferenceEquals(check, FeatureSearchEditCheck)) Add(check, check.IsChecked, check.IsChecked == true ? "开启" : "关闭");
        foreach (var radio in FindSettingsControls<RadioButton>(SettingsRootPanel)) Add(radio, radio.IsChecked, radio.IsChecked == true ? "选中" : "未选中");
        foreach (var slider in FindSettingsControls<Slider>(SettingsRootPanel))
            if (!ReferenceEquals(slider, AccentRSlider) && !ReferenceEquals(slider, AccentGSlider) && !ReferenceEquals(slider, AccentBSlider)) Add(slider, Math.Round(slider.Value, 3), slider.Value.ToString("0.###", CultureInfo.CurrentCulture));
        foreach (var combo in FindSettingsControls<ComboBox>(SettingsRootPanel))
        {
            if (ReferenceEquals(combo, PerformanceTemplateFieldCombo)) continue; // 插入字段工具，不是待保存设置。
            if (ReferenceEquals(combo, UiSkinCombo)) { Add(combo, _committedUiSkin, ThemeService.GetDisplayName(_committedUiSkin)); continue; }
            var value = combo.SelectedItem switch
            {
                JavaListItem java => java.Entry?.Id ?? "",
                ComboBoxItem item => item.Tag?.ToString() ?? item.Content?.ToString() ?? "",
                _ => combo.SelectedValue?.ToString() ?? combo.SelectedItem?.ToString() ?? ""
            };
            var display = combo.SelectedItem is ComboBoxItem choice ? choice.Content?.ToString() ?? "选择已修改" : "选择已修改";
            Add(combo, value, display);
        }
        result["CustomAccentColor"] = new("自定义主题颜色", _committedCustomAccentColor ?? "", _committedCustomAccentColor ?? "未指定");
        result["UiSkinCandidates"] = new("配色候选列表", JsonSerializer.Serialize(_pendingSkinCandidates), "列表已修改");
        result["HiddenFeatures"] = new("隐藏功能入口", JsonSerializer.Serialize(_pendingHiddenFeatureKeys.OrderBy(key => key, StringComparer.Ordinal)), "列表已修改");
        result["HiddenSettings"] = new("隐藏设置项", JsonSerializer.Serialize(_pendingHiddenSettingKeys.OrderBy(key => key, StringComparer.Ordinal)), "列表已修改");
        return result;
    }

    private List<string> GetUxPendingChanges()
    {
        if (!_uxSettingsReady) return new();
        var current = CaptureUxSettingsValues();
        bool IsSavedInCurrentMode(string key) => key switch
        {
            nameof(JavaArchCombo) or nameof(JavaInstallModeCombo) or nameof(JavaVersionCombo) or nameof(EnforceJavaVersionMatchCheck) => AdvancedModeCheck.IsChecked == true,
            nameof(SimpleJavaVersionCombo) => AdvancedModeCheck.IsChecked != true,
            _ => true
        };
        return current.Where(item => IsSavedInCurrentMode(item.Key) && _uxSavedValues.TryGetValue(item.Key, out var previous) && previous.Value != item.Value.Value)
            .Select(item => $"{item.Value.Label}：{(_uxSavedValues[item.Key].Display == "文本已修改" ? "文本已修改" : _uxSavedValues[item.Key].Display + " → " + item.Value.Display)}")
            .Distinct(StringComparer.Ordinal).ToList();
    }

    private void UpdateUxChangeSummary()
    {
        if (!_uxSettingsReady) return;
        var changes = GetUxPendingChanges();
        _uxSummaryIsLatestSave = changes.Count == 0 && _uxLastSavedChanges.Count > 0;
        var count = _uxSummaryIsLatestSave ? _uxLastSavedChanges.Count : changes.Count;
        UxChangeSummaryButton.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UxChangeSummaryButton.Content = _uxSummaryIsLatestSave ? $"最近保存的修改（{count}）" : $"查看本次修改（{count}）";
    }

    private void MarkUxValuesAsSaved()
    {
        if (!_uxSettingsReady) return;
        var changes = GetUxPendingChanges();
        if (changes.Count > 0) _uxLastSavedChanges = changes;
        _uxSavedValues = CaptureUxSettingsValues();
        UpdateUxChangeSummary();
    }

    private void UxChangeSummary_Click(object sender, RoutedEventArgs e)
    {
        UpdateUxChangeSummary();
        var changes = _uxSummaryIsLatestSave ? _uxLastSavedChanges : GetUxPendingChanges();
        if (changes.Count == 0) return;
        MessageBoxDialog.ShowInfo(string.Join("\n\n", changes.Select(change => "• " + change)), _uxSummaryIsLatestSave ? "最近保存的设置修改" : "本次待保存的设置修改");
    }

    private void UxSettings_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { SettingsSearchBox.Focus(); SettingsSearchBox.SelectAll(); e.Handled = true; }
        else if (SettingsSearchBox.IsKeyboardFocusWithin && e.Key == Key.Enter)
        {
            _searchDebounceTimer?.Stop();
            if (_searchMatches.Count == 0) RunSettingsSearch(SettingsSearchBox.Text?.Trim() ?? "");
            else StepSettingsSearchMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (SettingsSearchBox.IsKeyboardFocusWithin && e.Key == Key.Escape && SettingsSearchBox.Text.Length > 0)
        { SettingsSearchBox.Clear(); e.Handled = true; }
    }

    private sealed class SettingsSearchOutline : Adorner
    {
        public bool IsCurrent { get; set; }
        public SettingsSearchOutline(UIElement element) : base(element) { IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext drawingContext)
        {
            var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            var pen = new Pen(accent, IsCurrent ? 2 : 1);
            var rect = new Rect(new Point(0, 0), AdornedElement.RenderSize);
            rect.Inflate(2, 2);
            drawingContext.DrawRoundedRectangle(null, pen, rect, 3, 3);
        }
    }
    private readonly Dictionary<FrameworkElement, SettingsSearchOutline> _uxSearchOutlines = new();
}
