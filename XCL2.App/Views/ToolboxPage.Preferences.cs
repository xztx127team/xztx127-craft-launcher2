using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ToolboxPage
{
    private void InitializeToolboxPreferences()
    {
        ToolboxMoreButton.ContextMenu = CreateToolPreferenceMenu(null);
        // 固定入口只改变位置；收藏保存在独立清单，不改写用户已排好的顺序。
        foreach (var button in PinnedToolboxButtons.Children.OfType<Button>())
            if (button.Tag is int index)
                button.ContextMenu = CreateToolPreferenceMenu(index);
    }

    private ContextMenu CreateToolPreferenceMenu(int? tabIndex)
    {
        var menu = new ContextMenu();
        if (tabIndex is int index)
        {
            var favorite = new MenuItem();
            favorite.Click += (_, _) => ToggleFavoriteTool(index);
            menu.Opened += (_, _) => favorite.Header = _owner.ConfigService.Config.FavoriteToolTabs?.Contains(index) == true
                ? "取消收藏此工具" : "收藏此工具";
            menu.Items.Add(favorite);
            menu.Items.Add(new Separator());
        }
        var recent = new MenuItem { Header = "记录最近使用的工具", IsCheckable = true };
        menu.Opened += (_, _) => recent.IsChecked = _owner.ConfigService.Config.EnableRecentToolHistory;
        recent.Click += (_, _) =>
        {
            _owner.ConfigService.Config.EnableRecentToolHistory = recent.IsChecked;
            _owner.ConfigService.Save();
            RefreshToolboxSearch();
        };
        menu.Items.Add(recent);
        var clear = new MenuItem { Header = "清除工具使用记录" };
        clear.Click += (_, _) =>
        {
            _owner.ConfigService.Config.RecentToolTabs?.Clear();
            _owner.ConfigService.Save();
            RefreshToolboxSearch();
        };
        menu.Opened += (_, _) => clear.IsEnabled = _owner.ConfigService.Config.RecentToolTabs?.Count > 0;
        menu.Items.Add(clear);
        return menu;
    }

    private void ToggleFavoriteTool(int index)
    {
        var cfg = _owner.ConfigService.Config;
        cfg.FavoriteToolTabs ??= new();
        if (!cfg.FavoriteToolTabs.Remove(index)) cfg.FavoriteToolTabs.Add(index);
        cfg.FavoriteToolTabs = cfg.FavoriteToolTabs.Where(id => id >= 0 && id < ToolboxTabControl.Items.Count).Distinct().Take(100).ToList();
        _owner.ConfigService.Save();
        RefreshToolboxSearch(index);
    }

    private void RecordToolVisit(int index)
    {
        var cfg = _owner.ConfigService.Config;
        if (!IsLoaded || !cfg.EnableRecentToolHistory || cfg.GuestModeEnabled) return;
        cfg.RecentToolTabs ??= new();
        if (cfg.RecentToolTabs.Count > 0 && cfg.RecentToolTabs[0] == index) return;
        cfg.RecentToolTabs.RemoveAll(id => id == index || id < 0 || id >= ToolboxTabControl.Items.Count);
        cfg.RecentToolTabs.Insert(0, index);
        cfg.RecentToolTabs = cfg.RecentToolTabs.Take(12).ToList();
        _owner.ConfigService.Save();
    }

    private void ToolboxSearchFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AllToolboxItems == null || ToolboxSearchCountText == null) return;
        RefreshToolboxSearch();
    }
}
