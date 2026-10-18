namespace XCL2.App.Services;

public enum LauncherExitMode { Normal, PreserveGamesAfterLaunch, PreserveGamesForUpdate }

/// <summary>自动关闭只结束启动器；用户普通退出仍使用原有游戏清理策略。</summary>
public static class LauncherExitPolicy
{
    public static bool ShouldCloseGames(LauncherExitMode mode, bool explicitlyKeepGames)
        => mode == LauncherExitMode.Normal && !explicitlyKeepGames;
}
