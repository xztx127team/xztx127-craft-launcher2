using System.Windows;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private bool _explicitExitRequested;
    private bool _processExitPreparing;
    private bool _processExitKeepRunning;
    private HashSet<GameProcessInfo> _confirmedExitGames = new();
    private HashSet<ServerProcessInfo> _confirmedExitServers = new();

    private void ResetProcessExitChoice()
    {
        _explicitExitRequested = false;
        _touchCloseDecisionHandled = false;
        _touchCloseKeepGameRunning = false;
        _processExitKeepRunning = false;
        _confirmedExitGames.Clear();
        _confirmedExitServers.Clear();
    }

    /// <summary>Only the processes visible at confirmation may be closed; newly launched games require a new choice.</summary>
    private bool ConfirmProcessExit()
    {
        if (_requestedExitMode != LauncherExitMode.Normal || _processExitKeepRunning || _touchCloseKeepGameRunning) return true;
        var games = ProcessManager.OwnedRunning.ToArray();
        var servers = ServerProcessManager.Processes.Where(p => !p.HasExited).ToArray();
        if (games.All(_confirmedExitGames.Contains) && servers.All(_confirmedExitServers.Contains)) return true;
        var names = games.Select(p => p.VersionId).Concat(servers.Select(p => p.DisplayName)).Take(8);
        var choice = MessageBoxDialog.ShowThreeChoice(
            $"仍有 {games.Length} 个游戏、{servers.Length} 个服务器由启动器启动并运行：\n" + string.Join("\n", names) +
            "\n\n剥离后游戏继续运行，启动器的日志、性能监视和触屏按键会停止。连带关闭会先请求游戏退出、服务器保存世界。外部启动的游戏不会被关闭。",
            "退出时如何处理游戏？", "取消", "连带关闭游戏", "剥离子进程，保留游戏");
        if (choice == XclMessageResult.Cancel) return false;
        _processExitKeepRunning = choice == XclMessageResult.Yes;
        _confirmedExitGames = games.ToHashSet();
        _confirmedExitServers = servers.ToHashSet();
        return true;
    }

    private bool HasConfirmedProcessesToClose => LauncherExitPolicy.ShouldCloseGames(
        _requestedExitMode, _processExitKeepRunning || _touchCloseKeepGameRunning, explicitlyCloseGames: true) &&
        (_confirmedExitGames.Any(p => !p.HasExited) || _confirmedExitServers.Any(p => !p.HasExited));

    private async Task CloseConfirmedProcessesAsync()
    {
        var gameTasks = _confirmedExitGames.Where(p => !p.HasExited).Select(p => p.CloseAsync()).ToArray();
        var serverTasks = _confirmedExitServers.Where(p => !p.HasExited)
            .Select(p => p.StopGracefullyAsync(TimeSpan.FromSeconds(20))).ToArray();
        await Task.WhenAll(gameTasks.Concat(serverTasks));
        var failures = _confirmedExitGames.Where(p => !p.HasExited).Select(p => p.VersionId)
            .Concat(_confirmedExitServers.Where(p => !p.HasExited).Select(p => p.DisplayName)).ToArray();
        if (failures.Length > 0)
            throw new InvalidOperationException("以下进程仍在运行，本次退出已取消。可以再次选择保留游戏后退出：\n" + string.Join("\n", failures));
    }
}
