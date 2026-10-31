using System.IO;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private async Task InitializeDownloadRecoveryAsync()
    {
        try
        {
            if (ConfigService.Config.GuestModeEnabled) return;
            var queue = DownloadQueueService.Instance;
            var store = await Task.Run(() => new DownloadRecoveryStore(Path.Combine(App.DataDir, "download_recovery")));
            if (_isWindowClosed) { store.Dispose(); return; }
            queue.InitializeRecovery(store,
                request => request.IsAllowed(ConfigService.Config.Folders.Select(folder => folder.Path), requireDirectory: false),
                async (request, item, ct) =>
                {
                    if (!Directory.Exists(request.Folder)) throw new DirectoryNotFoundException("实例目录暂不可用，请连接对应磁盘或恢复目录后再继续。");
                    using var downloader = DownloadService.CreateFromConfig(ConfigService.Config);
                    await downloader.InstallVersionAsync(request.Folder, new VersionManifestEntry
                    { Id = request.VersionId, Url = request.VersionUrl, Type = request.VersionType }, item.CreateProgress(), ct);
                    await Dispatcher.InvokeAsync(RefreshSidebar);
                });
            ConfigService.Saved += queue.PersistRecovery;
            Closed += (_, _) => { ConfigService.Saved -= queue.PersistRecovery; queue.CloseRecovery(); };
            queue.PersistRecovery();
            var restored = await queue.RestoreInterruptedAsync();
            if (restored > 0 && !_isWindowClosed)
                ReminderCenterService.Publish("unfinished-downloads", "有未完成的下载",
                    $"已将 {restored} 个原版安装任务放回下载管理，保持暂停。点击继续后才会联网下载。",
                    "打开下载管理", () => { DownloadQueueButton_Click(DownloadQueueButton, new RoutedEventArgs()); return Task.CompletedTask; });
        }
        catch (Exception ex) { LauncherLogService.AppendLine("[下载恢复] 初始化失败：" + ex.GetType().Name); }
    }
}
