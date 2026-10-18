using System.Windows;
using System.Windows.Controls;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>Embedded contextual help. Opening help never accesses the network or reads user files.</summary>
public static class OfflineHelpService
{
    private static readonly IReadOnlyDictionary<string, (string Title, string Text)> Topics = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["logs"] = ("日志阅读帮助", "日志只用于查看与排错。向上滚动查看历史时，新日志不会把页面拉到底部；滚回底部即可恢复跟随。\n\n在“更多 → 阅读字号与视图筛选”调整字号、关键词和级别。筛选只影响显示，不会删除原日志；未知级别的开头信息仍保留。清除关键词并选择“全部”即可恢复完整视图。\n\n选中文字后可复制脱敏后的内容。可选择附带原始行号；未选择文字时，上方复制按钮继续复制原日志。\n\nAI 分析会使用原日志而非筛选后的视图，且受已有日志读取设置限制。诊断导出只保存到本地，分享前请检查预览。"),
        ["downloads"] = ("下载管理帮助", "等待中的任务可在下载管理中查看、暂停或取消。任务阶段会区分连接、传输、校验与安装；部分安装阶段无法安全中断。\n\n暂停不会删除已下载的可复用内容；删除缓存需主动选择。重启后支持恢复的任务保持暂停，只有点击继续才会执行。\n\n限速和游戏运行时降速均在下载设置中调整。"),
        ["instances"] = ("实例管理帮助", "实例页面中的更多菜单提供备注、标签、环境摘要和目录入口。标签仅用于启动器列表，不改游戏文件。\n\n导入或重命名前请核对目标目录。共享运行目录的实例可能使用相同的模组、存档或设置；隔离方式不会因查看页面自动改变。\n\n备份和恢复由用户主动执行，恢复前可检查备份内容和目标。"),
        ["settings"] = ("设置帮助", "使用设置搜索定位相关选项。低频功能在相应分组中折叠显示。\n\n设置修改摘要只包含值的变化；滚动、切换页面和查看提示不会被计为修改。恢复默认值只修改选定项目或分组。\n\n部分便捷功能默认关闭，例如历史记录、额外采样与按住确认；可在对应设置中开启，并可随时关闭。"),
        ["updates"] = ("更新提醒帮助", "自动发现的新版本只进入右下角提醒角标。点击后才进入更新确认，不会自动下载。\n\n可以对当前版本选择“稍后提醒”，也可以从该提醒中取消暂缓；设置页主动检查更新始终可用。暂缓只影响指定版本的被动提醒，不改变模组更新设置。")
    };

    public static void Show(string topic)
    {
        if (!Topics.TryGetValue(topic, out var help)) help = ("离线帮助", "请在对应功能页面打开帮助，查看与当前操作相关的说明。");
        var dialog = new OverlayDialogControl { MinWidth = 300, MaxWidth = 620 };
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = help.Title, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        var scroll = new ScrollViewer { MaxHeight = Math.Max(180, Math.Min(440, (Application.Current.MainWindow?.ActualHeight ?? 700) - 200)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = help.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) } };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var close = new Button { Content = "关闭", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => dialog.Close(); Grid.SetRow(close, 2); root.Children.Add(close);
        dialog.Content = root;
        OverlayDialogService.ShowNonModal(dialog);
    }
}
