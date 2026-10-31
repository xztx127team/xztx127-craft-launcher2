using System.IO;
using System.Windows;

namespace XCL2.App.Services;

/// <summary>统一展示可操作的错误报告：分类代码、唯一事件编号、脱敏详情和下一步指引。</summary>
public static class ErrorPresenter
{
    /// <summary>本项目的 GitHub 仓库地址，日志/问题反馈的默认落脚点。</summary>
    public const string GitHubRepoUrl = "https://github.com/adorable0127/xztx127-craft-launcher2";

    /// <summary>报告正文同时用于弹窗预览、复制/导出与本地日志，以报告编号关联同一次故障。</summary>
    public static void ShowFriendlyError(string friendlySummary, string technicalDetail, string title = "出了点问题")
    {
        var report = ErrorReport.Create(friendlySummary, technicalDetail, title);
        var logged = TryLogTechnicalDetail(report.ToText());
        void Show() => Views.FriendlyErrorDialog.Show(report, logged);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(Show);
        else Show();
    }


    /// <summary>把技术细节追加写入 xcl2/logs/crash.log，静默失败（写日志本身不应该再抛出新异常打断主流程）。</summary>
    public static void LogTechnicalDetail(string technicalDetail) => TryLogTechnicalDetail(technicalDetail);

    public static bool TryLogTechnicalDetail(string technicalDetail)
    {
        try
        {
            var logDir = Path.Combine(App.DataDir, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {DiagnosticRedactionService.Sanitize(technicalDetail, 32000)}\n\n");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 记录那些为了不中断主流程而被兜底吞掉的异常。调用方继续按原逻辑降级/忽略，
    /// 但排查问题时能在 crash.log 和会话日志里看到发生过什么。
    /// </summary>
    public static void LogFallback(string context, Exception? ex = null)
    {
        var detail = ex == null
            ? $"[兜底日志] {context}"
            : $"[兜底日志] {context}\n{ex}";

        LogTechnicalDetail(detail);
        try
        {
            LauncherLogService.AppendLine(ex == null
                ? $"[兜底] {context}"
                : $"[兜底] {context}: {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // 会话日志自身不可用时不能影响原有兜底路径。
        }
    }
}
