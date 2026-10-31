using System.IO;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private async Task<bool> RepairLegacyLoaderBeforeLaunchAsync(GameFolder folder, string versionId,
        CancellationToken cancellationToken, string? architecture)
    {
        bool needsRepair;
        try
        {
            needsRepair = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inheritance = VersionInheritanceResolver.Load(folder.Path, versionId,
                    FolderService.ResolveVersionJson, architecture);
                return inheritance.Layers.Any(layer =>
                    ClientLoaderInstallService.NeedsStandaloneProfileRepair(folder.Path, layer.VersionId));
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // The normal integrity flow handles missing or invalid metadata.
            return true;
        }
        if (!needsRepair) return true;

        try
        {
            await DownloadQueueService.Instance.RunAndWaitAsync($"补全加载器实例 {versionId}", async (item, ct) =>
            {
                var error = await RepairInstanceWithoutLaunchAsync(folder, versionId, item.CreateProgress(), ct, architecture);
                if (error != null) throw new InvalidOperationException(error);
                return true;
            }, cancellationToken, targetVersionId: versionId, targetFolderPath: folder.Path);
            return true;
        }
        catch (OperationCanceledException)
        {
            ToastService.ShowInfo("已取消补全，本次不启动游戏。");
            return false;
        }
        catch (Exception error)
        {
            ErrorPresenter.ShowFriendlyError(
                "旧加载器实例缺少原版依赖，自动补全尚未完成。请检查网络后重新启动，或在版本列表中补全文件。",
                $"[加载器完整依赖补全失败] {error}", "补全失败");
            return false;
        }
    }
}
