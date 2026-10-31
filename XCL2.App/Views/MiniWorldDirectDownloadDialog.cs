using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal sealed class MiniWorldDirectDownloadDialog : OverlayDialogControl
{
    public MiniWorldVersion? DownloadVersion { get; private set; }

    public MiniWorldDirectDownloadDialog(MiniWorldVersion version)
    {
        Width = 590; MaxHeight = 690;
        var closed = new CancellationTokenSource();
        var isClosed = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "CDK / 直链下载", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = version.Name + " · " + version.Version, Margin = new Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap });
        var mode = new ComboBox { ItemsSource = new[] { "文件直链", "CDK 兑换" }, SelectedIndex = version.CdkRedeemUrl.Length > 0 ? 1 : 0 };
        panel.Children.Add(mode);
        TextBox Field(Panel parent, string label, string value)
        {
            parent.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 5) });
            var field = new TextBox { Text = value, MaxLength = 4096, Padding = new Thickness(8) }; parent.Children.Add(field); return field;
        }
        var direct = new StackPanel(); panel.Children.Add(direct);
        var url = Field(direct, "发布者提供的 HTTPS 文件直链", version.FileName.Length > 0 ? version.DownloadUrl : "");
        var redeem = new StackPanel(); panel.Children.Add(redeem);
        var endpoint = Field(redeem, "发布者提供的 CDK 兑换接口（HTTPS）", version.CdkRedeemUrl);
        var destination = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) };
        redeem.Children.Add(destination);
        void UpdateDestination() => destination.Text = MiniWorldVersionService.IsWebAddress(endpoint.Text.Trim())
            ? "CDK 仅发送到：" + new Uri(endpoint.Text.Trim()).Host + "。请确认这是提供该 CDK 的发布者。" : "请填写发布者明确支持的兑换接口，普通网页不能用于兑换。";
        endpoint.TextChanged += (_, _) => UpdateDestination(); UpdateDestination();
        redeem.Children.Add(new TextBlock { Text = "CDK（本次使用，不保存）", Margin = new Thickness(0, 8, 0, 5) });
        var code = new PasswordBox { MaxLength = 512, Padding = new Thickness(8) }; redeem.Children.Add(code);
        var file = Field(panel, "保存文件名（exe / zip / 7z；CDK 接口返回文件名时优先使用返回值）", version.FileName); file.MaxLength = 240;
        panel.Children.Add(new TextBlock { Text = "文件直接加入启动器下载队列，不自动打开浏览器、不自动安装。CDK 需由发布者提供并支持本启动器的 JSON 兑换协议；不支持的来源可粘贴已获取的文件直链。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush"); panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => CloseWith(false); actions.Children.Add(cancel);
        var download = new Button { Content = "加入下载队列", Padding = new Thickness(16, 8, 16, 8) };
        download.SetResourceReference(Button.StyleProperty, "PrimaryButton"); actions.Children.Add(download);
        void UpdateMode()
        {
            direct.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            redeem.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        mode.SelectionChanged += (_, _) => UpdateMode(); UpdateMode();
        download.Click += async (_, _) =>
        {
            if (!download.IsEnabled) return;
            download.IsEnabled = false; mode.IsEnabled = false;
            endpoint.IsEnabled = false; code.IsEnabled = false; url.IsEnabled = false; file.IsEnabled = false;
            try
            {
                MiniWorldCdkDownload result;
                if (mode.SelectedIndex == 1)
                {
                    status.Text = "正在兑换，请勿重复提交…";
                    result = await MiniWorldCdkService.Shared.RedeemAsync(endpoint.Text.Trim(), code.Password, version.Id, file.Text, closed.Token);
                }
                else
                {
                    var link = url.Text.Trim(); var name = file.Text.Trim();
                    if (!MiniWorldVersionService.IsWebAddress(link)) throw new InvalidDataException("请填写有效的 HTTPS 文件直链。");
                    if (name.Length == 0) name = Path.GetFileName(new Uri(link).LocalPath);
                    if (!MiniWorldVersionService.IsSupportedFileName(name)) throw new InvalidDataException("请填写有效的 exe / zip / 7z 保存文件名。");
                    result = new(link, name, version.Sha256, Array.Empty<string>());
                }
                if (isClosed) return;
                // Keep the original version identity so progress, installer and launch binding stay together.
                DownloadVersion = version with { DownloadUrl = result.Url, FileName = result.FileName, Sha256 = result.Sha256,
                    Mirrors = result.Mirrors, PublicShares = Array.Empty<MiniWorldShare>(), SkipDownloadProbe = true };
                code.Clear(); CloseWith(true);
            }
            catch (OperationCanceledException) { if (!isClosed) status.Text = "兑换已取消或超时。请先确认兑换码状态，再决定是否重试。"; }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or IOException)
            { if (!isClosed) status.Text = ex is InvalidDataException ? ex.Message : "无法取得有效兑换结果，请检查网络、接口协议与兑换码状态。"; }
            finally
            {
                code.Clear();
                if (!isClosed) { download.IsEnabled = true; mode.IsEnabled = true; endpoint.IsEnabled = true; code.IsEnabled = true; url.IsEnabled = true; file.IsEnabled = true; }
            }
        };
        OverlayClosed += (_, _) => { isClosed = true; closed.Cancel(); closed.Dispose(); code.Clear(); };
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.VerticalOnly };
    }
}
