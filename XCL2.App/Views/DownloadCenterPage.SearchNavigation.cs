using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    /// <summary>Open the Mod category once with the selected installed instance's
    /// real game version and loader. This is a search, never an automatic install.</summary>
    public void SelectModCategoryForInstance(string? minecraftVersion, string? loader)
    {
        _applyingSearchPreset = true;
        try
        {
            // This action explicitly targets the selected instance. A directory
            // chosen for an earlier resource must not replace its mods default.
            _downloadDirectoryMemory.Clear();
            _autoLoadedCategories.Add("mod");
            var filters = ModManagementPresentation.DownloadFilters(minecraftVersion, loader);
            _lastTypedGameVersion = filters.MinecraftVersion.Length == 0 ? null : filters.MinecraftVersion;
            ModSearchBox.Clear();
            ModGameVersionBox.Text = _lastTypedGameVersion ?? "";
            ModLoaderCombo.SelectedIndex = 0;
            SelectTag(ModLoaderCombo, filters.Loader);
            CatMod.IsChecked = true;
            _modPageIndex = 0;
        }
        finally { _applyingSearchPreset = false; }
        _debounceTimer.Stop();
        _pendingDebouncedAction = null;
        _ = RunModSearchAsync(showHints: true);
    }

    /// <summary>搜索定位仅切换分类，沿用正常分类事件及 API Key 提示，不触发下载。</summary>
    internal bool SelectFeatureSearchSection(string sectionKey)
    {
        RadioButton? category = sectionKey switch
        {
            "VersionPanel" => CatVersion,
            "ModPanel" => CatMod,
            "ResourcePanel" => CatResourcePack,
            "MapPanel" or "MapKeyMissingPanel" => CatMap,
            "FavoritesPanel" => CatFavorites,
            _ => null
        };
        if (category == null || category.Visibility != Visibility.Visible || !category.IsEnabled) return false;
        category.SetCurrentValue(RadioButton.IsCheckedProperty, true);
        return category.IsChecked == true;
    }
}
