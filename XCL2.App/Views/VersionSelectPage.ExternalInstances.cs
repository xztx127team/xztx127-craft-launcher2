using System.IO;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class VersionSelectPage
{
    private bool _importingExternalInstances;
    private async Task ImportExternalInstancesAsync()
    {
        if (_importingExternalInstances) { ToastService.ShowInfo("已有外部实例导入正在进行。"); return; }
        _importingExternalInstances = true;
        try
        {
            var pick = new CompatibleFolderDialog { Title = "选择单个 instance、instances/profiles 文件夹或启动器数据目录" };
            if (pick.ShowDialog() != true) return;
            ExternalInstanceScan scan;
            var scanning = new ProgressDialog("正在识别第三方实例…", indeterminate: true); scanning.Show();
            try { scan = await Task.Run(() => ExternalInstanceDiscovery.Scan(pick.FolderName)); }
            finally { scanning.Close(); }
            var choices = new ExternalInstancePickerDialog(scan);
            if (await OverlayDialogService.ShowModalAsync(choices) != true) return;
            var folder = FolderListBox.SelectedItem as GameFolder;
            if (folder == null)
            {
                var target = new CompatibleFolderDialog { Title = "选择保存副本的目标 .minecraft 目录（不要选择源实例）" };
                if (target.ShowDialog() != true) return;
                folder = _folderService.AddFolder(_owner.ConfigService.Config, target.FolderName);
                if (!_folders.Contains(folder)) _folders.Add(folder);
                FolderListBox.SelectedItem = folder; _owner.ConfigService.Save();
            }
            var targetRoot = folder.Path;
            if (!MessageBoxDialog.ShowConfirm($"将 {choices.Selected.Count} 个实例导入为独立副本。\n目标：{targetRoot}\\versions\n\n需要联网获取游戏、资源和相同版本的加载器；原目录不会更改。请确认源游戏已经退出。", "确认实例副本目标")) return;
            var config = _owner.ConfigService.Config;
            string? java = null;
            if (choices.Selected.Any(c => c.Loader is "Forge" or "NeoForge"))
            {
                try { java = await Task.Run(() => new JavaService().FindJava(config.JavaPath, configService: _owner.ConfigService)); }
                catch { }
            }
            var imported = new List<string>(); var failures = new List<string>();
            foreach (var candidate in choices.Selected)
            {
                Exception? deferredCleanup = null;
                string? publishedId = null;
                try
                {
                    var id = await DownloadQueueService.Instance.RunAndWaitAsync("导入实例副本 " + candidate.Name,
                        async (item, ct) =>
                        {
                            try { return publishedId = await new ExternalInstanceImportService(config).ImportAsync(candidate, targetRoot, candidate.Name, java,
                                item.CreateProgress(), ct, targetId => DownloadQueueService.Instance.RegisterInstallationTarget(item, targetId)); }
                            catch (Exception ex) when (InstallerProcessLifetime.RequiresPreservedDirectory(ex))
                            { deferredCleanup = ex; throw; }
                        },
                        targetFolderPath: targetRoot);
                    imported.Add(id); _owner.NotifyInstanceImported(targetRoot, id);
                }
                catch (OperationCanceledException ex)
                {
                    if (publishedId != null)
                    {
                        imported.Add(publishedId); _owner.NotifyInstanceImported(targetRoot, publishedId);
                        failures.Add(candidate.Name + "：取消请求在副本发布后到达，已保留完成的副本；未继续后续实例。");
                        break;
                    }
                    failures.Add(candidate.Name + "：" + (deferredCleanup?.Message ?? (InstallerProcessLifetime.RequiresPreservedDirectory(ex)
                        ? ex.Message : "用户取消，未继续后续实例。")));
                    break;
                }
                catch (Exception ex) { failures.Add(candidate.Name + "：" + ex.Message); }
            }
            await RefreshInstalledVersionsAsync();
            if (failures.Count > 0) MessageBoxDialog.ShowWarning($"成功导入 {imported.Count} 个实例副本。\n\n" + string.Join("\n", failures), "实例导入结果");
            else ToastService.ShowSuccess($"已导入 {imported.Count} 个实例副本，原目录保持不变。");
        }
        catch (Exception ex) { ErrorPresenter.ShowFriendlyError("第三方实例导入失败：" + ex.Message, ex.ToString(), "导入失败"); }
        finally { _importingExternalInstances = false; }
    }
}
