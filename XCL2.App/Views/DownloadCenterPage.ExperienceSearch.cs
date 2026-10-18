using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    private readonly Dictionary<TextBox, SearchCompositionGate> _searchComposition = new();
    private readonly List<Action> _refreshFilterChips = new();
    private CancellationTokenSource? _modSearchCancellation;
    private CancellationTokenSource? _resourceSearchCancellation;
    private CancellationTokenSource? _mapSearchCancellation;
    private bool _applyingSearchPreset;
    private readonly Dictionary<string, ExperienceSearchPage> _displayedSearchPages = new();
    private readonly HashSet<string> _pendingSearchScopes = new();
    private bool IsSearchComposing => _searchComposition.Values.Any(gate => gate.IsComposing);

    private void InitializeSearchExperience()
    {
        foreach (var box in new[] { VersionSearchBox, ResourceSearchBox, ResourceGameVersionBox, ModSearchBox,
            ModGameVersionBox, MapSearchBox, MapGameVersionBox })
        {
            _searchComposition[box] = new SearchCompositionGate(box,
                () => _owner.ConfigService.Config.Ux50SearchWaitForComposition,
                () => { _debounceTimer.Stop(); _pendingDebouncedAction = null; InvalidateExperienceSearches(); },
                () => ReapplySearchFor(box));
            box.PreviewKeyDown += (_, e) =>
            {
                if (IsSearchComposing) return;
                if (e.Key == Key.Escape && _owner.ConfigService.Config.Ux50SearchLayeredEscape && box.Text.Length > 0)
                { box.Clear(); e.Handled = true; }
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    CommitExperienceSearch(box);
                    _debounceTimer.Stop(); _pendingDebouncedAction = null;
                    ReapplySearchFor(box, immediately: true);
                    e.Handled = true;
                }
            };
        }
        AttachSearchMenu(VersionSearchBox, "versions", () => new()
        {
            ["query"] = VersionSearchBox.Text, ["category"] = ComboTag(VersionTypeCombo)
        }, values => { VersionSearchBox.Text = values.GetValueOrDefault("query", ""); SelectTag(VersionTypeCombo, values.GetValueOrDefault("category", "Release")); });
        AttachSearchMenu(ModSearchBox, "mods", () => new()
        {
            ["query"] = ModSearchBox.Text, ["version"] = ModGameVersionBox.Text,
            ["loader"] = ComboTag(ModLoaderCombo), ["source"] = ComboTag(ModSourceCombo)
        }, values => { ModSearchBox.Text = values.GetValueOrDefault("query", ""); ModGameVersionBox.Text = values.GetValueOrDefault("version", "");
            SelectTag(ModLoaderCombo, values.GetValueOrDefault("loader", "")); SelectTag(ModSourceCombo, values.GetValueOrDefault("source", "combined")); });
        AttachSearchMenu(ResourceSearchBox, "resources", () => new()
        {
            ["query"] = ResourceSearchBox.Text, ["version"] = ResourceGameVersionBox.Text,
            ["source"] = ComboTag(ResourceSourceCombo), ["type"] = ComboTag(ResourceTypeCombo), ["preview"] = (ResourceShowPreviewCheck.IsChecked == true).ToString()
        }, values => { ResourceSearchBox.Text = values.GetValueOrDefault("query", ""); ResourceGameVersionBox.Text = values.GetValueOrDefault("version", "");
            SelectTag(ResourceSourceCombo, values.GetValueOrDefault("source", "combined")); SelectTag(ResourceTypeCombo, values.GetValueOrDefault("type", "ResourcePack"));
            bool showPreview = values.GetValueOrDefault("preview", "False") == "True";
            if (ResourceShowPreviewCheck.IsChecked != showPreview)
            {
                ResourceShowPreviewCheck.IsChecked = showPreview;
                ResourceShowPreview_Changed(ResourceShowPreviewCheck, new RoutedEventArgs());
            } });
        AttachSearchMenu(MapSearchBox, "maps", () => new()
        {
            ["query"] = MapSearchBox.Text, ["version"] = MapGameVersionBox.Text
        }, values => { MapSearchBox.Text = values.GetValueOrDefault("query", ""); MapGameVersionBox.Text = values.GetValueOrDefault("version", ""); });

        InitializeVersionSortMenu();
        AddFilterChips(VersionSearchBox, new[] { (VersionSearchBox, "名称") }, new[] { (VersionTypeCombo, "分类") });
        AddFilterChips(ModSearchBox, new[] { (ModSearchBox, "名称"), (ModGameVersionBox, "版本") }, new[] { (ModSourceCombo, "来源"), (ModLoaderCombo, "加载器") });
        AddFilterChips(ResourceSearchBox, new[] { (ResourceSearchBox, "名称"), (ResourceGameVersionBox, "版本") }, new[] { (ResourceSourceCombo, "来源"), (ResourceTypeCombo, "类型") }, ResourceShowPreviewCheck);
        AddFilterChips(MapSearchBox, new[] { (MapSearchBox, "名称"), (MapGameVersionBox, "版本") }, Array.Empty<(ComboBox, string)>());
        Loaded += (_, _) => { foreach (var refresh in _refreshFilterChips) refresh(); RefreshExperiencePageButtons(); };
        Unloaded += (_, _) => InvalidateExperienceSearches();
    }
    private static readonly (string Key, string Label)[] VersionSortOptions =
    {
        ("newest", "发布时间：新 → 旧"), ("oldest", "发布时间：旧 → 新"),
        ("name-asc", "版本名称：A → Z"), ("name-desc", "版本名称：Z → A")
    };
    private void InitializeVersionSortMenu()
    {
        var menu = OnlineListBox.ContextMenu ??= new ContextMenu();
        void RefreshMenu(ContextMenu target)
        {
            foreach (var old in target.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "XCL2.VersionSort")).ToArray()) target.Items.Remove(old);
            var sorting = new MenuItem { Header = "排序方式", Tag = "XCL2.VersionSort", IsEnabled = _owner.ConfigService.Config.Ux50SearchStableSort };
            foreach (var option in VersionSortOptions)
            {
                var item = new MenuItem { Header = option.Label, IsCheckable = true,
                    IsChecked = _owner.ConfigService.Config.Ux50VersionListSort == option.Key };
                item.Click += (_, _) => ApplyVersionSort(option.Key);
                sorting.Items.Add(item);
            }
            target.Items.Add(sorting);
        }
        RefreshMenu(menu);
        menu.Opened += (_, _) => RefreshMenu(menu);
        // Existing version-row menus keep the Wiki action, and receive the same sort submenu.
        OnlineListBox.ContextMenuOpening += (_, args) =>
        {
            if (args.OriginalSource is not DependencyObject source) return;
            DependencyObject? parent = source;
            while (parent != null && parent is not Grid { ContextMenu: not null })
                parent = parent is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(parent) : LogicalTreeHelper.GetParent(parent);
            if (parent is Grid { ContextMenu: { } rowMenu }) RefreshMenu(rowMenu);
        };
        OnlineListBox.ToolTip = "右键可选择排序方式；默认按发布时间从新到旧。";
    }
    private void ApplyVersionSort(string key)
    {
        _owner.ConfigService.Config.Ux50VersionListSort = key;
        _versionPageIndex = 0;
        try { _owner.ConfigService.Save(); }
        catch (Exception ex) { LauncherLogService.AppendLine("[版本列表排序] 保存失败：" + ex.Message); }
        ApplyVersionFilter();
    }
    private string VersionSortCaption => !_owner.ConfigService.Config.Ux50SearchStableSort ? "新 → 旧"
        : VersionSortOptions.FirstOrDefault(option => option.Key == _owner.ConfigService.Config.Ux50VersionListSort).Label ?? "发布时间：新 → 旧";
    private List<VersionManifestEntry> OrderExperienceVersions(List<VersionManifestEntry> versions)
    {
        if (!_owner.ConfigService.Config.Ux50SearchStableSort) return versions;
        return _owner.ConfigService.Config.Ux50VersionListSort switch
        {
            "oldest" => ExperienceSearchState.StableOrder(versions, entry => entry.GetReleaseTimeOrMin(), false),
            "name-asc" => ExperienceSearchState.StableOrder(versions, entry => entry.Id, false, StringComparer.OrdinalIgnoreCase),
            "name-desc" => ExperienceSearchState.StableOrder(versions, entry => entry.Id, true, StringComparer.OrdinalIgnoreCase),
            _ => versions
        };
    }
    private static string ComboTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private static void SelectTag(ComboBox combo, string tag)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (item.Tag as string ?? "") == tag);
        // Saved filters may not launch a plugin picker as a side effect.
        if (item != null && !tag.StartsWith("plugin", StringComparison.Ordinal)) combo.SelectedItem = item;
    }
    private void AttachSearchMenu(TextBox box, string scope, Func<Dictionary<string, string>> capture, Action<Dictionary<string, string>> apply)
        => ExperienceSearchMenu.Attach(box, () => _owner.ConfigService.Config, () => _owner.ConfigService.Save(), scope,
            capture, values =>
            {
                _applyingSearchPreset = true;
                try { apply(values); }
                finally { _applyingSearchPreset = false; }
                _debounceTimer.Stop(); _pendingDebouncedAction = null; ReapplySearchFor(box, true);
            });
    private void CommitExperienceSearch(TextBox box)
    {
        string scope = box == VersionSearchBox ? "versions" : box == ModSearchBox ? "mods" : box == ResourceSearchBox ? "resources" : box == MapSearchBox ? "maps" : "";
        if (scope.Length != 0) ExperienceSearchMenu.Commit(_owner.ConfigService.Config, scope, box.Text, () => _owner.ConfigService.Save());
    }
    private void ReapplySearchFor(TextBox box, bool immediately = false)
    {
        if (!_initialized || IsSearchComposing) return;
        Action action;
        if (box == VersionSearchBox) { _versionPageIndex = 0; action = ApplyVersionFilter; }
        else if (box == ModSearchBox || box == ModGameVersionBox) { _modPageIndex = 0; action = () => _ = RunModSearchAsync(); }
        else if (box == MapSearchBox || box == MapGameVersionBox) { _mapPageIndex = 0; action = () => _ = RunMapSearchAsync(); }
        else { _resourcePageIndex = 0; action = () => _ = RunResourceSearchAsync(); }
        if (immediately) action(); else Debounce(action);
    }
    private void InvalidateExperienceSearches()
    {
        ++_modSearchSeq; ++_resourceSearchSeq; ++_mapSearchSeq;
        CancelSearch(_modSearchCancellation); CancelSearch(_resourceSearchCancellation); CancelSearch(_mapSearchCancellation);
        foreach (var scope in _pendingSearchScopes.ToArray()) RestoreCanceledSearch(scope);
    }
    private void RecordDisplayedSearchPage(string scope, int page, int loaded, int firstTotal, int secondTotal)
    {
        _displayedSearchPages[scope] = new(page, loaded, firstTotal, secondTotal);
        _pendingSearchScopes.Remove(scope);
    }
    private void RestoreCanceledSearch(string scope)
    {
        if (!_pendingSearchScopes.Remove(scope)) return;
        var page = _displayedSearchPages.GetValueOrDefault(scope);
        var recovery = ExperienceSearchState.RecoverCanceledPage(page, PageSize);
        var (index, summary, previous, next) = recovery;
        switch (scope)
        {
            case "mods":
                _modPageIndex = index; ModPageSummaryText.Text = summary;
                ModPrevPageButton.IsEnabled = previous; ModNextPageButton.IsEnabled = next;
                SetContextHint(ModListBox, "搜索已取消，可重新搜索。", "重新搜索", () => _ = RunModSearchAsync());
                break;
            case "resources":
                _resourcePageIndex = index; ResourcePageSummaryText.Text = summary;
                ResourcePrevPageButton.IsEnabled = previous; ResourceNextPageButton.IsEnabled = next;
                SetContextHint(ResourceListBox, "搜索已取消，可重新搜索。", "重新搜索", () => _ = RunResourceSearchAsync());
                break;
            case "maps":
                _mapPageIndex = index; MapPageSummaryText.Text = summary;
                MapPrevPageButton.IsEnabled = previous; MapNextPageButton.IsEnabled = next;
                SetContextHint(MapListBox, "搜索已取消，可重新搜索。", "重新搜索", () => _ = RunMapSearchAsync());
                break;
        }
    }
    private static void CancelSearch(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private static CancellationTokenSource BeginSearch(ref CancellationTokenSource? previous)
    {
        CancelSearch(previous);
        return previous = new CancellationTokenSource();
    }
    private void RefreshExperiencePageButtons()
    {
        bool enabled = _owner.ConfigService.Config.Ux50SearchLoadedCounts;
        foreach (var button in new[] { ModNextPageButton, ResourceNextPageButton, MapNextPageButton })
        {
            if (enabled)
            {
                button.Content = "加载下一页";
                button.ToolTip = "点击才请求下一页；显示的数量仅指本页实际返回的项目。";
                button.MinWidth = 96;
            }
            else
            {
                button.SetResourceReference(ContentControl.ContentProperty, "Str_Ui_Next");
                button.ClearValue(FrameworkElement.MinWidthProperty);
                button.ClearValue(FrameworkElement.ToolTipProperty);
            }
        }
    }
    private void AddFilterChips(TextBox anchor, (TextBox Box, string Label)[] boxes,
        (ComboBox Combo, string Label)[] combos, CheckBox? preview = null)
    {
        FrameworkElement? container = anchor;
        while (container?.Parent is FrameworkElement parent && parent is not Grid { Name: "VersionPanel" or "MapPanel" })
        {
            if (container is Border && container.Parent is Grid) break;
            container = parent;
        }
        if (container == null || container.Parent is not Grid host) return;
        int index = host.Children.IndexOf(container);
        var wrapper = new StackPanel();
        Grid.SetRow(wrapper, Grid.GetRow(container)); Grid.SetColumn(wrapper, Grid.GetColumn(container));
        Grid.SetColumnSpan(wrapper, Grid.GetColumnSpan(container));
        host.Children.Remove(container); wrapper.Children.Add(container); host.Children.Insert(index, wrapper);
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        wrapper.Children.Add(chips);
        void AddChip(string label, string value, Action clear)
        {
            var text = new TextBlock { Text = label + "：" + value + "  ×", TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap, MaxWidth = 210 };
            var button = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), MinHeight = 26,
                Margin = new Thickness(0, 0, 6, 4), ToolTip = "移除筛选：" + label + "（" + value + "）",
                Style = Application.Current.TryFindResource("SecondaryButton") as Style };
            System.Windows.Automation.AutomationProperties.SetName(button, "移除筛选 " + label + " " + value);
            button.Click += (_, _) => clear(); chips.Children.Add(button);
        }
        void Refresh()
        {
            chips.Children.Clear();
            if (_owner.ConfigService.Config.Ux50SearchFilterChips)
            {
                foreach (var pair in boxes.Where(pair => !string.IsNullOrWhiteSpace(pair.Box.Text)))
                    AddChip(pair.Label, pair.Box.Text, pair.Box.Clear);
                foreach (var pair in combos.Where(pair => pair.Combo.SelectedIndex > 0))
                    AddChip(pair.Label, (pair.Combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "", () => pair.Combo.SelectedIndex = 0);
                if (preview?.IsChecked == true) AddChip("版本", "包括预览版", () => { preview.IsChecked = false; ResourceShowPreview_Changed(preview, new RoutedEventArgs()); });
            }
            chips.Visibility = chips.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var pair in boxes) pair.Box.TextChanged += (_, _) => Refresh();
        foreach (var pair in combos) pair.Combo.SelectionChanged += (_, _) => Refresh();
        if (preview != null) { preview.Checked += (_, _) => Refresh(); preview.Unchecked += (_, _) => Refresh(); }
        _refreshFilterChips.Add(Refresh); Refresh();
    }
}
