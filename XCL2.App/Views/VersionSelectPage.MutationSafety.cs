using System.IO;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class VersionSelectPage
{
    private void EnsureInstanceMutationAllowed(string folderPath, string versionId)
    {
        // Recheck after modal confirmation: a queued installation may have started while
        // the dialog was open. Paused/waiting/cleanup tasks still own their destination.
        var queue = DownloadQueueService.Instance;
        var active = queue.FindActiveForVersion(folderPath, versionId) ?? queue.Items.FirstOrDefault(item =>
            (item.IsRunning || item.IsCleaningUp || item.Status is DownloadQueueItemStatus.Paused or DownloadQueueItemStatus.Waiting) &&
            string.IsNullOrWhiteSpace(item.TargetVersionId) && !string.IsNullOrWhiteSpace(item.TargetFolderPath) &&
            string.Equals(NormalizeMutationFolder(item.TargetFolderPath), NormalizeMutationFolder(folderPath), StringComparison.OrdinalIgnoreCase));
        if (active != null)
            throw new InvalidOperationException("该实例或所在文件夹仍有下载、导入、补全或清理任务。请先完成或取消任务，再重命名或删除实例。");
        if (_owner.PluginLaunchInProgress || _owner.ProcessManager.Running.Any(game =>
            string.Equals(game.VersionId, versionId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("游戏正在启动或该版本仍在运行。请先退出游戏，再重命名或删除实例。");
    }

    private static string NormalizeMutationFolder(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
