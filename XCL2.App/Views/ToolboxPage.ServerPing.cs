using System.Windows;
using System.Windows.Media;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ToolboxPage
{
    private CancellationTokenSource? _serverPingCancellation;

    private CancellationTokenSource BeginServerProbe()
    {
        var cancellation = new CancellationTokenSource();
        _serverPingCancellation = cancellation;
        PingGoBtn.IsEnabled = PingLinkBtn.IsEnabled = false;
        PingCancelBtn.IsEnabled = true;
        PingResultText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        return cancellation;
    }
    private void EndServerProbe(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_serverPingCancellation, cancellation))
        {
            _serverPingCancellation = null;
            PingGoBtn.IsEnabled = PingLinkBtn.IsEnabled = true;
            PingCancelBtn.IsEnabled = false;
        }
        cancellation.Dispose();
    }
    private void PingCancel_Click(object sender, RoutedEventArgs e) => _serverPingCancellation?.Cancel();

    private async void PingGo_Click(object sender, RoutedEventArgs e)
    {
        if (_serverPingCancellation != null) return;
        if (!ServerPingAddress.TryParse(PingHostBox.Text, PingPortBox.Text, out var endpoint, out var error))
        { PingResultText.Text = error; return; }
        var cancellation = BeginServerProbe();
        PingResultText.Text = "正在连接 " + endpoint!.DisplayAddress + "…";
        PingLinkResultText.Text = "连接后识别本次连接使用的本机网卡…";
        try
        {
            var result = await ServerPingService.PingAsync(endpoint.Host, endpoint.Port, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            PingResultText.Foreground = result.Success ? (Brush)FindResource("TextPrimaryBrush") : Brushes.IndianRed;
            var connection = result.ConnectionLatencyMs.HasValue ? $"连接耗时（含地址解析）：{result.ConnectionLatencyMs} ms\n" : "";
            PingResultText.Text = $"目标：{result.ServerAddress ?? endpoint.DisplayAddress}\n" + connection +
                (result.Success ? $"服务器响应延迟：{result.LatencyMs} ms\n版本：{result.VersionName ?? "未知"}\n" +
                    $"在线人数：{result.OnlinePlayers?.ToString() ?? "?"} / {result.MaxPlayers?.ToString() ?? "?"}\n" +
                    "MOTD：" + (string.IsNullOrWhiteSpace(result.MotdPlain) ? "(空)" : result.MotdPlain)
                    : result.ErrorMessage);
            PingLinkResultText.Text = result.LocalLink is { } link
                ? $"本次连接的本机链路：{link.Name}（{link.InterfaceType}）\n系统报告速率：{ServerLinkRateService.FormatRate(link.BitsPerSecond)}"
                : "未能确定本次连接的本机链路速率；可点击“检测本机链路速率”查看当前网卡。";
        }
        catch (OperationCanceledException) { PingResultText.Text = "测速已取消。"; PingLinkResultText.Text = "本次链路检测已取消。"; }
        catch (Exception ex) { PingResultText.Text = "测速失败：" + ex.Message; PingLinkResultText.Text = "本次链路速率暂不可用。"; }
        finally { EndServerProbe(cancellation); }
    }

    private async void PingLink_Click(object sender, RoutedEventArgs e)
    {
        if (_serverPingCancellation != null) return;
        var cancellation = BeginServerProbe();
        PingLinkResultText.Text = "正在读取本机网卡链路速率…";
        try
        {
            var links = await ServerLinkRateService.DetectAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            PingLinkResultText.Text = links.Count == 0 ? "未发现已连接且有 IP 地址的网卡。" :
                "本机已连接网卡（不代表服务器路由选择）：\n" + string.Join("\n", links.Select(link =>
                    $"{link.Name}（{link.InterfaceType}）：{ServerLinkRateService.FormatRate(link.BitsPerSecond)}"));
        }
        catch (OperationCanceledException) { PingLinkResultText.Text = "链路检测已取消。"; }
        catch (Exception ex) { PingLinkResultText.Text = "无法读取本机链路速率：" + ex.Message; }
        finally { EndServerProbe(cancellation); }
    }
}
