using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal sealed class MiniWorldLocalClientDialog : OverlayDialogControl
{
    public MiniWorldLocalInstallation? Installation { get; private set; }
    public MiniWorldLocalClientDialog(MiniWorldLocalInstallation seed)
    {
        Width = 650; MaxHeight = 740;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "本地客户端 · 启动设置", FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "选择安装/解压后的游戏程序。不会把安装包、卸载器或更新器作为游戏入口。CBL 归档会读取同目录渠道信息，补齐所需启动参数。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        TextBox Field(string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label }); var text = new TextBox { Text = value, Padding = new Thickness(8), Margin = new Thickness(0, 5, 0, 10), MaxLength = 2048 }; panel.Children.Add(text); return text;
        }
        var name = Field("显示名称", seed.Name); name.MaxLength = 160;
        var source = Field("来源 / 社区", seed.SourceName); source.MaxLength = 160;
        var version = Field("本地版本名称", seed.VersionName); version.MaxLength = 160;
        var executable = Field("游戏 EXE", seed.ExecutablePath);
        var directory = Field("工作目录（通常是 EXE 所在文件夹）", seed.WorkingDirectory);
        var arguments = Field("可选启动参数（每行一个参数；含空格的参数不需要额外加引号）", string.Join(Environment.NewLine, seed.Arguments ?? new()));
        arguments.MaxLength = 28000; arguments.AcceptsReturn = true; arguments.MinHeight = 70; arguments.MaxHeight = 130; arguments.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) }; error.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush");
        var choices = new WrapPanel();
        var browse = new Button { Content = "选择游戏 EXE…", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 8) };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "选择已安装的迷你世界游戏入口", Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog() != true) return;
            try
            {
                executable.Text = MiniWorldLaunchService.ValidateGameExecutable(picker.FileName); directory.Text = Path.GetDirectoryName(executable.Text)!;
                arguments.Text = string.Join(Environment.NewLine, MiniWorldLaunchService.SuggestedArguments(executable.Text)); error.Text = "";
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        choices.Children.Add(browse);
        var work = new Button { Content = "选择工作目录…", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 8) };
        work.Click += (_, _) => { var picker = new XCL2.App.Services.CompatibleFolderDialog { Title = "选择游戏工作目录" }; if (picker.ShowDialog() == true) directory.Text = picker.FolderName; }; choices.Children.Add(work);
        panel.Children.Add(choices); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => CloseWith(false); actions.Children.Add(cancel);
        var save = new Button { Content = "保存客户端", Padding = new Thickness(16, 8, 16, 8) }; save.SetResourceReference(Button.StyleProperty, "PrimaryButton");
        save.Click += (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "请输入客户端名称。"; return; }
                var result = new MiniWorldLocalInstallation
                {
                    Id = seed.Id, BindingKey = seed.BindingKey, SourceId = seed.SourceId, SourceName = source.Text.Trim(),
                    VersionId = seed.VersionId, VersionName = version.Text.Trim(), Name = name.Text.Trim(), ExecutablePath = executable.Text.Trim(), WorkingDirectory = directory.Text.Trim(),
                    Arguments = arguments.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(value => value.Length > 0).ToList(), LastLaunchedAt = seed.LastLaunchedAt
                };
                var start = MiniWorldLaunchService.CreateStartInfo(result);
                result.ExecutablePath = start.FileName; result.WorkingDirectory = start.WorkingDirectory; result.Arguments = start.ArgumentList.ToList();
                if (string.IsNullOrWhiteSpace(result.BindingKey)) result.BindingKey = MiniWorldCatalog.ClientKey(result.SourceId, "local:" + result.ExecutablePath.ToUpperInvariant());
                Installation = result; CloseWith(true);
            }
            catch (Exception ex) { error.Text = "无法保存启动设置：" + ex.Message; }
        };
        actions.Children.Add(save); panel.Children.Add(actions); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

internal sealed class MiniWorldDetectedClientsDialog : OverlayDialogControl
{
    public MiniWorldLocalCandidate? Selected { get; private set; }
    public MiniWorldDetectedClientsDialog(IReadOnlyList<MiniWorldLocalCandidate> candidates)
    {
        Width = 700; MaxHeight = 680; var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "选择要导入的本地客户端", FontSize = 21, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "只识别本地文件；选择后还可核对名称、版本、工作目录和参数，不会自动启动。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var candidate in candidates)
        {
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12) };
            button.Content = new TextBlock { Text = candidate.SourceName + " · " + candidate.Reason + "\n" + candidate.ExecutablePath, TextWrapping = TextWrapping.Wrap };
            button.Click += (_, _) => { Selected = candidate; CloseWith(true); }; panel.Children.Add(button);
        }
        var close = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 8, 16, 8) }; close.Click += (_, _) => CloseWith(false); panel.Children.Add(close);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
