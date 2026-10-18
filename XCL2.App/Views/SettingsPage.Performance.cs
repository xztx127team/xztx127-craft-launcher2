using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private bool _performanceControlsReady;
    private Dictionary<string, object?> _performanceConfigSnapshot = new();

    private sealed record PerformanceInstanceChoice(string Id, string Name, string Label)
    {
        public override string ToString() => Id;
    }

    private static readonly PropertyInfo[] PerformanceConfigProperties = typeof(AppConfig).GetProperties()
        .Where(property => property.Name.StartsWith("PerformanceOverlay", StringComparison.Ordinal)).ToArray();

    private void InitializePerformanceOverlaySettings(AppConfig cfg)
    {
        _performanceControlsReady = false;
        foreach (var property in PerformanceConfigProperties) ApplyPerformanceConfigValue(cfg, property.Name);
        RefreshPerformanceInstanceChoices();
        _performanceConfigSnapshot = CapturePerformanceConfig(cfg);
        _performanceControlsReady = true;
        UpdatePerformanceScopeUi();
    }

    private static Dictionary<string, object?> CapturePerformanceConfig(AppConfig cfg)
        => PerformanceConfigProperties.ToDictionary(property => property.Name, property => property.GetValue(cfg), StringComparer.Ordinal);

    private string? ApplyPerformanceConfigValue(AppConfig cfg, string property)
    {
        switch (property)
        {
            case nameof(AppConfig.PerformanceOverlayEnabled): PerformanceOverlayEnabledCheck.IsChecked = cfg.PerformanceOverlayEnabled; return nameof(PerformanceOverlayEnabledCheck);
            case nameof(AppConfig.PerformanceOverlayShowSystemCpu): PerformanceOverlaySystemCpuCheck.IsChecked = cfg.PerformanceOverlayShowSystemCpu; return nameof(PerformanceOverlaySystemCpuCheck);
            case nameof(AppConfig.PerformanceOverlayShowSystemMemory): PerformanceOverlaySystemMemoryCheck.IsChecked = cfg.PerformanceOverlayShowSystemMemory; return nameof(PerformanceOverlaySystemMemoryCheck);
            case nameof(AppConfig.PerformanceOverlayShowMemoryPercent): PerformanceOverlayMemoryPercentCheck.IsChecked = cfg.PerformanceOverlayShowMemoryPercent; return nameof(PerformanceOverlayMemoryPercentCheck);
            case nameof(AppConfig.PerformanceOverlayShowGames): PerformanceOverlayGamesCheck.IsChecked = cfg.PerformanceOverlayShowGames; return nameof(PerformanceOverlayGamesCheck);
            case nameof(AppConfig.PerformanceOverlayShowGameTotals): PerformanceOverlayGameTotalsCheck.IsChecked = cfg.PerformanceOverlayShowGameTotals; return nameof(PerformanceOverlayGameTotalsCheck);
            case nameof(AppConfig.PerformanceOverlayShowLauncher): PerformanceOverlayLauncherCheck.IsChecked = cfg.PerformanceOverlayShowLauncher; return nameof(PerformanceOverlayLauncherCheck);
            case nameof(AppConfig.PerformanceOverlayShowGameCpu): PerformanceOverlayGameCpuCheck.IsChecked = cfg.PerformanceOverlayShowGameCpu; return nameof(PerformanceOverlayGameCpuCheck);
            case nameof(AppConfig.PerformanceOverlayShowGameMemory): PerformanceOverlayGameMemoryCheck.IsChecked = cfg.PerformanceOverlayShowGameMemory; return nameof(PerformanceOverlayGameMemoryCheck);
            case nameof(AppConfig.PerformanceOverlayShowProcessId): PerformanceOverlayPidCheck.IsChecked = cfg.PerformanceOverlayShowProcessId; return nameof(PerformanceOverlayPidCheck);
            case nameof(AppConfig.PerformanceOverlayAllInstances): SelectComboByTag(PerformanceOverlayScopeCombo, cfg.PerformanceOverlayAllInstances ? "All" : "Selected"); return nameof(PerformanceOverlayScopeCombo);
            case nameof(AppConfig.PerformanceOverlayInstanceId):
            case nameof(AppConfig.PerformanceOverlayInstanceName): RefreshPerformanceInstanceChoices(cfg.PerformanceOverlayInstanceId, cfg.PerformanceOverlayInstanceName); return nameof(PerformanceOverlayInstanceCombo);
            case nameof(AppConfig.PerformanceOverlayStyle): SelectComboByTag(PerformanceOverlayStyleCombo, PerformanceOverlaySettings.Detailed(cfg) ? "Detailed" : "Compact"); return nameof(PerformanceOverlayStyleCombo);
            case nameof(AppConfig.PerformanceOverlayWindowWidth):
                var width = cfg.PerformanceOverlayWindowWidth <= 0 ? 0 : new[] { 280, 320, 420, 520, 600 }
                    .OrderBy(value => Math.Abs(value - PerformanceOverlaySettings.WindowWidth(cfg))).First();
                SelectComboByTag(PerformanceOverlayWidthCombo, width.ToString()); return nameof(PerformanceOverlayWidthCombo);
            case nameof(AppConfig.PerformanceOverlayGameSort): SelectComboByTag(PerformanceOverlaySortCombo, PerformanceOverlaySettings.GameSort(cfg)); return nameof(PerformanceOverlaySortCombo);
            case nameof(AppConfig.PerformanceOverlayColorScheme): SelectComboByTag(PerformanceOverlayColorCombo, PerformanceOverlaySettings.ColorScheme(cfg)); return nameof(PerformanceOverlayColorCombo);
            case nameof(AppConfig.PerformanceOverlayMemoryUnit): SelectComboByTag(PerformanceOverlayMemoryUnitCombo, PerformanceOverlaySettings.MemoryUnit(cfg)); return nameof(PerformanceOverlayMemoryUnitCombo);
            case nameof(AppConfig.PerformanceOverlayFontSize): PerformanceOverlayFontSlider.Value = PerformanceOverlaySettings.FontSize(cfg); return nameof(PerformanceOverlayFontSlider);
            case nameof(AppConfig.PerformanceOverlayOpacityPercent): PerformanceOverlayOpacitySlider.Value = PerformanceOverlaySettings.Opacity(cfg) * 100; return nameof(PerformanceOverlayOpacitySlider);
            case nameof(AppConfig.PerformanceOverlayRefreshMilliseconds):
                var ms = PerformanceOverlaySettings.RefreshMilliseconds(cfg);
                SelectComboByTag(PerformanceOverlayRefreshCombo, new[] { 500, 1000, 2000, 5000, 10000 }.OrderBy(value => Math.Abs(value - ms)).First().ToString());
                return nameof(PerformanceOverlayRefreshCombo);
            case nameof(AppConfig.PerformanceOverlayAlwaysOnTop): PerformanceOverlayTopmostCheck.IsChecked = cfg.PerformanceOverlayAlwaysOnTop; return nameof(PerformanceOverlayTopmostCheck);
            case nameof(AppConfig.PerformanceOverlayLockPosition): PerformanceOverlayLockCheck.IsChecked = cfg.PerformanceOverlayLockPosition; return nameof(PerformanceOverlayLockCheck);
            default: return null;
        }
    }

    private void SavePerformanceOverlaySettings(AppConfig cfg)
    {
        cfg.PerformanceOverlayEnabled = PerformanceOverlayEnabledCheck.IsChecked == true;
        cfg.PerformanceOverlayShowSystemCpu = PerformanceOverlaySystemCpuCheck.IsChecked == true;
        cfg.PerformanceOverlayShowSystemMemory = PerformanceOverlaySystemMemoryCheck.IsChecked == true;
        cfg.PerformanceOverlayShowMemoryPercent = PerformanceOverlayMemoryPercentCheck.IsChecked == true;
        cfg.PerformanceOverlayShowGames = PerformanceOverlayGamesCheck.IsChecked == true;
        cfg.PerformanceOverlayShowGameTotals = PerformanceOverlayGameTotalsCheck.IsChecked == true;
        cfg.PerformanceOverlayShowLauncher = PerformanceOverlayLauncherCheck.IsChecked == true;
        cfg.PerformanceOverlayShowGameCpu = PerformanceOverlayGameCpuCheck.IsChecked == true;
        cfg.PerformanceOverlayShowGameMemory = PerformanceOverlayGameMemoryCheck.IsChecked == true;
        cfg.PerformanceOverlayShowProcessId = PerformanceOverlayPidCheck.IsChecked == true;
        cfg.PerformanceOverlayAllInstances = TagOf(PerformanceOverlayScopeCombo) != "Selected";
        if (PerformanceOverlayInstanceCombo.SelectedItem is PerformanceInstanceChoice choice)
        {
            cfg.PerformanceOverlayInstanceId = choice.Id;
            cfg.PerformanceOverlayInstanceName = choice.Name;
        }
        cfg.PerformanceOverlayStyle = TagOf(PerformanceOverlayStyleCombo) == "Detailed" ? "Detailed" : "Compact";
        cfg.PerformanceOverlayWindowWidth = int.TryParse(TagOf(PerformanceOverlayWidthCombo), out var width) && width > 0
            ? Math.Clamp(width, 280, 600) : 0;
        cfg.PerformanceOverlayGameSort = TagOf(PerformanceOverlaySortCombo) is "Cpu" or "Memory" ? TagOf(PerformanceOverlaySortCombo)! : "Name";
        cfg.PerformanceOverlayColorScheme = TagOf(PerformanceOverlayColorCombo) is "Dark" or "Light" ? TagOf(PerformanceOverlayColorCombo)! : "Theme";
        cfg.PerformanceOverlayMemoryUnit = TagOf(PerformanceOverlayMemoryUnitCombo) is "MB" or "GB" ? TagOf(PerformanceOverlayMemoryUnitCombo)! : "Auto";
        cfg.PerformanceOverlayFontSize = PerformanceOverlaySettings.FiniteClamp(PerformanceOverlayFontSlider.Value, 10, 26, 14);
        cfg.PerformanceOverlayOpacityPercent = PerformanceOverlaySettings.FiniteClamp(PerformanceOverlayOpacitySlider.Value, 25, 100, 90);
        cfg.PerformanceOverlayRefreshMilliseconds = int.TryParse(TagOf(PerformanceOverlayRefreshCombo), out var ms) ? Math.Clamp(ms, 500, 10000) : 1000;
        cfg.PerformanceOverlayAlwaysOnTop = PerformanceOverlayTopmostCheck.IsChecked == true;
        cfg.PerformanceOverlayLockPosition = PerformanceOverlayLockCheck.IsChecked == true;
        _performanceConfigSnapshot = CapturePerformanceConfig(cfg);
    }

    private void SubscribePerformanceOverlayUpdates()
    {
        PerformanceOverlayService.SettingsChanged -= PerformanceSettingsChanged;
        PerformanceOverlayService.SettingsChanged += PerformanceSettingsChanged;
        _owner.ProcessManager.Changed -= PerformanceProcessesChanged;
        _owner.ProcessManager.Changed += PerformanceProcessesChanged;
        PerformanceSettingsChanged(_owner.ConfigService.Config);
        RefreshPerformanceInstanceChoices();
    }

    private void UnsubscribePerformanceOverlayUpdates()
    {
        PerformanceOverlayService.SettingsChanged -= PerformanceSettingsChanged;
        _owner.ProcessManager.Changed -= PerformanceProcessesChanged;
    }

    private void PerformanceSettingsChanged(AppConfig cfg)
    {
        if (!ReferenceEquals(cfg, _owner.ConfigService.Config)) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => PerformanceSettingsChanged(cfg))); return; }
        if (!_performanceControlsReady) return;
        var next = CapturePerformanceConfig(cfg);
        var changed = PerformanceConfigProperties.Where(property => !_performanceConfigSnapshot.TryGetValue(property.Name, out var previous) ||
            !Equals(previous, next[property.Name])).ToArray();
        if (changed.Length == 0) return;
        var controls = new HashSet<string>(StringComparer.Ordinal);
        var oldSuppress = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        try
        {
            // 只同步右键实际更改的字段，保留本页其它尚未保存的编辑。
            foreach (var property in changed)
            {
                var control = ApplyPerformanceConfigValue(cfg, property.Name);
                if (control != null) controls.Add(control);
            }
            UpdatePerformanceScopeUi();
        }
        finally { _suppressDirtyTracking = oldSuppress; }
        _performanceConfigSnapshot = next;
        if (!_uiFingerprintReady) return;
        var current = BuildSettingsUiFingerprint();
        bool Affected(string line) { var fields = line.Split('|'); return fields.Length > 1 && controls.Contains(fields[1]); }
        _lastSavedUiFingerprint = string.Join("\n", _lastSavedUiFingerprint.Split('\n').Where(line => !Affected(line))
            .Concat(current.Split('\n').Where(Affected)).OrderBy(line => line, StringComparer.Ordinal));
        _lastPolledFingerprint = current;
        SyncUxExternallySavedControls(controls);
        if (_preAutoSaveSnapshotJson != null)
        {
            var snapshot = JsonSerializer.Deserialize<AppConfig>(_preAutoSaveSnapshotJson);
            if (snapshot != null)
            {
                foreach (var property in changed) property.SetValue(snapshot, property.GetValue(cfg));
                _preAutoSaveSnapshotJson = JsonSerializer.Serialize(snapshot);
            }
        }
    }

    private void PerformanceProcessesChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(PerformanceProcessesChanged)); return; }
        if (!IsLoaded) return;
        RefreshPerformanceInstanceChoices();
    }

    private void RefreshPerformanceInstanceChoices(string? selectedId = null, string? selectedName = null)
    {
        if (PerformanceOverlayInstanceCombo == null) return;
        var previous = PerformanceOverlayInstanceCombo.SelectedItem as PerformanceInstanceChoice;
        selectedId ??= previous?.Id ?? _owner.ConfigService.Config.PerformanceOverlayInstanceId;
        selectedName ??= previous?.Name ?? _owner.ConfigService.Config.PerformanceOverlayInstanceName;
        var sample = PerformanceOverlayService.LatestSnapshot;
        var maxAge = TimeSpan.FromMilliseconds(PerformanceOverlaySettings.RefreshMilliseconds(_owner.ConfigService.Config) * 2 + 1000);
        var sampledGames = PerformanceOverlayService.IsPolling && sample != null && DateTimeOffset.UtcNow - sample.SampledAt <= maxAge
            ? sample.Games.Where(game => game.Edition != "Java") : Enumerable.Empty<PerformanceGameSample>();
        var targets = PerformanceMetricsService.CreateJavaTargets(_owner.ProcessManager.Running)
            .Select(target => new PerformanceInstanceChoice(target.InstanceId, target.DisplayName, target.DisplayName + $" · PID {target.ProcessId}"))
            .Concat(sampledGames
                .Select(game => new PerformanceInstanceChoice(game.InstanceId, game.DisplayName, game.DisplayName + $" · PID {game.ProcessId}")))
            .DistinctBy(choice => choice.Id).OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!string.IsNullOrEmpty(selectedId) && targets.All(choice => choice.Id != selectedId))
            targets.Insert(0, new PerformanceInstanceChoice(selectedId, selectedName ?? "", (selectedName ?? "已选实例") + "（未运行）"));
        targets.Insert(0, new PerformanceInstanceChoice("", "", "请选择一个运行实例"));
        var suppress = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        try
        {
            PerformanceOverlayInstanceCombo.ItemsSource = targets;
            PerformanceOverlayInstanceCombo.SelectedValue = selectedId;
        }
        finally { _suppressDirtyTracking = suppress; }
    }

    private void PerformanceOverlayScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_performanceControlsReady) UpdatePerformanceScopeUi();
    }

    private void UpdatePerformanceScopeUi()
    {
        var selected = TagOf(PerformanceOverlayScopeCombo) == "Selected";
        PerformanceOverlayInstanceCombo.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        PerformanceOverlayRefreshGamesButton.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PerformanceOverlayRefreshGames_Click(object sender, RoutedEventArgs e)
    {
        await _owner.ProcessManager.RefreshAsync();
        if (IsLoaded) RefreshPerformanceInstanceChoices();
    }

    public void RevealPerformanceSettings()
    {
        PerformanceOverlaySettingsGroup.IsExpanded = true;
        PerformanceOverlaySettingsGroup.BringIntoView();
    }
}
