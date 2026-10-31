using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>仅卡片自身接收点击，没有遮罩，也不会禁用当前页面。</summary>
internal sealed class AiSideDrawer : Border
{
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ComboBox _entries = new() { MinHeight = 30, Margin = new Thickness(8, 2, 8, 8), DisplayMemberPath = nameof(Entry.Title) };
    private readonly List<Entry> _items = new();
    private readonly Button _settings;
    private readonly Button _newChat;
    private readonly FrameworkElement _fallbackActions;
    private bool _selecting;
    internal sealed record Entry(string Title, FrameworkElement View, Action? Settings = null, Action? NewChat = null);
    internal event Action? CollapseRequested;
    internal event Action? DockRequested;
    internal event Action<double, double>? DragRequested;
    internal Entry? Current { get; private set; }

    internal AiSideDrawer()
    {
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush2");
        ClipToBounds = true;
        PageTransitionBehavior.SetAnimateOnVisible(this, true);
        PageTransitionBehavior.SetAnimateOnVisible(_body, true);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid { Margin = new Thickness(16, 0, 8, 0), Height = 48 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var drag = new Thumb { Cursor = Cursors.SizeAll, Opacity = 0, ToolTip = "拖动标题移动面板" };
        Grid.SetColumnSpan(drag, 2);
        drag.DragDelta += (_, args) => DragRequested?.Invoke(args.HorizontalChange, args.VerticalChange);
        heading.Children.Add(drag);
        heading.Children.Add(AiAssistantPanel.CreateCompactTitle());
        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var action in new[] { ("IconWinMinimize", "最小化", "最小化为AI助手悬浮入口；对话、报告和后台请求都会保留。"),
            ("IconWinClose", "关闭", "关闭当前面板；点击AI助手入口可继续。停止生成请使用聊天中的停止按钮。") })
        {
            var button = AiAssistantPanel.CreateCompactIconButton(action.Item1, action.Item2, action.Item3);
            button.Width = 36;
            button.Margin = new Thickness(0, 0, 2, 0);
            button.Click += (_, _) => CollapseRequested?.Invoke();
            windowButtons.Children.Add(button);
        }
        Grid.SetColumn(windowButtons, 1); heading.Children.Add(windowButtons);
        root.Children.Add(heading);
        _entries.SelectionChanged += (_, _) =>
        {
            if (!_selecting && _entries.SelectedItem is Entry entry) ShowEntry(entry);
        };
        _entries.ToolTip = "切换聊天或分析报告；已有内容和后台请求会保留。";
        _entries.Visibility = Visibility.Collapsed;
        Grid.SetRow(_entries, 1); root.Children.Add(_entries);
        Grid.SetRow(_body, 2); root.Children.Add(_body);
        var footer = new StackPanel { Margin = new Thickness(12, 4, 12, 10) };
        _fallbackActions = footer;
        var buttons = new WrapPanel();
        _newChat = MakeButton("新对话", "创建独立对话；当前对话会保留在对话历史中。");
        _newChat.Click += (_, _) => Current?.NewChat?.Invoke();
        buttons.Children.Add(_newChat);
        _settings = MakeButton("设置", "配置 API 接口、模型、联网搜索、自动保存与助手权限。");
        _settings.Click += (_, _) => Current?.Settings?.Invoke();
        buttons.Children.Add(_settings);
        footer.Children.Add(buttons);
        var menu = new ContextMenu();
        var settingsItem = new MenuItem { Header = "AI 设置…", ToolTip = _settings.ToolTip };
        settingsItem.Click += (_, _) => Current?.Settings?.Invoke();
        var newChatItem = new MenuItem { Header = "新对话", ToolTip = _newChat.ToolTip };
        newChatItem.Click += (_, _) => Current?.NewChat?.Invoke();
        var dock = new MenuItem { Header = "恢复右侧停靠", ToolTip = "把移动过的面板放回启动器右侧；不会清空对话。" };
        dock.Click += (_, _) => DockRequested?.Invoke();
        var close = new MenuItem { Header = "收起面板", ToolTip = "保留对话和后台请求；点击 AI 入口重新打开。" };
        close.Click += (_, _) => CollapseRequested?.Invoke();
        menu.Items.Add(settingsItem); menu.Items.Add(newChatItem); menu.Items.Add(new Separator()); menu.Items.Add(dock); menu.Items.Add(close);
        menu.Opened += (_, _) =>
        {
            settingsItem.IsEnabled = Current?.Settings != null;
            newChatItem.IsEnabled = Current?.NewChat != null;
        };
        heading.ContextMenu = menu;
        footer.ContextMenu = menu;
        heading.ToolTip = "拖动标题移动面板；右键可打开设置、创建对话或恢复停靠。";
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Child = root;
    }

    private static Button MakeButton(string label, string hint)
    {
        var button = new Button { Content = label, ToolTip = hint, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 5, 0), MinHeight = 32 };
        button.SetResourceReference(StyleProperty, "SecondaryButton");
        return button;
    }

    internal void ShowEntry(Entry entry)
    {
        if (!_items.Contains(entry)) { _items.Add(entry); _entries.Items.Add(entry); }
        Current = entry;
        _selecting = true;
        _entries.SelectedItem = entry;
        _selecting = false;
        _body.Content = entry.View;
        _settings.IsEnabled = entry.Settings != null;
        _newChat.IsEnabled = entry.NewChat != null;
        _fallbackActions.Visibility = entry.View is AiAssistantPanel { IsSideDrawerView: true } or AiCrashAnalysisDialog
            ? Visibility.Collapsed : Visibility.Visible;
        _entries.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RemoveEntry(FrameworkElement view)
    {
        var entry = _items.FirstOrDefault(item => ReferenceEquals(item.View, view));
        if (entry == null) return;
        if (ReferenceEquals(Current, entry)) { _body.Content = null; Current = null; }
        _items.Remove(entry);
        _entries.Items.Remove(entry);
        _entries.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (Current == null && _items.Count > 0) ShowEntry(_items[^1]);
    }
}
