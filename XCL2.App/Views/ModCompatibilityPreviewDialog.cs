using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class ModCompatibilityPreviewDialog : OverlayDialogControl
{
    public bool OpenUpdateCheckRequested { get; private set; }
    public ModCompatibilityPreviewDialog(MainWindow owner, ModCompatibilityReport report)
    {
        Width = 620; MaxWidth = Math.Max(360, owner.ActualWidth - 80);
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(new TextBlock { Text = "模组兼容性预览", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = $"Minecraft {report.MinecraftVersion} · {report.Loader}\n已启用 {report.EnabledCount} 个文件，识别到 {report.RecognizedCount} 个文件的元数据。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        var rows = new StackPanel();
        if (report.Issues.Count == 0) rows.Children.Add(new TextBlock { Text = "本地元数据中没有发现已支持检查范围内的问题。", TextWrapping = TextWrapping.Wrap });
        foreach (var issue in report.Issues.Take(200))
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(new TextBlock { Text = issue.ModName + " · " + issue.Kind, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = issue.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) }); rows.Children.Add(row);
        }
        if (report.Issues.Count > 200) rows.Children.Add(new TextBlock { Text = $"另有 {report.Issues.Count - 200} 项，建议分批整理后重新检查。", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = Math.Clamp(owner.ActualHeight - 390, 100, 280), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = "只读取本地文件，不安装、启用或修改模组。支持 Fabric / Quilt / Forge / NeoForge 的常见声明；复杂版本范围、依赖版本匹配、运行期冲突仍需加载器验证。", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) });
        var declarationText = new TextBox { Text = report.Declarations.Count == 0 ? "没有读取到依赖声明。" : string.Join("\n", report.Declarations.Take(300)), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new Expander { Header = "查看原始依赖范围", Content = declarationText });
        var online = new CheckBox { Content = "允许在此页面手动联网检查模组更新", IsChecked = owner.ConfigService.Config.AllowManualModUpdateNetworkChecks, Margin = new Thickness(0, 12, 0, 0) };
        var networkHint = new TextBlock { Text = "只在点击“检查更新”后访问 Modrinth；不改变已有模组自动更新提醒设置。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(new Expander { Header = "联网检查设置", Margin = new Thickness(0, 12, 0, 0), Content = new StackPanel { Children = { online, networkHint } } });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var update = new Button { Content = "检查更新", MinWidth = 96, IsEnabled = online.IsChecked == true, Margin = new Thickness(0, 0, 8, 0) };
        update.SetResourceReference(StyleProperty, "SecondaryButton");
        online.Click += (_, _) =>
        {
            var old = owner.ConfigService.Config.AllowManualModUpdateNetworkChecks;
            try { owner.ConfigService.Config.AllowManualModUpdateNetworkChecks = online.IsChecked == true; owner.ConfigService.Save(); update.IsEnabled = online.IsChecked == true; }
            catch (Exception ex) { owner.ConfigService.Config.AllowManualModUpdateNetworkChecks = old; online.IsChecked = old; ToastService.ShowWarning("联网设置未保存：" + ex.Message); }
        };
        update.Click += (_, _) => { OpenUpdateCheckRequested = true; Close(); };
        var close = new Button { Content = "关闭", MinWidth = 72 }; close.SetResourceReference(StyleProperty, "SecondaryButton"); close.Click += (_, _) => Close();
        actions.Children.Add(update); actions.Children.Add(close); body.Children.Add(actions);
        Content = new ScrollViewer { Content = body, MaxHeight = Math.Max(250, owner.ActualHeight - 100), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
