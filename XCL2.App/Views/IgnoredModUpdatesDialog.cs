using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class IgnoredModUpdatesDialog : OverlayDialogControl
{
    public IgnoredModUpdatesDialog(ConfigService config, string gameDirectory)
    {
        Width = 520;
        MaxWidth = Math.Max(300, (Application.Current.MainWindow?.ActualWidth ?? 854) - 100);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "当前实例忽略的更新版本", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "只忽略指定版本，后续新版本仍会显示。恢复后需手动重新检查更新。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var rows = new StackPanel();
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Clamp((Application.Current.MainWindow?.ActualHeight ?? 480) - 220, 90, 360) };
        panel.Children.Add(scroll);
        void Refresh()
        {
            rows.Children.Clear(); var items = ModUpdatePreferenceService.Ignored(config.Config, gameDirectory);
            if (items.Count == 0) rows.Children.Add(new TextBlock { Text = "当前没有忽略的更新版本。" });
            foreach (var item in items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var undo = new Button { Content = "恢复提醒", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(undo, Dock.Right); row.Children.Add(undo);
                row.Children.Add(new TextBlock { Text = item.DisplayName + " · " + item.VersionName, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.DisplayName + " · " + item.VersionName, VerticalAlignment = VerticalAlignment.Center });
                undo.Click += (_, _) =>
                {
                    var index = config.Config.Ux50IgnoredModUpdateVersions.IndexOf(item);
                    config.Config.Ux50IgnoredModUpdateVersions.Remove(item);
                    try { config.Save(); Refresh(); }
                    catch (Exception ex)
                    {
                        config.Config.Ux50IgnoredModUpdateVersions.Insert(Math.Clamp(index, 0, config.Config.Ux50IgnoredModUpdateVersions.Count), item);
                        ErrorPresenter.ShowFriendlyError("未能保存恢复提醒的设置。", ex.Message, "恢复更新提醒");
                    }
                };
                rows.Children.Add(row);
            }
        }
        Refresh();
        var close = new Button { Content = "关闭", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        close.Click += (_, _) => CloseWith(false); panel.Children.Add(close); Content = panel;
    }
}
