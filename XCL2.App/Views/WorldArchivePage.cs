using System.IO;
using System.Diagnostics;
using System.Windows.Data;
using XCL2.App.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Views;

public sealed class WorldArchivePage : UserControl
{
    private readonly ListBox _worlds = new() { MinWidth = 180, BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _worldSourceFilter = new() { ItemsSource = new[] { "全部存档", "客户端", "服务器" }, SelectedIndex = 0 };
    private readonly TextBlock _worldTitle = new() { Text = "选择一个世界", FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _worldSubtitle = new() { TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 6, 0, 0) };
    private readonly WrapPanel _quickActions = new() { IsEnabled = false, Margin = new Thickness(0, 10, 0, 0) };
    private readonly WrapPanel _toolbar = new();
    private readonly TextBlock _actionHint = new() { Text = "先在左侧选择一个存档，即可使用打开目录、备份、修复与保存。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75, Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock _emptyEditor = new() { Text = "尚未选择存档。请在左侧列表选择；也可使用上方“添加存档目录”。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 400, Opacity = .75 };
    private readonly Grid _body = new();
    private readonly Popup _filterPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly Border _sidebarCard = new();
    private readonly TabControl _tabs = new();
    private bool _busy, _loadedOnce, _loadingSelection;
    private int _selectionGeneration;
    private string _draftBaseline = "";
    private readonly TextBox _worldSearch = new() { Name = "WorldSearchBox", ToolTip = "按存档名称、目录／实例、版本、种子搜索；空格分隔多个词" };
    private readonly ComboBox _worldModeFilter = new() { ItemsSource = new[] { "全部游戏模式", "生存", "创造", "冒险", "旁观", "极限模式" }, SelectedIndex = 0 };
    private readonly ComboBox _worldVersionFilter = new();
    private readonly ComboBox _worldSort = new() { ItemsSource = new[] { "最近修改优先", "按存档名称" }, SelectedIndex = 0 };
    private readonly TextBlock _worldCount = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), Opacity = .75 };
    private readonly TextBlock _worldEmpty = new() { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12), Visibility = Visibility.Collapsed };
    private readonly TextBlock _selectionHint = new() { TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _settingSearch = new() { Name = "WorldSettingSearchBox", ToolTip = "搜索设置名称或字段，例如 种子、出生点、keepInventory、player.health" };
    private readonly TextBlock _settingSearchInfo = new() { Margin = new Thickness(0, 5, 0, 0), Opacity = .75, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _settingsContent = new() { Margin = new Thickness(16), IsEnabled = false };
    private readonly List<(FrameworkElement Row, Func<string> Keywords)> _settingRows = new();
    private readonly List<(Expander Group, FrameworkElement[] Rows)> _settingGroups = new();
    private readonly Dictionary<Expander, bool> _expandedBeforeSearch = new();
    private List<WorldArchive> _allWorlds = new();
    private bool _filteringWorlds;
    private readonly TextBox _name = new();
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "生存", "创造", "冒险", "旁观" } };
    private readonly ComboBox _difficulty = new() { ItemsSource = new[] { "和平", "简单", "普通", "困难" } };
    private readonly CheckBox _cheats = new() { Content = "允许作弊" }, _hardcore = new() { Content = "极限模式" };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap }, _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _nbt = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Image _projection = new() { Stretch = Stretch.Uniform, MinHeight = 220 };
    private readonly TextBlock _projectionInfo = new() { Text = "尚未打开结构文件。支持 .litematic、.nbt、.schematic 和 .schem；选择文件后会显示俯视投影及材料清单。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Opacity = .75 };
    private readonly TextBlock _materials = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WorldMapPanel _worldMap = new() { Name = "WorldMapPreview" };
    private readonly WrapPanel _actions = new();
    private readonly Dictionary<string, TextBox> _extraFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _extraFlags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _initialFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _initialFlags = new(StringComparer.Ordinal);
    private readonly TextBox _rules = new() { AcceptsReturn = true, MinHeight = 140, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
    private readonly StackPanel _playerFields = new();
    private WorldArchive? _selected;
    public WorldArchivePage()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
        var page = new Grid { Margin = new Thickness(20), ClipToBounds = true };
        page.RowDefinitions.Add(new() { Height = GridLength.Auto }); page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = "世界与存档", FontSize = 28, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock { Text = "管理客户端与服务器的 Java 版世界，让每一次探索都有备份。", Opacity = .7, Margin = new Thickness(0, 4, 0, 12) });
        var toolbar = _toolbar;
        Add(toolbar, "刷新", async () => await RefreshAsync());
        Add(toolbar, "添加存档目录", async () => { var picker = new XCL2.App.Services.CompatibleFolderDialog { Title = "选择客户端或服务器的 Java 版世界目录（包含 level.dat）" }; if (ShowOwnedDialog(picker) == true) await RunAsync(async () => { Select(await Task.Run(() => WorldArchiveService.Read(picker.FolderName))); _tabs.SelectedIndex = 0; _status.Text = "已打开存档，可编辑、备份或检查修复。"; }); });
        Add(toolbar, "修复其他存档", RepairOtherAsync);
        Add(toolbar, "查看投影 / 结构文件", OpenStructureAsync); header.Children.Add(toolbar); page.Children.Add(header);
        var body = _body; body.Margin = new Thickness(0, 10, 0, 10);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(252), MinWidth = 210 }); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 }); Grid.SetRow(body, 1); page.Children.Add(body);
        var worldSidebar = new Grid { Margin = new Thickness(12), MinHeight = 0 };
        worldSidebar.RowDefinitions.Add(new() { Height = GridLength.Auto }); worldSidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        worldSidebar.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 }); worldSidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _sidebarCard.Child = worldSidebar; _sidebarCard.CornerRadius = new CornerRadius(12); _sidebarCard.BorderThickness = new Thickness(1);
        _sidebarCard.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); _sidebarCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2"); body.Children.Add(_sidebarCard);
        _worlds.ItemTemplate = WorldCardTemplate();
        _worlds.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _worlds.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _worlds.SetValue(ScrollViewer.CanContentScrollProperty, true);
        _worlds.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        var search = new StackPanel(); Label(search, "搜索存档", _worldSearch); worldSidebar.Children.Add(search);
        var filterSection = new StackPanel { Margin = new Thickness(0, 8, 0, 8) }; Grid.SetRow(filterSection, 1); worldSidebar.Children.Add(filterSection);
        var filters = new StackPanel(); Label(filters, "存档来源", _worldSourceFilter); Label(filters, "游戏模式", _worldModeFilter);
        Label(filters, "版本", _worldVersionFilter); Label(filters, "排序", _worldSort);
        var filterActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Add(filterActions, "清空筛选", () => { _worldSearch.Clear(); _worldSourceFilter.SelectedIndex = 0; _worldModeFilter.SelectedIndex = 0; _worldVersionFilter.SelectedIndex = 0; return Task.CompletedTask; }, quiet: true);
        filters.Children.Add(filterActions);
        var filterToggle = new Button { Content = "筛选与排序 ▾", FontSize = 13, MinHeight = 32, Padding = new Thickness(11, 5, 11, 5), HorizontalAlignment = HorizontalAlignment.Left };
        filterToggle.SetResourceReference(StyleProperty, "CompactActionButton");
        var filterCard = Card(new ScrollViewer { Content = filters, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        filterCard.Padding = new Thickness(12); filterCard.Width = 252;
        _filterPopup.PlacementTarget = filterToggle; _filterPopup.Child = filterCard;
        filterToggle.Click += (_, _) => _filterPopup.IsOpen = !_filterPopup.IsOpen;
        filterSection.Children.Add(filterToggle); filterSection.Children.Add(_filterPopup); filterSection.Children.Add(_worldCount);
        var worldList = new Grid { MinHeight = 0, ClipToBounds = true }; Grid.SetRow(worldList, 2); worldList.Children.Add(_worlds);
        _worldEmpty.IsHitTestVisible = false; worldList.Children.Add(_worldEmpty); worldSidebar.Children.Add(worldList);
        Grid.SetRow(_selectionHint, 3); _selectionHint.FontSize = 11; _selectionHint.MaxHeight = 22; _selectionHint.TextWrapping = TextWrapping.NoWrap; _selectionHint.TextTrimming = TextTrimming.CharacterEllipsis; worldSidebar.Children.Add(_selectionHint);
        _worlds.SelectionChanged += (_, _) => { if (!_filteringWorlds && _worlds.SelectedItem is WorldArchive world) { try { Select(world); } catch (Exception ex) { _status.Text = ex.Message; } } };
        _worldSearch.TextChanged += (_, _) => ApplyWorldFilters();
        _worldSearch.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { _worldSearch.Clear(); e.Handled = true; } };
        _worldSourceFilter.SelectionChanged += (_, _) => ApplyWorldFilters();
        _worldModeFilter.SelectionChanged += (_, _) => ApplyWorldFilters();
        _worldVersionFilter.SelectionChanged += (_, _) => ApplyWorldFilters();
        _worldSort.SelectionChanged += (_, _) => ApplyWorldFilters();
        UpdateVersionFilters();
        var tabs = _tabs; tabs.Margin = new Thickness(12, 0, 0, 0); tabs.HorizontalContentAlignment = HorizontalAlignment.Stretch; tabs.VerticalContentAlignment = VerticalAlignment.Stretch; Grid.SetColumn(tabs, 1); body.Children.Add(tabs);
        var settings = _settingsContent;
        var overview = new StackPanel { Margin = new Thickness(14) }; overview.Children.Add(_worldTitle); overview.Children.Add(_worldSubtitle);
        overview.Children.Add(_info);
        Add(_quickActions, "打开文件夹", OpenSelectedFolderAsync); Add(_quickActions, "备份与恢复", ManageBackupsAsync); Add(_quickActions, "健康检查与修复", RepairSelectedAsync); overview.Children.Add(_quickActions);
        Setting(settings, "存档名称", _name, "name LevelName"); Setting(settings, "游戏模式", _mode, "gameMode GameType"); Setting(settings, "难度", _difficulty, "difficulty");
        Setting(settings, "", _cheats, "允许作弊 allowCommands"); Setting(settings, "", _hardcore, "极限模式 hardcore");
        var advanced = new StackPanel();
        foreach (var field in WorldArchiveService.EditableFields.Where(f => f.Key is not "name" and not "gameMode" and not "difficulty" and not "allowCommands" and not "hardcore" and not "gameRules"))
        {
            var panel = field.Key.StartsWith("player.", StringComparison.Ordinal) ? _playerFields : advanced;
            if (field.Type == "bool")
            {
                var check = new CheckBox { Content = field.Label, IsThreeState = true, Margin = new Thickness(0, 8, 0, 4) };
                _extraFlags[field.Key] = check; Setting(panel, "", check, field.Label + " " + field.Key, field.Note);
            }
            else
            {
                var box = new TextBox { ToolTip = field.Note ?? $"可选；范围 {field.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "不限"}–{field.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "不限"}" };
                _extraFields[field.Key] = box; Setting(panel, field.Label, box, field.Key, field.Note);
            }
        }
        AddSettingGroup(settings, "时间、天气、种子、出生点与世界边界", advanced, advanced);
        var gameRules = new StackPanel();
        gameRules.Children.Add(new TextBlock { Text = "每行填写 规则名称=true/false 或非负整数，名称区分大小写。可加入当前版本支持的新规则；未列出或删除的行保留原值。", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 4, 0, 8) });
        Setting(gameRules, "", _rules, "游戏规则 gameRules keepInventory doDaylightCycle randomTickSpeed", extraKeywords: () => _rules.Text);
        AddSettingGroup(settings, "游戏规则（例如 keepInventory、doDaylightCycle、randomTickSpeed）", gameRules, gameRules);
        var playerPanel = new StackPanel(); playerPanel.Children.Add(new TextBlock { Text = "编辑 level.dat 中的单人玩家生命、饥饿、经验、坐标和模式。不修改多人 playerdata；移动坐标前请确认目标安全。", TextWrapping = TextWrapping.Wrap, Opacity = .7 }); playerPanel.Children.Add(_playerFields);
        AddSettingGroup(settings, "单人玩家数据", playerPanel, _playerFields);
        settings.Children.Add(new TextBlock { Text = "留空或三态勾选的中间状态表示保留原值。保存前请退出游戏。修改 level.dat 会自动备份；副本保留已有区块。修改种子只影响之后生成的区块，可能产生地形接缝。", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 12, 0, 12) });
        Add(_actions, "保存设置", SaveAsync, primary: true); Add(_actions, "另存为副本", CopyAsync);
        var settingTab = new Grid { MinHeight = 0 };
        settingTab.RowDefinitions.Add(new() { Height = GridLength.Auto }); settingTab.RowDefinitions.Add(new() { Height = GridLength.Auto });
        settingTab.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 }); settingTab.RowDefinitions.Add(new() { Height = GridLength.Auto });
        settingTab.Children.Add(Card(overview));
        var settingSearchBar = new Grid { Margin = new Thickness(16, 10, 16, 4) }; Grid.SetRow(settingSearchBar, 1);
        settingSearchBar.RowDefinitions.Add(new() { Height = GridLength.Auto }); settingSearchBar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        settingSearchBar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); settingSearchBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _settingSearch.ToolTip = "搜索当前存档字段；筛选保留尚未保存的修改"; _settingSearch.MinHeight = 30; settingSearchBar.Children.Add(_settingSearch);
        var clearSearch = new WrapPanel { Margin = new Thickness(6, 0, 0, 0) }; Grid.SetColumn(clearSearch, 1);
        Add(clearSearch, "清除", () => { _settingSearch.Clear(); return Task.CompletedTask; }, quiet: true); settingSearchBar.Children.Add(clearSearch);
        _settingSearchInfo.FontSize = 11; Grid.SetRow(_settingSearchInfo, 1); Grid.SetColumnSpan(_settingSearchInfo, 2); settingSearchBar.Children.Add(_settingSearchInfo); settingTab.Children.Add(settingSearchBar);
        var editor = new Grid { MinHeight = 0 }; Grid.SetRow(editor, 2);
        editor.Children.Add(new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false });
        _emptyEditor.IsHitTestVisible = false; editor.Children.Add(_emptyEditor); settingTab.Children.Add(editor);
        var footer = new StackPanel { Margin = new Thickness(16, 8, 16, 8) }; footer.Children.Add(_actionHint); footer.Children.Add(_actions); Grid.SetRow(footer, 3); settingTab.Children.Add(footer);
        _settingSearch.TextChanged += (_, _) => ApplySettingFilter();
        _settingSearch.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { _settingSearch.Clear(); e.Handled = true; } };
        tabs.Items.Add(new TabItem { Header = "世界设置", Content = settingTab });
        var preview = new StackPanel { Margin = new Thickness(16) };
        preview.Children.Add(new TextBlock { Text = "结构投影与材料", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        preview.Children.Add(_projectionInfo);
        var projectionActions = new WrapPanel(); Add(projectionActions, "打开投影 / 结构文件", OpenStructureAsync); preview.Children.Add(projectionActions);
        preview.Children.Add(new TextBlock { Text = "俯视投影 · 颜色区分方块类型（非游戏材质）", Margin = new Thickness(0, 12, 0, 8), Opacity = .7 });
        var projectionCard = Card(_projection); projectionCard.Padding = new Thickness(12); preview.Children.Add(projectionCard);
        _projection.ToolTip = "从上往下显示各位置最高的非空气方块，颜色由方块类型决定；这是结构文件的预览，不会修改世界。";
        _materials.Margin = new Thickness(0, 12, 0, 0); _materials.Text = "打开结构文件后显示材料清单。"; preview.Children.Add(_materials);
        tabs.Items.Add(new TabItem { Header = "投影 / 材料", Content = new ScrollViewer { Content = preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "NBT 结构", Content = _nbt });
        tabs.Items.Add(new TabItem { Header = "二维地图", Content = _worldMap });
        _status.Margin = new Thickness(12, 8, 12, 8);
        var statusCard = Card(_status); Grid.SetRow(statusCard, 2); page.Children.Add(statusCard); Content = page;
        UpdateEditorState(); ApplyWorldFilters(); ApplySettingFilter();
        Loaded += async (_, _) => { if (_loadedOnce) return; _loadedOnce = true; await RefreshAsync(); };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Unloaded += (_, _) => { _filterPopup.IsOpen = false; ++_selectionGeneration; };
        _name.TextChanged += (_, _) => WorldDraftChanged(); _rules.TextChanged += (_, _) => WorldDraftChanged();
        _mode.SelectionChanged += (_, _) => WorldDraftChanged(); _difficulty.SelectionChanged += (_, _) => WorldDraftChanged();
        foreach (var field in _extraFields.Values) field.TextChanged += (_, _) => WorldDraftChanged();
        foreach (var flag in _extraFlags.Values.Concat(new[] { _cheats, _hardcore }))
        { flag.Checked += (_, _) => WorldDraftChanged(); flag.Unchecked += (_, _) => WorldDraftChanged(); flag.Indeterminate += (_, _) => WorldDraftChanged(); }
    }
    private string CaptureDraft() => JsonSerializer.Serialize(new
    {
        Name = _name.Text, Mode = _mode.SelectedIndex, Difficulty = _difficulty.SelectedIndex,
        Cheats = _cheats.IsChecked, Hardcore = _hardcore.IsChecked, Rules = _rules.Text,
        Fields = _extraFields.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value.Text }).ToArray(),
        Flags = _extraFlags.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value.IsChecked }).ToArray()
    });
    private bool HasDraft => _selected != null && _draftBaseline.Length != 0 && CaptureDraft() != _draftBaseline;
    private void WorldDraftChanged() { if (!_loadingSelection) UpdateEditorState(); }
    private void UpdateEditorState()
    {
        var selected = _selected != null; var ready = selected && !_busy;
        _settingsContent.IsEnabled = ready; _settingsContent.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        _emptyEditor.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        _actions.IsEnabled = _quickActions.IsEnabled = ready; _toolbar.IsEnabled = !_busy; _worlds.IsEnabled = !_busy;
        foreach (var control in new Control[] { _worldSearch, _worldSourceFilter, _worldModeFilter, _worldVersionFilter, _worldSort }) control.IsEnabled = !_busy;
        var hint = _busy ? "正在处理存档，请稍候。完成后按钮会恢复。"
            : !selected ? "先在左侧选择存档；选中后可打开目录、备份、检查修复或保存。"
            : HasDraft ? "有未保存的修改。保存前自动备份 level.dat；也可另存为副本。"
            : "所有操作针对当前存档。修改与恢复前请退出游戏或停止服务器。";
        _actionHint.Text = hint;
        foreach (var button in _actions.Children.OfType<Button>().Concat(_quickActions.Children.OfType<Button>())) button.ToolTip = ready ? ActionDescription(button.Content?.ToString() ?? "") : hint;
    }
    private void UpdateResponsiveLayout()
    {
        var narrow = ActualWidth > 0 && ActualWidth < 760;
        if (_body.RowDefinitions.Count < 2) _body.RowDefinitions.Add(new() { Height = new GridLength(0) });
        _body.RowDefinitions[0].Height = narrow ? new GridLength(252) : new GridLength(1, GridUnitType.Star);
        _body.RowDefinitions[1].Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _body.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(252);
        _body.ColumnDefinitions[0].MinWidth = narrow ? 0 : 210;
        Grid.SetColumn(_sidebarCard, 0); Grid.SetColumnSpan(_sidebarCard, narrow ? 2 : 1);
        Grid.SetRow(_tabs, narrow ? 1 : 0); Grid.SetColumn(_tabs, narrow ? 0 : 1); Grid.SetColumnSpan(_tabs, narrow ? 2 : 1);
        _tabs.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
    }
    private void RestoreListSelection()
    {
        _filteringWorlds = true;
        try { _worlds.SelectedItem = _worlds.Items.OfType<WorldArchive>().FirstOrDefault(w => string.Equals(w.Path, _selected?.Path, StringComparison.OrdinalIgnoreCase)); }
        finally { _filteringWorlds = false; }
    }
    private async Task LoadNbtPreviewAsync(string path, int generation)
    {
        try
        {
            var text = await Task.Run(() => NbtDocument.Load(Path.Combine(path, "level.dat")).Describe());
            if (generation == _selectionGeneration) _nbt.Text = text;
        }
        catch (Exception ex) { if (generation == _selectionGeneration) _nbt.Text = "NBT 预览暂不可用：" + ex.Message + "\n可使用健康检查与修复检查此目录。"; }
    }
    private static void Label(Panel panel, string label, UIElement control)
    { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4) }); panel.Children.Add(control); }
    private static void Add(Panel panel, string label, Func<Task> action, bool primary = false, bool quiet = false)
    {
        var button = new Button { Content = label, ToolTip = ActionDescription(label), Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(11, 5, 11, 5), MinHeight = 32, FontSize = 13, FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : quiet ? "QuietActionButton" : "CompactActionButton");
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { ToastService.ShowWarning(ex.Message); } }; panel.Children.Add(button);
    }
    private static string ActionDescription(string label) => label switch
    {
        "刷新" => "重新扫描已配置的客户端与服务器世界；当前尚未保存的编辑会保留。",
        "添加存档目录" => "选择包含 level.dat 的 Java 版世界目录，在此页面编辑或备份。",
        "修复其他存档" => "直接选择无法正常读取的世界目录，检查元数据并尝试从备份恢复。",
        "查看投影 / 结构文件" or "打开投影 / 结构文件" => "打开 litematic、NBT、schematic 或 schem 文件，查看俯视投影、材料数量及 NBT 结构。",
        "打开文件夹" => "在资源管理器中打开当前世界目录。",
        "备份与恢复" => "备份磁盘中已保存的世界、恢复为独立副本，或设置自动备份；未保存的编辑不会进入备份。",
        "健康检查与修复" => "只读检查世界文件；如有可用元数据备份，可先备份当前文件再恢复。",
        "保存设置" => "将当前修改写入 level.dat，并自动保存修改前的元数据备份。请先退出游戏。",
        "另存为副本" => "将整个世界复制到新目录并应用当前设置，原存档保持不变。请先退出游戏。",
        "清空筛选" => "清除存档名称、来源、游戏模式及版本筛选，显示全部已发现的世界。",
        "清除" => "清除字段搜索，重新显示全部世界设置。",
        _ => label
    };
    private MainWindow? LauncherOwner => Window.GetWindow(this) as MainWindow ?? Application.Current.MainWindow as MainWindow;
    private bool? ShowOwnedDialog(Microsoft.Win32.CommonDialog dialog) => LauncherOwner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
    private static Border Card(UIElement content)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2"); return card;
    }
    private static DataTemplate WorldCardTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel)); root.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 10, 10, 12));
        root.SetBinding(ToolTipProperty, new Binding("Path"));
        void Text(string binding, double size, bool bold, bool subdued)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(binding));
            text.SetValue(TextBlock.FontSizeProperty, size); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
            if (bold) text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            if (subdued) text.SetValue(UIElement.OpacityProperty, .65);
            root.AppendChild(text);
        }
        Text("Settings.Name", 15, true, false); Text("Source", 11, false, true); Text("Settings.Version", 12, false, true);
        return new DataTemplate { VisualTree = root };
    }
    private void Setting(Panel panel, string label, UIElement control, string keywords, string? note = null, Func<string>? extraKeywords = null)
    {
        var row = new StackPanel();
        if (!string.IsNullOrEmpty(label)) row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4) });
        row.Children.Add(control);
        if (note != null) row.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        panel.Children.Add(row);
        _settingRows.Add((row, () => label + " " + keywords + " " + note + " " + extraKeywords?.Invoke()));
    }
    private void AddSettingGroup(Panel parent, string header, Panel content, Panel fields)
    {
        var group = new Expander { Header = header, Content = content, Margin = new Thickness(0, 12, 0, 8) };
        parent.Children.Add(group);
        _settingGroups.Add((group, _settingRows.Where(r => ReferenceEquals(r.Row.Parent, fields)).Select(r => r.Row).ToArray()));
    }
    private void ApplySettingFilter()
    {
        var query = _settingSearch.Text.Trim();
        var searching = query.Length != 0;
        if (searching && _expandedBeforeSearch.Count == 0)
            foreach (var (group, _) in _settingGroups) _expandedBeforeSearch[group] = group.IsExpanded;
        var visible = 0;
        foreach (var (row, keywords) in _settingRows)
        {
            var matches = WorldSearch.MatchesText(query, keywords());
            row.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            if (matches) visible++;
        }
        foreach (var (group, rows) in _settingGroups)
        {
            var matches = rows.Any(row => row.Visibility == Visibility.Visible);
            group.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            if (searching && matches) group.IsExpanded = true;
            else if (!searching && _expandedBeforeSearch.TryGetValue(group, out var wasExpanded)) group.IsExpanded = wasExpanded;
        }
        if (!searching) _expandedBeforeSearch.Clear();
        _settingSearchInfo.Text = !searching ? "按设置名称或字段查找；筛选不会丢弃尚未保存的修改。"
            : visible == 0 ? "没有匹配的设置，请换个关键词或清除搜索。" : $"找到 {visible} 项设置；匹配的分组已展开。";
    }
    private void UpdateVersionFilters()
    {
        var previous = (_worldVersionFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        _filteringWorlds = true;
        try
        {
            _worldVersionFilter.Items.Clear();
            _worldVersionFilter.Items.Add(new ComboBoxItem { Content = "全部版本", Tag = "" });
            foreach (var version in _allWorlds.Select(w => w.Settings.Version).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                _worldVersionFilter.Items.Add(new ComboBoxItem { Content = version, Tag = version });
            _worldVersionFilter.SelectedItem = _worldVersionFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, previous, StringComparison.OrdinalIgnoreCase))
                ?? _worldVersionFilter.Items[0];
        }
        finally { _filteringWorlds = false; }
    }
    private void ApplyWorldFilters()
    {
        if (_filteringWorlds) return;
        var version = (_worldVersionFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var source = _worldSourceFilter.SelectedItem as string;
        var matches = _allWorlds.Where(w => (_worldSourceFilter.SelectedIndex == 0 || w.Source == source) && WorldSearch.Matches(w, _worldSearch.Text, _worldModeFilter.SelectedIndex, version));
        var filtered = (_worldSort.SelectedIndex == 1 ? matches.OrderBy(w => w.Settings.Name, StringComparer.CurrentCultureIgnoreCase)
            : matches.OrderByDescending(w => w.ModifiedUtc)).ToArray();
        _filteringWorlds = true;
        try
        {
            _worlds.ItemsSource = filtered;
            _worlds.SelectedItem = filtered.FirstOrDefault(w => string.Equals(w.Path, _selected?.Path, StringComparison.OrdinalIgnoreCase));
        }
        finally { _filteringWorlds = false; }
        _worldCount.Text = $"显示 {filtered.Length} / {_allWorlds.Count} 个存档";
        _worldEmpty.Text = _allWorlds.Count == 0 ? "没有发现存档。可刷新或打开其他存档目录。" : "没有匹配的存档，请清空筛选或更换关键词。";
        _worldEmpty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _selectionHint.ToolTip = _selected?.Path;
        _selectionHint.Text = _selected == null ? "选择左侧存档后编辑。" : "当前编辑：" + _selected.Settings.Name +
            (_worlds.SelectedItem == null ? "（当前筛选未显示；未保存修改仍保留）" : "");
    }
    private void Select(WorldArchive world, bool reload = false)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Select(world, reload)); return; }
        var sameWorld = string.Equals(_selected?.Path, world.Path, StringComparison.OrdinalIgnoreCase);
        if (sameWorld && !reload) { RestoreListSelection(); return; }
        if (!reload && HasDraft && !MessageBoxDialog.ShowConfirm("当前存档有未保存的修改。放弃这些修改并切换存档？", "切换存档")) { RestoreListSelection(); return; }
        _loadingSelection = true;
        try
        {
        var previous = _allWorlds.FirstOrDefault(w => string.Equals(w.Path, world.Path, StringComparison.OrdinalIgnoreCase));
        if (previous?.Source == "服务器") world = world with { Source = "服务器" };
        _selected = world; _name.Text = world.Settings.Name; _mode.SelectedIndex = world.Settings.GameMode;

        _worldTitle.Text = world.Settings.Name;
        _worldSubtitle.FontSize = 11; _worldSubtitle.Text = world.Source + "世界 · " + world.Settings.Version + " · 最近修改 " + world.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var index = _allWorlds.FindIndex(w => string.Equals(w.Path, world.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _allWorlds[index] = world; else _allWorlds.Add(world);
        UpdateVersionFilters(); ApplyWorldFilters();
        _difficulty.SelectedIndex = world.Settings.Difficulty; _cheats.IsChecked = world.Settings.AllowCommands; _hardcore.IsChecked = world.Settings.Hardcore;
        var snapshot = JsonSerializer.SerializeToNode(world.Settings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        JsonNode? Field(string key) { var parts = key.Split('.'); return parts.Length == 1 ? snapshot[parts[0]] : snapshot[parts[0]]?[parts[1]]; }
        _initialFields.Clear(); _initialFlags.Clear();
        foreach (var pair in _extraFields) { pair.Value.Text = Field(pair.Key)?.ToString() ?? ""; _initialFields[pair.Key] = pair.Value.Text; }
        foreach (var pair in _extraFlags) { pair.Value.IsChecked = Field(pair.Key)?.GetValue<bool>(); _initialFlags[pair.Key] = pair.Value.IsChecked; }
        _rules.Text = string.Join("\n", world.Settings.GameRules.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
        _playerFields.IsEnabled = world.Settings.Player != null;
        _info.Margin = new Thickness(0, 6, 0, 0); _info.FontSize = 11; _info.Opacity = .7;
        _info.TextTrimming = TextTrimming.CharacterEllipsis; _info.MaxHeight = 36; _info.ToolTip = world.Path;
        _info.Text = $"种子：{world.Settings.Seed?.ToString() ?? "未知"} · DataVersion {world.Settings.DataVersion}\n{world.Path}";
        _nbt.Text = "正在读取 NBT 结构…";
        _ = LoadNbtPreviewAsync(world.Path, ++_selectionGeneration);
        _worldMap.SetWorld(world);
        ApplySettingFilter(); _draftBaseline = CaptureDraft();
        }
        finally { _loadingSelection = false; UpdateEditorState(); }
    }
    private WorldEdit Edit()
    {
        var obj = new JsonObject { ["name"] = _name.Text.Trim(), ["gameMode"] = _mode.SelectedIndex, ["difficulty"] = _difficulty.SelectedIndex, ["allowCommands"] = _cheats.IsChecked == true, ["hardcore"] = _hardcore.IsChecked == true };
        void Put(string key, JsonNode? value)
        {
            var parts = key.Split('.');
            if (parts.Length == 1) obj[key] = value;
            else { if (obj[parts[0]] == null) obj[parts[0]] = new JsonObject(); obj[parts[0]]![parts[1]] = value; }
        }
        foreach (var field in WorldArchiveService.EditableFields)
        {
            if (_extraFields.TryGetValue(field.Key, out var box) && !string.IsNullOrWhiteSpace(box.Text) && box.Text.Trim() != _initialFields.GetValueOrDefault(field.Key))
            {
                var text = box.Text.Trim(); JsonNode value;
                try
                {
                    value = field.Type switch
                    {
                        "long" => JsonValue.Create(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture))!,
                        "int" => JsonValue.Create(int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture))!,
                        "float" => JsonValue.Create(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))!,
                        _ => JsonValue.Create(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))!
                    };
                }
                catch (Exception ex) when (ex is FormatException or OverflowException) { throw new ArgumentException(field.Label + "：请输入有效数字。", ex); }
                Put(field.Key, value);
            }
            if (_extraFlags.TryGetValue(field.Key, out var flag) && flag.IsChecked.HasValue && flag.IsChecked != _initialFlags.GetValueOrDefault(field.Key)) Put(field.Key, JsonValue.Create(flag.IsChecked.Value));
        }
        var rules = new JsonObject(); var ruleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in _rules.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = line.Trim(); if (entry.Length == 0) continue;
            var index = entry.IndexOf('='); if (index <= 0) throw new ArgumentException("游戏规则应为 名称=值，每行一条。");
            var key = entry[..index].Trim(); var value = entry[(index + 1)..].Trim();
            if (!ruleNames.Add(key)) throw new ArgumentException("游戏规则重复：" + key);
            if (_selected?.Settings.GameRules.GetValueOrDefault(key) != value) rules[key] = value;
        }
        if (rules.Count > 0) obj["gameRules"] = rules;
        var edit = obj.Deserialize<WorldEdit>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        WorldArchiveService.Validate(edit); return edit;
    }
    private async Task RefreshAsync()
    {
        var roots = WorldCatalog.GameRoots(); var warnings = new List<string>();
        var serverRoots = LauncherOwner?.ServerInstanceService.Instances.Select(s => s.Directory).ToArray() ?? Array.Empty<string>();
        await RunAsync(async () =>
        {
            _allWorlds = await Task.Run(() => WorldArchiveService.Discover(roots, warnings.Add)
                .Concat(WorldArchiveService.DiscoverServerWorlds(serverRoots, warnings.Add))
                .GroupBy(w => w.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).OrderByDescending(w => w.ModifiedUtc).ToList());
            if (_selected != null && _allWorlds.All(w => !string.Equals(w.Path, _selected.Path, StringComparison.OrdinalIgnoreCase))) _allWorlds.Add(_selected);
            UpdateVersionFilters(); ApplyWorldFilters();
            if (_selected == null && _worlds.Items.OfType<WorldArchive>().FirstOrDefault() is { } first) Select(first);
            _status.Text = warnings.Count == 0 ? "已载入客户端与服务器存档。选择世界后可编辑、备份或检查修复。" : string.Join("\n", warnings.Take(5));
        });
    }
    private Task OpenSelectedFolderAsync()
    {
        if (_selected != null) { WorldArchiveService.RejectLinks(_selected.Path); Process.Start(new ProcessStartInfo(_selected.Path) { UseShellExecute = true }); _status.Text = "已打开存档目录：" + _selected.Path; }
        else _status.Text = "请先选择要打开的存档。";
        return Task.CompletedTask;
    }
    private async Task RepairSelectedAsync()
    {
        if (_selected == null) { _status.Text = "请先选择要检查的存档。"; return; }
        if (LauncherOwner is not { } owner) { _status.Text = "无法找到启动器主窗口，请重新打开存档页。"; return; }
        if (HasDraft && await MessageBoxDialog.ShowConfirmAsync("当前有未保存的修改。修复成功后会重新读取磁盘存档并放弃这些修改，是否继续检查与修复？", "检查与修复存档") != true) return;
        var directory = _selected.Path; var dialog = new WorldRepairDialog(owner, directory);
        await OverlayDialogService.ShowModalAsync(dialog);
        if (dialog.Repaired) await RunAsync(async () => { Select(await Task.Run(() => WorldArchiveService.Read(directory)), reload: true); _status.Text = "元数据已修复，当前存档信息已重新读取。"; });
        else _status.Text = "健康检查已关闭，当前存档保持不变。";
    }
    private async Task RepairOtherAsync()
    {
        var picker = new XCL2.App.Services.CompatibleFolderDialog { Title = "选择需要检查或修复的客户端／服务器世界目录" };
        if (LauncherOwner is not { } owner) { _status.Text = "无法找到启动器主窗口，请重新打开存档页。"; return; }
        if (picker.ShowDialog(owner) != true) return;
        var sameWorld = string.Equals(_selected?.Path, picker.FolderName, StringComparison.OrdinalIgnoreCase);
        if (sameWorld && HasDraft && await MessageBoxDialog.ShowConfirmAsync("修复成功后将重新读取此存档，当前未保存的修改会被放弃。是否继续？", "检查与修复存档") != true) return;
        var dialog = new WorldRepairDialog(owner, picker.FolderName); await OverlayDialogService.ShowModalAsync(dialog);
        if (dialog.Repaired) await RunAsync(async () => { Select(await Task.Run(() => WorldArchiveService.Read(picker.FolderName)), reload: sameWorld); _tabs.SelectedIndex = 0; _status.Text = "元数据已修复，已打开修复后的存档。"; });
        else _status.Text = "健康检查已关闭，存档保持不变。";
    }
    private async Task<bool> EnsureStoppedAsync(string directory)
    {
        if (LauncherOwner is not { } owner) { _status.Text = "无法确认游戏运行状态，请重新打开存档页后重试。"; return false; }
        await owner.ProcessManager.RefreshAsync();
        var full = Path.GetFullPath(directory);
        var serverRunning = owner.ServerInstanceService.Instances.Any(s => owner.ServerProcessManager.IsRunning(s.Id) &&
        (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Directory)), StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        if (serverRunning || ScheduledSaveBackupService.IsWorldRunning(owner, full)) { _status.Text = "请先退出使用此存档的游戏或停止服务器，再修改或生成副本。"; return false; }
        return true;
    }
    private async Task ManageBackupsAsync()
    {
        if (_selected == null) { _status.Text = "请先选择存档。"; return; }
        var world = _selected;
        if (LauncherOwner is not { } owner) { _status.Text = "无法找到启动器主窗口，请重新打开存档页。"; return; }
        await RunAsync(async () =>
        {
            await OverlayDialogService.ShowModalAsync(new SaveBackupsDialog(owner, world.Path, world.Settings.Name));
            _status.Text = HasDraft ? "备份管理已关闭。备份只包含磁盘中已保存的数据；当前编辑仍保留，恢复操作仅生成副本。" : "备份管理已关闭。恢复只生成副本，当前原存档保持不变。可刷新列表查看新增副本。";
        });
    }
    private async Task SaveAsync()
    {
        if (_selected == null) { _status.Text = "请先选择存档。"; return; }
        await RunAsync(async () =>
        {
            var world = _selected; if (world == null || !await EnsureStoppedAsync(world.Path)) return;
            WorldEdit edit; try { edit = Edit(); } catch (Exception ex) { _status.Text = ex.Message; return; }
            if (await MessageBoxDialog.ShowConfirmAsync($"将修改 {world.Path}\\level.dat，自动保留修改前备份。\n{WorldArchiveService.DescribeEdit(edit)}\n请确认游戏已经退出。", "保存存档设置") != true) { _status.Text = "已取消保存，当前编辑仍然保留。"; return; }
            if (!await EnsureStoppedAsync(world.Path)) return;
            var backup = await Task.Run(() => WorldArchiveService.Update(world.Path, edit));
            var updated = await Task.Run(() => WorldArchiveService.Read(world.Path)); Select(updated, reload: true); _status.Text = "设置已保存。备份：" + backup;
        });
    }
    private async Task CopyAsync()
    {
        if (_selected == null) { _status.Text = "请先选择作为模板的存档。"; return; }
        await RunAsync(async () =>
        {
            var world = _selected; if (world == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "输入新存档目录名称（不覆盖已有目录）", FileName = Path.GetFileName(world.Path) + "-copy", InitialDirectory = Path.GetDirectoryName(world.Path), CheckPathExists = true, AddExtension = false };
            if (ShowOwnedDialog(dialog) != true) { _status.Text = "已取消生成副本。"; return; }
            if (!await EnsureStoppedAsync(world.Path)) return;
            WorldEdit edit; try { edit = Edit(); } catch (Exception ex) { _status.Text = ex.Message; return; }
            if (await MessageBoxDialog.ShowConfirmAsync($"从 {world.Path}\n生成到 {dialog.FileName}\n保留已有区块，应用以下设置：\n{WorldArchiveService.DescribeEdit(edit)}\n请退出游戏后继续。", "生成存档副本") != true) { _status.Text = "已取消生成副本，原存档保持不变。"; return; }
            if (!await EnsureStoppedAsync(world.Path)) return;
            await Task.Run(() => WorldArchiveService.GenerateCopy(world.Path, dialog.FileName, edit));
            var copy = await Task.Run(() => WorldArchiveService.Read(dialog.FileName)); Select(copy, reload: true); _status.Text = "已生成：" + dialog.FileName;
        });
    }
    private async Task OpenStructureAsync()
    {
        if (_busy) { _status.Text = "已有存档任务正在处理，请稍候。"; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "投影 / 结构|*.litematic;*.nbt;*.schematic;*.schem", Title = "选择投影或结构文件" };
        if (ShowOwnedDialog(dialog) != true) return;
        await RunAsync(async () =>
        {
            ++_selectionGeneration;
            var data = await Task.Run(() => StructureInspector.Read(dialog.FileName)); _nbt.Text = data.NbtText;
            _projection.Source = RenderProjection(data);
            _materials.Text = data.Materials.Count == 0 ? "此结构没有非空气方块，因此没有可显示的投影或材料。" : "材料清单\n" + string.Join("\n", data.Materials.OrderByDescending(p => p.Value).Take(200).Select(p => $"{p.Key}  × {p.Value:N0}"));
            if (data.Materials.Count > 200) _materials.Text += $"\n另有 {data.Materials.Count - 200:N0} 种材料，可在 NBT 结构中查看。";
            _projectionInfo.Text = $"{Path.GetFileName(dialog.FileName)}\n{data.Format} · {data.Width} × {data.Height} × {data.Depth} · {data.Blocks:N0} 个非空气方块";
            _projectionInfo.ToolTip = dialog.FileName;
            _tabs.SelectedIndex = 1;
            _status.Text = "结构已载入，正在显示投影与材料。完整字段可在 NBT 结构标签查看。";
        });
    }
    private static ImageSource RenderProjection(StructurePreview preview)
    {
        const int limit = 720; double scale = Math.Min(1, limit / (double)Math.Max(preview.Width, preview.Depth));
        int width = Math.Max(1, (int)Math.Ceiling(preview.Width * scale)), height = Math.Max(1, (int)Math.Ceiling(preview.Depth * scale));
        var pixels = new byte[width * height * 4]; var levels = Enumerable.Repeat(int.MinValue, width * height).ToArray();
        foreach (var cell in preview.TopView)
        {
            int x = Math.Min(width - 1, (int)(cell.X * scale)), z = Math.Min(height - 1, (int)(cell.Z * scale)); int index = z * width + x;
            if (cell.Y < levels[index]) continue; levels[index] = cell.Y; var color = StructureInspector.Color(cell.Block);
            pixels[index * 4] = color.B; pixels[index * 4 + 1] = color.G; pixels[index * 4 + 2] = color.R; pixels[index * 4 + 3] = 255;
        }
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze(); return bitmap;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) { _status.Text = "已有存档任务正在处理，请稍候。"; return; }
        _busy = true; _filterPopup.IsOpen = false; UpdateEditorState(); _status.Text = "正在处理…";
        try { await action(); } catch (Exception ex) { _status.Text = "操作未完成：" + ex.Message; }
        finally { _busy = false; UpdateEditorState(); }
    }
}
