using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    private readonly IncrementalDownloadList<UnifiedModItem> _continuousMods = new();
    private readonly IncrementalDownloadList<UnifiedResourceItem> _continuousResources = new();
    private readonly IncrementalDownloadList<CurseForgeMod> _continuousMaps = new();
    private List<VersionManifestEntry> _continuousVersions = new();
    private bool _versionWindowInvalid = true;
    private bool _versionWindowLoading;
    private int _versionWindowGeneration;
    private bool _syncingContinuousChecks;
    private bool _previousGameContinuous;
    private bool _previousResourceContinuous;
    private readonly Dictionary<ListBox, int> _continuousScrollIntent = new();

    private bool ContinuousGames => _owner.ConfigService.Config.DownloadGamesContinuousLoading;
    private bool ContinuousResources => _owner.ConfigService.Config.DownloadResourcesContinuousLoading;
    private int ContinuousBatchSize => DownloadListBatchPolicy.Normalize(_owner.ConfigService.Config.DownloadListBatchSize);

    private void InitializeContinuousLists()
    {
        _previousGameContinuous = ContinuousGames;
        _previousResourceContinuous = ContinuousResources;
        RefreshContinuousListPreferences();
        Loaded += (_, _) => RefreshContinuousListPreferences();
        ListPanelsHost.IsVisibleChanged += (_, _) =>
        {
            if (ListPanelsHost.IsVisible) RefreshContinuousListPreferences();
        };
    }

    private void RefreshContinuousListPreferences()
    {
        if (!_initialized) return;
        _syncingContinuousChecks = true;
        VersionContinuousCheck.IsChecked = ContinuousGames;
        ModContinuousCheck.IsChecked = ResourceContinuousCheck.IsChecked = MapContinuousCheck.IsChecked = ContinuousResources;
        _syncingContinuousChecks = false;
        VersionPrevPageButton.Visibility = VersionNextPageButton.Visibility = ContinuousGames ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { ModPrevPageButton, ModNextPageButton, ResourcePrevPageButton,
            ResourceNextPageButton, MapPrevPageButton, MapNextPageButton })
            button.Visibility = ContinuousResources ? Visibility.Collapsed : Visibility.Visible;

        if (_previousGameContinuous != ContinuousGames)
        {
            _previousGameContinuous = ContinuousGames;
            _versionPageIndex = 0;
            ApplyVersionFilter();
        }
        else if (ContinuousGames && VersionPanel.IsVisible && _versionWindowInvalid && _manifestCache != null)
            ApplyVersionFilter();
        if (_previousResourceContinuous != ContinuousResources)
        {
            _previousResourceContinuous = ContinuousResources;
            InvalidateExperienceSearches();
            _modPageIndex = _resourcePageIndex = _mapPageIndex = 0;
            _autoLoadedCategories.Remove("mod");
            _autoLoadedCategories.Remove("map");
            _lastLoadedResourceType = null;
            ReloadVisibleContinuousCategory();
        }
        else if (IsLoaded)
        {
            // Navigating away cancels pending requests. Re-entering a retained page
            // must not leave it stuck with an invalid cursor and no active search.
            if (ContinuousResources && ((ModPanel.IsVisible && _continuousMods.NeedsReset) ||
                (ResourcePanel.IsVisible && _continuousResources.NeedsReset) ||
                (MapPanel.IsVisible && _continuousMaps.NeedsReset))) ReloadVisibleContinuousCategory();
        }
    }

    private void ContinuousLoading_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncingContinuousChecks || sender is not CheckBox box) return;
        if (box == VersionContinuousCheck)
            _owner.ConfigService.Config.DownloadGamesContinuousLoading = box.IsChecked == true;
        else
            _owner.ConfigService.Config.DownloadResourcesContinuousLoading = box.IsChecked == true;
        try { _owner.ConfigService.Save(); }
        catch (Exception ex) { LauncherLogService.AppendLine("[下载列表设置] 保存失败：" + ex.Message); }
        RefreshContinuousListPreferences();
    }

    private void ReloadVisibleContinuousCategory()
    {
        if (!IsLoaded) return;
        if (ModPanel.IsVisible) { _autoLoadedCategories.Add("mod"); _ = RunModSearchAsync(); }
        else if (ResourcePanel.IsVisible) { _lastLoadedResourceType = _currentResourceType; _ = RunResourceSearchAsync(); }
        else if (MapPanel.IsVisible) { _autoLoadedCategories.Add("map"); _ = RunMapSearchAsync(); }
    }

    private void InvalidateContinuousSearches()
    {
        _continuousMods.Invalidate(); _continuousResources.Invalidate(); _continuousMaps.Invalidate();
        _versionWindowInvalid = true;
        ++_versionWindowGeneration;
        _versionWindowLoading = false;
        _continuousScrollIntent.Clear();
        foreach (var host in new[] { VersionIncrementalLoading, ModIncrementalLoading, ResourceIncrementalLoading, MapIncrementalLoading })
            host.Visibility = Visibility.Collapsed;
        foreach (var button in new[] { ModIncrementalRetry, ResourceIncrementalRetry, MapIncrementalRetry })
            button.Visibility = Visibility.Collapsed;
    }

    private void ResetContinuousVersions(List<VersionManifestEntry> filtered)
    {
        ++_versionWindowGeneration;
        _versionWindowLoading = false;
        _versionWindowInvalid = false;
        _continuousVersions = filtered;
        _online.Clear();
        AppendContinuousVersions(DownloadListBatchPolicy.Initial(ContinuousBatchSize));
        ResetListScroll(OnlineListBox);
    }

    private void AppendContinuousVersions(int count)
    {
        var favorites = _owner.ConfigService.Config.FavoriteItems
            .Where(item => item.Type == FavoriteItemType.Version).Select(item => item.SourceId).ToHashSet();
        foreach (var entry in _continuousVersions.Skip(_online.Count).Take(count))
            _online.Add(new VersionListItem(entry, favorites.Contains(entry.Id)));
        VersionPageSummaryText.Text = $"已加载 {_online.Count} / {_continuousVersions.Count} 条 · {VersionSortCaption}" +
            (_online.Count < _continuousVersions.Count ? " · 向下滚动继续加载" : " · 已全部加载");
    }

    private async Task LoadMoreVersionsAsync()
    {
        if (_versionWindowInvalid || _versionWindowLoading || _online.Count >= _continuousVersions.Count) return;
        var generation = _versionWindowGeneration;
        _versionWindowLoading = true;
        VersionIncrementalLoading.Visibility = Visibility.Visible;
        try
        {
            // Only UI models are created here: Mojang's manifest is cached as one
            // metadata document and has no remote pagination endpoint.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (generation == _versionWindowGeneration && ContinuousGames && IsLoaded)
                AppendContinuousVersions(ContinuousBatchSize);
        }
        finally
        {
            if (generation == _versionWindowGeneration)
            {
                _versionWindowLoading = false;
                VersionIncrementalLoading.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static string[] ContinuousSources(ModSource source) => source switch
    {
        ModSource.Modrinth => new[] { "modrinth" },
        ModSource.CurseForge => new[] { "curseforge" },
        _ => new[] { "modrinth", "curseforge" }
    };
    private static ModSource SourceFromCursor(string source) => source == "modrinth" ? ModSource.Modrinth : ModSource.CurseForge;

    private async Task LoadContinuousModsAsync(bool reset, bool showHints = false)
    {
        var keyword = ModSearchBox.Text?.Trim() ?? "";
        var gameVersion = NullIfEmpty(ModGameVersionBox.Text);
        var loader = NullIfEmpty(ComboTag(ModLoaderCombo));
        if (reset)
        {
            ++_modSearchSeq; CancelSearch(_modSearchCancellation);
            _continuousMods.Reset(ContinuousSources(_currentModSource));
            _mods.Clear(); _mcModHits.Clear(); ResetListScroll(ModListBox);
            ModTranslationHintText.Visibility = Visibility.Collapsed;
            if (showHints) CommitExperienceSearch(ModSearchBox);
        }
        string? translatedFrom = null, translatedKeyword = null;
        var warnings = new List<string>();
        var updated = await LoadContinuousWindowAsync("mods", _continuousMods, _mods, ModListBox,
            ModPageSummaryText, ModIncrementalLoading, ModIncrementalRetry, reset, showHints,
            async (source, offset, limit, ct) =>
            {
                var outcome = await GetModSearch().SearchAsync(SourceFromCursor(source), keyword, gameVersion,
                    loader, 0, limit, ct, startOffset: offset);
                translatedFrom ??= outcome.TranslatedFrom;
                translatedKeyword ??= outcome.TranslatedKeyword;
                warnings.AddRange(outcome.Warnings);
                return new(outcome.Items, source == "modrinth" ? outcome.ModrinthTotal : outcome.CurseForgeTotal,
                    source == "modrinth" ? outcome.ModrinthFailed : outcome.CurseForgeFailed);
            }, item => item.Source + ":" + item.SourceId, item =>
            {
                item.ShowIcon = _owner.ConfigService.Config.ShowModIcons;
                item.IsFavorite = _owner.ConfigService.Config.FavoriteItems.Any(f => f.MatchesKey(FavoriteItemType.Mod, item.SourceId, item.Source));
            });
        if (updated && translatedFrom != null)
        {
            ModTranslationHintText.Text = $"“{translatedFrom}” 已按 “{translatedKeyword ?? translatedFrom}” 搜索。";
            ModTranslationHintText.Visibility = Visibility.Visible;
        }
        if (updated && showHints && warnings.Count > 0)
            ReminderCenterService.Publish("download-mod-source-warning", "部分模组来源暂不可用", string.Join("\n", warnings.Distinct()));
        // The optional encyclopedia result is fetched once for a new query, never
        // once per appended batch, and shares cancellation with the primary query.
        if (reset && updated)
        {
            var generation = _continuousMods.Generation;
            try
            {
                var hits = await GetModSearch().SearchMcModAsync(keyword, _modSearchCancellation?.Token ?? default);
                if (generation != _continuousMods.Generation) return;
                _mcModHits.Clear(); foreach (var hit in hits) _mcModHits.Add(hit);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Optional reference lookup must not block downloads. */ }
        }
    }

    private async Task LoadContinuousResourcesAsync(bool reset, bool showHints = false)
    {
        var keyword = ResourceSearchBox.Text?.Trim() ?? "";
        var gameVersion = NullIfEmpty(ResourceGameVersionBox.Text);
        var resourceType = _currentResourceType;
        if (reset)
        {
            ++_resourceSearchSeq; CancelSearch(_resourceSearchCancellation);
            _continuousResources.Reset(ContinuousSources(_currentResourceSource));
            _resources.Clear(); ResetListScroll(ResourceListBox);
            if (showHints) CommitExperienceSearch(ResourceSearchBox);
        }
        var favoriteType = resourceType switch
        {
            ModrinthResourceType.DataPack => FavoriteItemType.DataPack,
            ModrinthResourceType.Shader => FavoriteItemType.Shader,
            _ => FavoriteItemType.ResourcePack
        };
        var warnings = new List<string>();
        var updated = await LoadContinuousWindowAsync("resources", _continuousResources, _resources, ResourceListBox,
            ResourcePageSummaryText, ResourceIncrementalLoading, ResourceIncrementalRetry, reset, showHints,
            async (source, offset, limit, ct) =>
            {
                var outcome = await GetModSearch().SearchResourcesAsync(SourceFromCursor(source), resourceType,
                    keyword, gameVersion, 0, limit, ct, startOffset: offset);
                warnings.AddRange(outcome.Warnings);
                return new(outcome.Items, source == "modrinth" ? outcome.ModrinthTotal : outcome.CurseForgeTotal,
                    source == "modrinth" ? outcome.ModrinthFailed : outcome.CurseForgeFailed);
            }, item => item.Source + ":" + item.SourceId, item =>
            {
                item.ShowIcon = _owner.ConfigService.Config.ShowModIcons; item.FavoriteType = favoriteType;
                item.IsFavorite = _owner.ConfigService.Config.FavoriteItems.Any(f => f.MatchesKey(favoriteType, item.SourceId, item.Source));
            });
        if (updated && showHints && warnings.Count > 0)
            ReminderCenterService.Publish("download-resource-source-warning", "部分资源来源暂不可用", string.Join("\n", warnings.Distinct()));
    }

    private async Task LoadContinuousMapsAsync(bool reset, bool showHints = false)
    {
        var keyword = MapSearchBox.Text?.Trim() ?? "";
        var gameVersion = NullIfEmpty(MapGameVersionBox.Text);
        if (reset)
        {
            ++_mapSearchSeq; CancelSearch(_mapSearchCancellation);
            _continuousMaps.Reset(new[] { "curseforge" });
            _maps.Clear(); ResetListScroll(MapListBox);
            if (showHints) CommitExperienceSearch(MapSearchBox);
        }
        await LoadContinuousWindowAsync("maps", _continuousMaps, _maps, MapListBox,
            MapPageSummaryText, MapIncrementalLoading, MapIncrementalRetry, reset, showHints,
            async (_, offset, limit, ct) =>
            {
                var result = await GetCurseForge().SearchMapsAsync(keyword, gameVersion, offset, limit, ct);
                return new(result.Data, result.Pagination?.TotalCount);
            }, item => item.Id.ToString(), item =>
            {
                item.ShowIcon = _owner.ConfigService.Config.ShowModIcons;
                item.IsFavorite = _owner.ConfigService.Config.FavoriteItems.Any(f => f.MatchesKey(FavoriteItemType.Map, item.Id.ToString(), ModSource.CurseForge));
            });
    }

    private async Task<bool> LoadContinuousWindowAsync<T>(string scope, IncrementalDownloadList<T> state,
        ObservableCollection<T> items, ListBox list, TextBlock summary, FrameworkElement loading, Button retry,
        bool initial, bool showHints,
        Func<string, int, int, CancellationToken, Task<IncrementalSourcePage<T>>> fetch,
        Func<T, string> key, Action<T> prepare)
    {
        if (state.IsLoading || !state.HasMore || IsSearchComposing || _applyingSearchPreset) return false;
        var generation = state.Generation;
        // Ownership stays with the page; retaining an undisposed completed CTS also
        // lets the optional encyclopedia lookup reuse the same cancellation token.
        var cancellation = scope switch
        {
            "mods" => BeginSearch(ref _modSearchCancellation),
            "resources" => BeginSearch(ref _resourceSearchCancellation),
            _ => BeginSearch(ref _mapSearchCancellation)
        };
        var token = cancellation.Token;
        loading.Visibility = Visibility.Visible; retry.Visibility = Visibility.Collapsed;
        summary.Text = $"已加载 {items.Count} 条 · 正在加载…";
        SetContextHint(list, "正在加载…");
        try
        {
            var batch = await state.LoadAsync(initial ? DownloadListBatchPolicy.Initial(ContinuousBatchSize) : ContinuousBatchSize,
                fetch, key, token);
            token.ThrowIfCancellationRequested();
            if (generation != state.Generation) return false;
            foreach (var item in batch) { prepare(item); items.Add(item); }
            summary.Text = $"已加载 {items.Count} 条 · " + (state.HasMore ? "向下滚动继续加载" : "已加载到末尾") +
                (state.HasFailedSources ? " · 部分来源加载失败" : "");
            retry.Visibility = state.HasFailedSources ? Visibility.Visible : Visibility.Collapsed;
            SetContextHint(list, state.HasFailedSources ? "暂时无法获取结果，请重试或更换来源。" : "没有找到匹配的结果，可以调整筛选条件。");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (generation != state.Generation) return false;
            summary.Text = $"加载失败 · 已保留 {items.Count} 条";
            retry.Visibility = Visibility.Visible;
            SetContextHint(list, "暂时无法获取结果，请重试或更换来源。", "重试", () => RetryContinuousScope(scope));
            if (showHints) ErrorPresenter.ShowFriendlyError("搜索失败，请检查网络后重试。", $"[分段加载] {ex}", "搜索失败");
            return false;
        }
        finally
        {
            if (generation == state.Generation) loading.Visibility = Visibility.Collapsed;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void IncrementalRetry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string scope }) RetryContinuousScope(scope);
    }
    private void RetryContinuousScope(string scope)
    {
        if (!ContinuousResources) return;
        if (scope == "mods") { _continuousMods.RetryFailedSources(); _ = _continuousMods.NeedsReset ? RunModSearchAsync() : LoadContinuousModsAsync(false); }
        else if (scope == "resources") { _continuousResources.RetryFailedSources(); _ = _continuousResources.NeedsReset ? RunResourceSearchAsync() : LoadContinuousResourcesAsync(false); }
        else { _continuousMaps.RetryFailedSources(); _ = _continuousMaps.NeedsReset ? RunMapSearchAsync() : LoadContinuousMapsAsync(false); }
    }

    private void ContinuousList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta < 0 && sender is ListBox list) QueueContinuousScroll(list);
    }
    private void ContinuousList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Down or Key.PageDown or Key.End) && sender is ListBox list) QueueContinuousScroll(list);
    }
    private void ContinuousList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ListBox list || e.VerticalChange <= 0 || e.ExtentHeightChange != 0) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || list.TouchesCapturedWithin.Any()) QueueContinuousScroll(list);
    }
    private void QueueContinuousScroll(ListBox list)
    {
        if (!list.IsVisible || !IsLoaded || (list == OnlineListBox ? !ContinuousGames : !ContinuousResources)) return;
        var generation = ContinuousListGeneration(list);
        if (_continuousScrollIntent.TryGetValue(list, out var queuedGeneration) && queuedGeneration == generation) return;
        _continuousScrollIntent[list] = generation;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_continuousScrollIntent.TryGetValue(list, out var queued) && queued == generation)
                _continuousScrollIntent.Remove(list);
            if (generation != ContinuousListGeneration(list)) return;
            if (!list.IsVisible || !IsLoaded) return;
            var scroll = FindContinuousScrollViewer(list);
            if (scroll == null || scroll.ScrollableHeight - scroll.VerticalOffset > Math.Max(80, scroll.ViewportHeight / 4)) return;
            if (list == OnlineListBox && ContinuousGames) _ = LoadMoreVersionsAsync();
            else if (ContinuousResources && list == ModListBox) _ = LoadContinuousModsAsync(false);
            else if (ContinuousResources && list == ResourceListBox) _ = LoadContinuousResourcesAsync(false);
            else if (ContinuousResources && list == MapListBox) _ = LoadContinuousMapsAsync(false);
        }));
    }
    private int ContinuousListGeneration(ListBox list) => list == OnlineListBox ? _versionWindowGeneration
        : list == ModListBox ? _continuousMods.Generation
        : list == ResourceListBox ? _continuousResources.Generation : _continuousMaps.Generation;
    private static ScrollViewer? FindContinuousScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); ++index)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroll) return scroll;
            var nested = FindContinuousScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }
    private static void ResetListScroll(ListBox list) => FindContinuousScrollViewer(list)?.ScrollToTop();
}
