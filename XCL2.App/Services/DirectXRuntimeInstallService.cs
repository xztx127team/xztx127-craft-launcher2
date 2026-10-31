using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>从微软下载 DirectX 最终用户运行库并安装，所有阶段在统一下载列表显示。</summary>
public static class DirectXRuntimeInstallService
{
    private const string WebSetupUrl =
        "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe";
    public const string OfficialPage = "https://www.microsoft.com/download/details.aspx?id=35";
    private static int _running;

    public static void StartWithConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            MessageBoxDialog.ShowWarning("该功能仅支持 Windows。", "DirectX 运行库");
            return;
        }
        if (Volatile.Read(ref _running) != 0)
        {
            ToastService.ShowInfo("DirectX 运行库任务已在右上角下载列表中，请等待完成。");
            return;
        }
        if (!MessageBoxDialog.ShowConfirm(
            "将从微软官方下载「DirectX 最终用户运行库」在线安装包并静默安装，补全缺失的 d3dx9 / d3dcompiler / xinput 等组件。\n" +
            "需要网络连接和管理员权限（会弹出系统 UAC 确认）；已经存在的组件不会被覆盖或降级。\n" +
            "这不是显卡驱动，不会更换你的显卡驱动。\n" +
            "进度与结果显示在右上角下载列表。下载阶段可取消，安装开始后需等待安装程序退出。\n\n是否开始？",
            "补全 DirectX 运行库")) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            DownloadQueueService.Instance.StartNew("补全 DirectX 运行库", async (item, ct) =>
            {
                try { await RunInstallAsync(item, ct); }
                finally { Interlocked.Exchange(ref _running, 0); }
            }, supportsPause: false, supportsRetry: false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _running, 0);
            MessageBoxDialog.ShowWarning("无法登记 DirectX 安装任务：" + ex.Message, "DirectX 运行库");
        }
    }

    private static async Task RunInstallAsync(DownloadQueueItem item, CancellationToken ct)
    {
        var setupPath = Path.Combine(Path.GetTempPath(), $"dxwebsetup_{Guid.NewGuid():N}.exe");
        var progress = item.CreateProgress();
        try
        {
            progress.Report(new ProgressInfo("下载 DirectX 运行库安装程序", 0, 0, "dxwebsetup.exe"));
            LauncherLogService.AppendLine("[DirectX] 开始下载 dxwebsetup.exe");
            using (var http = LauncherHttpClientFactory.Create(TimeSpan.FromMinutes(10)))
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 XCL2-Launcher/1.0");
                using var resp = await http.GetAsync(WebSetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(setupPath);
                await src.CopyToAsync(dst, ct);
            }

            ct.ThrowIfCancellationRequested();
            // 安装不可回滚：不强杀安装器，也不在它尚未退出时把任务标成取消。
            item.SupportsCancel = false;
            progress.Report(new ProgressInfo("等待管理员授权并安装 DirectX 运行库，请勿关闭计算机", 0, 0, ""));
            LauncherLogService.AppendLine("[DirectX] 开始静默安装");
            using var process = Process.Start(new ProcessStartInfo(setupPath)
            {
                Arguments = "/Q",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("无法启动 DirectX 安装程序。");
            await process.WaitForExitAsync();
            LauncherLogService.AppendLine($"[DirectX] 安装程序退出，退出码 {process.ExitCode}");
            item.CompletionText = process.ExitCode switch
            {
                0 => "DirectX 运行库已补全，可重新启动游戏",
                3010 => "DirectX 运行库已安装，需要重启计算机",
                _ => throw new InvalidOperationException($"安装失败（退出码 {process.ExitCode}）。可前往微软官方页面手动安装：{OfficialPage}")
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new DownloadUserCanceledException("未授予管理员权限，DirectX 安装已取消。", ex);
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[DirectX] 安装未完成：" + ex);
            throw;
        }
        finally
        {
            try { File.Delete(setupPath); } catch { }
        }
    }
}
