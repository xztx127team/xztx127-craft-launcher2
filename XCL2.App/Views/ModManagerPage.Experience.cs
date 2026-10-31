using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ModManagerPage
{
    private readonly HashSet<string> _selectedModPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectionGameDirectory;
    private bool _applyingModSelection;
    private bool _bulkChangingMods;

    private void ApplyModExperience()
    {
        var gameDirectory = GetEffectiveGameDir();
        if (!string.Equals(gameDirectory, _selectionGameDirectory, StringComparison.OrdinalIgnoreCase))
        { _selectedModPaths.Clear(); _selectionGameDirectory = gameDirectory; }
        _selectedModPaths.IntersectWith(_mods.Select(item => item.Info.FilePath));
        _applyingModSelection = true;
        try { foreach (var item in _mods) { item.IsBatchSelected = _selectedModPaths.Contains(item.Info.FilePath); item.BatchToolsVisible = ModFilterRow?.Visibility == Visibility.Visible; } }
        finally { _applyingModSelection = false; }
        ApplyModFilter();
        RefreshModMetadataStatus();
    }

    private void ToggleModFilter_Click(object sender, RoutedEventArgs e)
    {
        ModFilterRow.Visibility = ModFilterRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in _mods) item.BatchToolsVisible = ModFilterRow.Visibility == Visibility.Visible;
        if (ModFilterRow.Visibility == Visibility.Visible) ModFilterBox.Focus();
    }
    private void ModFilter_Changed(object sender, TextChangedEventArgs e) => ApplyModFilter();
    private void ApplyModFilter()
    {
        if (ModFilterBox == null || ModsListBox == null) return;
        var query = ModFilterBox.Text.Trim();
        var visible = _mods.Where(item => query.Length == 0 || (item.DisplayName + " " + item.FileName).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _applyingModSelection = true;
        try { ModsListBox.ItemsSource = visible; }
        finally { _applyingModSelection = false; }
        var hidden = _selectedModPaths.Count(path => !visible.Any(item => string.Equals(item.Info.FilePath, path, StringComparison.OrdinalIgnoreCase)));
        ModSelectionSummary.Text = _selectedModPaths.Count == 0 ? "" : $"已选 {_selectedModPaths.Count} 个" + (_owner.ConfigService.Config.Ux50ShowHiddenModSelections && hidden > 0 ? $"（筛选隐藏 {hidden} 个）" : "");
        ModSelectionSummary.Visibility = _selectedModPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateModPresentation(visible.Length);
    }
    private void ModSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingModSelection || sender is not CheckBox { Tag: LocalModDisplayItem item } check) return;
        if (check.IsChecked == true) _selectedModPaths.Add(item.Info.FilePath); else _selectedModPaths.Remove(item.Info.FilePath);
        ApplyModFilter();
    }
    private void SelectVisibleMods_Click(object sender, RoutedEventArgs e)
    {
        _applyingModSelection = true;
        try
        {
        foreach (var item in (ModsListBox.ItemsSource as IEnumerable<LocalModDisplayItem> ?? Array.Empty<LocalModDisplayItem>()))
        { _selectedModPaths.Add(item.Info.FilePath); item.IsBatchSelected = true; }
        }
        finally { _applyingModSelection = false; }
        ApplyModFilter();
    }
    private void ClearModSelection_Click(object sender, RoutedEventArgs e)
    { _applyingModSelection = true; try { _selectedModPaths.Clear(); foreach (var item in _mods) item.IsBatchSelected = false; } finally { _applyingModSelection = false; } ApplyModFilter(); }
    private async void EnableSelectedMods_Click(object sender, RoutedEventArgs e) => await ChangeSelectedModsAsync(true);
    private async void DisableSelectedMods_Click(object sender, RoutedEventArgs e) => await ChangeSelectedModsAsync(false);
    private async Task ChangeSelectedModsAsync(bool enable)
    {
        if (_bulkChangingMods) return;
        var selected = _mods.Where(item => _selectedModPaths.Contains(item.Info.FilePath)).ToArray();
        if (selected.Length == 0) { ToastService.ShowInfo("请先展开筛选与批量操作，勾选需要处理的模组。"); return; }
        var changes = selected.Where(item => item.Info.IsEnabled != enable).ToArray();
        if (changes.Length == 0) { ToastService.ShowInfo("所选模组已经处于目标状态。"); return; }
        var visible = (ModsListBox.ItemsSource as IEnumerable<LocalModDisplayItem> ?? Array.Empty<LocalModDisplayItem>()).Select(item => item.Info.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = selected.Count(item => !visible.Contains(item.Info.FilePath));
        if (_owner.ConfigService.Config.Ux50BulkModSummary && !MessageBoxDialog.ShowConfirm(
            $"已选 {selected.Length} 个模组，其中启用 {selected.Count(item => item.Info.IsEnabled)} 个、禁用 {selected.Count(item => !item.Info.IsEnabled)} 个。\n" +
            $"本次将{(enable ? "启用" : "禁用")} {changes.Length} 个，保持原状态 {selected.Length - changes.Length} 个。\n" +
            (hidden > 0 ? $"包含筛选后隐藏的 {hidden} 个已选项目。\n" : "") + "仅修改所选文件的启停状态，不删除文件。", "批量模组变更预览")) return;
        _bulkChangingMods = true;
        try
        {
            var failures = await Task.Run(() =>
            {
                var errors = new List<string>();
                foreach (var item in changes)
                {
                    try { if (enable) _localModService.Enable(item.Info.FilePath); else _localModService.Disable(item.Info.FilePath); }
                    catch (Exception ex) { errors.Add(item.FileName + "：" + ex.Message); }
                }
                return errors;
            });
            if (failures.Count > 0) MessageBoxDialog.ShowWarning($"完成 {changes.Length - failures.Count} 个，失败 {failures.Count} 个：\n" + string.Join("\n", failures.Take(10)), "批量操作结果");
            else ToastService.ShowSuccess($"已{(enable ? "启用" : "禁用")} {changes.Length} 个模组。");
            RefreshMods();
        }
        finally { _bulkChangingMods = false; }
    }

    private void RefreshModMetadataStatus()
    {
        if (ModMetadataStatus == null) return;
        var directory = GetEffectiveGameDir(); var cache = directory == null ? null : ModUpdatePreferenceService.GetCached(directory);
        ModMetadataStatus.Visibility = _owner.ConfigService.Config.Ux50ShowCachedModMetadata && cache != null ? Visibility.Visible : Visibility.Collapsed;
        ModMetadataStatus.Text = cache == null ? "" : ModUpdatePreferenceService.CacheLabel(cache);
        ModMetadataStatus.ToolTip = "手动联网检查时获取的更新资料，仅保存在本次启动器会话。不是当前网络状态；不会自动更新或安装。";
    }
    private void ShowCachedModMetadata_Click(object sender, RoutedEventArgs e)
    {
        var directory = GetEffectiveGameDir(); var cache = directory == null ? null : ModUpdatePreferenceService.GetCached(directory);
        if (cache == null) { ToastService.ShowInfo("当前实例还没有本次会话的更新资料，请先手动检查模组更新。"); return; }
        new ModUpdateSelectionDialog(cache.Candidates.ToList(), readOnly: true, cacheLabel: ModUpdatePreferenceService.CacheLabel(cache)).ShowDialog();
    }
    private void ManageIgnoredModUpdates_Click(object sender, RoutedEventArgs e)
    {
        var directory = GetEffectiveGameDir();
        if (directory == null) { ToastService.ShowInfo("请先选择实例。"); return; }
        new IgnoredModUpdatesDialog(_owner.ConfigService, directory).ShowDialog();
    }
}
