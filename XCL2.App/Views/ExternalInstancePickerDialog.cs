using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>Review source metadata before choosing which independent copies to create.</summary>
public sealed class ExternalInstancePickerDialog : OverlayDialogControl
{
    public IReadOnlyList<ExternalInstanceCandidate> Selected { get; private set; } = Array.Empty<ExternalInstanceCandidate>();
    public ExternalInstancePickerDialog(ExternalInstanceScan scan)
    {
        var owner = Application.Current?.MainWindow;
        Width = Math.Min(720, Math.Max(340, (owner?.ActualWidth ?? 920) - 100));
        MaxHeight = Math.Max(280, (owner?.ActualHeight ?? 650) - 80);
        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "选择要导入的实例副本", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "支持 Prism / MultiMC、Modrinth App、CurseForge App。\n请先关闭源游戏和源启动器。复制模组、存档、配置与资源包；联网重建相同版本的游戏和加载器。源目录保持原样，副本不随原启动器同步。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        grid.Children.Add(header);
        var list = new StackPanel();
        var choices = new List<(CheckBox Check, ExternalInstanceCandidate Candidate)>();
        var first = true;
        foreach (var candidate in scan.Instances)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = candidate.Summary, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = candidate.GameDirectory, FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            if (!candidate.CanImport) panel.Children.Add(new TextBlock { Text = candidate.Problem ?? "版本信息不完整", TextWrapping = TextWrapping.Wrap, Opacity = .8 });
            var check = new CheckBox { Content = panel, IsEnabled = candidate.CanImport, IsChecked = first && candidate.CanImport, Margin = new Thickness(0, 7, 0, 7), VerticalContentAlignment = VerticalAlignment.Top };
            if (candidate.CanImport) first = false;
            list.Children.Add(check); choices.Add((check, candidate));
        }
        foreach (var warning in scan.Warnings.Take(20)) list.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 7, 14, 7) }; cancel.Click += (_, _) => CloseWith(false);
        var confirm = new Button { Content = "导入所选副本", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0), IsEnabled = scan.Instances.Any(c => c.CanImport) };
        confirm.Click += (_, _) => { Selected = choices.Where(c => c.Check.IsChecked == true && c.Candidate.CanImport).Select(c => c.Candidate).ToArray(); if (Selected.Count > 0) CloseWith(true); };
        actions.Children.Add(cancel); actions.Children.Add(confirm); Grid.SetRow(actions, 2); grid.Children.Add(actions);
        Content = grid;
    }
}
