using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal sealed partial class MiniWorldLauncherDialog
{
    private readonly HashSet<string> _launching = new();
    private bool _scanningLocal;

    private void InitializeLocalInstallations()
    {
        var config = _owner.ConfigService.Config; config.MiniWorldLocalInstallations ??= new();
        var changed = config.MiniWorldLocalInstallations.RemoveAll(item => item == null) > 0;
        void Preserve(string key, string path, string sourceId, string sourceName)
        {
            if (string.IsNullOrWhiteSpace(path) || MiniWorldLaunchService.IsInstallerOrUtility(path) || config.MiniWorldLocalInstallations.Any(item => item.BindingKey == key)) return;
            try
            {
                config.MiniWorldLocalInstallations.Add(new MiniWorldLocalInstallation
                {
                    BindingKey = key, SourceId = sourceId, SourceName = sourceName, Name = sourceName + " · 已有本地绑定",
                    VersionName = "已有本地安装（版本待确认）", ExecutablePath = Path.GetFullPath(path), WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""
                }); changed = true;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        foreach (var pair in config.MiniWorldClientPaths)
        {
            var source = MiniWorldCatalog.Sources.FirstOrDefault(item => item.Id == pair.Key);
            Preserve(pair.Key, pair.Value, source?.Id ?? "legacy", source?.Name ?? "历史版本绑定");
        }
        foreach (var community in config.MiniWorldCommunityClients.Where(item => item != null && !string.IsNullOrWhiteSpace(item.ExecutablePath)))
            Preserve(MiniWorldCatalog.ClientKey("custom:" + community.Id, "local:" + community.ExecutablePath.ToUpperInvariant()), community.ExecutablePath, "custom:" + community.Id, community.Name);
        foreach (var installation in config.MiniWorldLocalInstallations)
        {
            if (installation.Arguments?.Count > 0 || !File.Exists(installation.ExecutablePath)) continue;
            try
            {
                var suggested = MiniWorldLaunchService.SuggestedArguments(installation.ExecutablePath);
                if (suggested.Count > 0) { installation.Arguments = suggested.ToList(); changed = true; }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        if (changed) _owner.ConfigService.Save();
    }

    private MiniWorldLocalInstallation? FindLocalInstallation(MiniWorldSource source, MiniWorldVersion? version)
    {
        var key = version?.ClientKey ?? source.Id;
        var installation = _owner.ConfigService.Config.MiniWorldLocalInstallations.FirstOrDefault(item => item.BindingKey == key);
        if (installation == null && version == null)
            installation = _owner.ConfigService.Config.MiniWorldLocalInstallations.FirstOrDefault(item => item.SourceId == source.Id && (string.IsNullOrEmpty(item.VersionId) || item.VersionId.StartsWith("local:", StringComparison.Ordinal)));
        if (installation != null && version != null)
        {
            // 旧版只有哈希路径，加载目录后补充可读元数据；从来不把来源级绑定移给首个在线版本。
            installation.SourceId = source.Id; installation.SourceName = source.Name;
            installation.VersionId = version.Id; installation.VersionName = version.Version;
        }
        return installation;
    }

    private void SelectLocalClients()
    {
        _sources.SelectedItem = _sources.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is MiniWorldSource { Id: "local-installed" });
        RefreshDetail();
    }

    private void RenderLocalClients()
    {
        _detail.Children.Add(new TextBlock { Text = "本地客户端", FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        _detail.Children.Add(Text("已安装与已解压的客户端保存在这里。在线版本目录加载失败也可以使用。", 12));
        var actions = new WrapPanel(); actions.Children.Add(Button("＋ 导入本地安装", () => ImportLocalClient(), true));
        actions.Children.Add(Button("检测常见安装位置", () => _ = DetectCommonClientsAsync()));
        actions.Children.Add(Button("扫描安装/解压文件夹…", () => _ = ScanLocalDirectoryAsync())); _detail.Children.Add(actions);
        var installations = _owner.ConfigService.Config.MiniWorldLocalInstallations.OrderByDescending(item => item.LastLaunchedAt).ThenBy(item => item.Name).ToArray();
        if (installations.Length == 0) _detail.Children.Add(Text("还没有导入本地游戏。若只有 exe 安装包，请先运行安装；若是 zip/7z，请先完整解压，再导入 MicroMiniNew.exe、MicroMini.exe 或 CBLministart.exe。", 14));
        foreach (var installation in installations)
        {
            var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "GlowSoftBrush"); card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
            var body = new StackPanel(); body.Children.Add(new TextBlock { Text = installation.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(Text(installation.SourceName + " · " + installation.VersionName + "\n" + installation.ExecutablePath, 12));
            var buttons = new WrapPanel();
            var launch = Button("▶ 启动", () => _ = LaunchInstallationAsync(installation), true);
            launch.IsEnabled = File.Exists(installation.ExecutablePath) && !MiniWorldLaunchService.IsInstallerOrUtility(installation.ExecutablePath) && !_launching.Contains(installation.Id); buttons.Children.Add(launch);
            buttons.Children.Add(Button("编辑 / 更换程序", () => EditLocalInstallation(installation)));
            buttons.Children.Add(Button("打开目录", () =>
            {
                var directory = Path.GetDirectoryName(installation.ExecutablePath);
                if (directory != null && Directory.Exists(directory)) OpenDirectory(directory);
                else MessageBoxDialog.ShowWarning("该客户端目录已移动或丢失，请编辑启动设置重新选择。", "本地客户端");
            }));
            buttons.Children.Add(Button("移除记录", () =>
            {
                _owner.ConfigService.Config.MiniWorldLocalInstallations.RemoveAll(item => item.Id == installation.Id);
                _owner.ConfigService.Config.MiniWorldClientPaths.Remove(installation.BindingKey);
                foreach (var community in _owner.ConfigService.Config.MiniWorldCommunityClients.Where(item => item != null && "custom:" + item.Id == installation.SourceId && string.Equals(item.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                    community.ExecutablePath = "";
                _owner.ConfigService.Save(); RefreshSources("local-installed");
            })); body.Children.Add(buttons);
            if (!File.Exists(installation.ExecutablePath)) body.Children.Add(Text("启动文件不存在。请点“编辑 / 更换程序”重新选择。", 12));
            else body.Children.Add(Text("工作目录：" + installation.WorkingDirectory + "\n参数：" + (installation.Arguments?.Count > 0 ? string.Join(" · ", installation.Arguments) : "无"), 11));
            card.Child = body; _detail.Children.Add(card);
        }
    }

    private void SaveLocalInstallation(MiniWorldLocalInstallation installation)
    {
        var config = _owner.ConfigService.Config;
        var existing = config.MiniWorldLocalInstallations.FirstOrDefault(item => item.Id == installation.Id || item.BindingKey == installation.BindingKey);
        if (existing != null) { installation.Id = existing.Id; config.MiniWorldLocalInstallations.Remove(existing); }
        config.MiniWorldLocalInstallations.Add(installation); config.MiniWorldClientPaths[installation.BindingKey] = installation.ExecutablePath;
        _owner.ConfigService.Save(); RefreshSources("local-installed");
    }

    private void ImportLocalClient(MiniWorldSource? source = null, MiniWorldVersion? version = null)
    {
        var picker = new OpenFileDialog { Title = "导入安装/解压后的迷你世界游戏程序", Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        ImportCandidate(new MiniWorldLocalCandidate(picker.FileName, source?.Id ?? "local", source?.Name ?? "本地导入", "用户选择"), source, version);
    }

    private void ImportCandidate(MiniWorldLocalCandidate candidate, MiniWorldSource? source, MiniWorldVersion? version)
    {
        try
        {
            var seed = MiniWorldLaunchService.CreateInstallation(candidate.ExecutablePath, source?.Id ?? candidate.SourceId, source?.Name ?? candidate.SourceName,
                version?.ClientKey, version?.Id, version?.Version, version?.Name);
            var dialog = new MiniWorldLocalClientDialog(seed);
            if (dialog.ShowDialog() == true && dialog.Installation != null) SaveLocalInstallation(dialog.Installation);
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning("无法导入客户端：" + ex.Message, "本地客户端"); }
    }

    private void EditLocalInstallation(MiniWorldLocalInstallation installation)
    {
        var dialog = new MiniWorldLocalClientDialog(installation);
        if (dialog.ShowDialog() == true && dialog.Installation != null) SaveLocalInstallation(dialog.Installation);
    }

    private async Task DetectCommonClientsAsync()
    {
        if (_scanningLocal || _closed) return;
        var ct = _catalogCancellation.Token;
        await ScanAndChooseAsync(() => MiniWorldLaunchService.DetectCommonInstallations(ct), null, null);
    }

    private async Task ScanLocalDirectoryAsync(MiniWorldSource? source = null, MiniWorldVersion? version = null)
    {
        if (_scanningLocal || _closed) return;
        var picker = new OpenFolderDialog { Title = "选择已安装或已完整解压的游戏文件夹" };
        if (version != null && Directory.Exists(VersionDirectory(version))) picker.InitialDirectory = VersionDirectory(version);
        if (picker.ShowDialog() != true) return;
        var ct = _catalogCancellation.Token;
        await ScanAndChooseAsync(() => MiniWorldLaunchService.ScanDirectory(picker.FolderName, source?.Id ?? "local", source?.Name ?? "本地导入", ct), source, version);
    }

    private async Task ScanAndChooseAsync(Func<IReadOnlyList<MiniWorldLocalCandidate>> scan, MiniWorldSource? source, MiniWorldVersion? version)
    {
        _scanningLocal = true; ToastService.ShowInfo("正在检查本地游戏入口…");
        try
        {
            var candidates = await Task.Run(scan);
            if (_closed || !OverlayDialogService.IsOpen(this)) return;
            if (candidates.Count == 0) { MessageBoxDialog.ShowInfo("没有识别到 MicroMiniNew.exe、MicroMini.exe 或带 cbl_game_info.json 的 CBLministart.exe。若仍是安装包/压缩包，请先安装或完整解压；社区自定义入口可用“导入本地安装”手选。", "检测本地客户端"); return; }
            var dialog = new MiniWorldDetectedClientsDialog(candidates);
            if (dialog.ShowDialog() == true && dialog.Selected != null) ImportCandidate(dialog.Selected, source, version);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) MessageBoxDialog.ShowWarning("检查本地客户端失败：" + ex.Message, "本地客户端"); }
        finally { _scanningLocal = false; }
    }

    private async Task LaunchInstallationAsync(MiniWorldLocalInstallation installation)
    {
        if (!_launching.Add(installation.Id)) return;
        RefreshDetail();
        try
        {
            var prepared = MiniWorldLaunchService.CreateStartInfo(installation);
            installation.WorkingDirectory = prepared.WorkingDirectory; installation.Arguments = prepared.ArgumentList.ToList();
            var result = await MiniWorldLaunchService.LaunchAsync(installation, process =>
            {
                _owner.ProcessManager.Register(new GameProcessInfo(process, "迷你世界 · " + installation.Name + " · " + installation.VersionName,
                    installation.SourceName, installation.WorkingDirectory));
                process.EnableRaisingEvents = true;
            });
            installation.LastLaunchedAt = DateTimeOffset.Now; _owner.ConfigService.Save();
            if (_closed) return;
            if (result.ExitedEarly)
            {
                var message = $"启动程序在启动后很快退出，退出码：{result.ExitCode}。若游戏没有出现，请检查选择的 EXE、工作目录、参数和本地游戏文件是否完整。引导程序退出后也可能继续启动游戏。";
                if (result.ExitCode == 0) MessageBoxDialog.ShowInfo(message, "游戏启动反馈"); else MessageBoxDialog.ShowWarning(message, "游戏启动反馈");
            }
            else ToastService.ShowInfo("已启动本地客户端：" + installation.Name + $"（进程 {result.ProcessId}）");
        }
        catch (Exception ex)
        {
            var reason = ex is Win32Exception native ? native.NativeErrorCode switch
            {
                2 or 3 => "启动程序或依赖目录不存在，请重新选择安装后的游戏 EXE。",
                5 => "系统拒绝访问该程序，请检查文件权限或系统拦截提示。",
                193 => "该文件不是当前系统可运行的程序，可能下载不完整、尚未解压或平台不匹配。",
                740 => "该程序要求管理员权限。请确认选择的是正常游戏入口；需要提升权限时由你通过系统操作启动。",
                _ => native.Message
            } : ex.Message;
            if (!_closed) MessageBoxDialog.ShowWarning("启动失败：" + reason, "迷你世界启动失败");
        }
        finally { _launching.Remove(installation.Id); if (!_closed) RefreshDetail(); }
    }
}
