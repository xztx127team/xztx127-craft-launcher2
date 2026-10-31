namespace XCL2.App.Services;

public enum LauncherExitMode { Normal, PreserveGamesAfterLaunch, PreserveGamesForUpdate }

/// <summary>自动退出保留游戏；普通退出也只有明确确认后才能结束游戏。</summary>
public static class LauncherExitPolicy
{
    public static bool ShouldCloseGames(LauncherExitMode mode, bool explicitlyKeepGames, bool explicitlyCloseGames = false)
        => mode == LauncherExitMode.Normal && explicitlyCloseGames && !explicitlyKeepGames;
}
