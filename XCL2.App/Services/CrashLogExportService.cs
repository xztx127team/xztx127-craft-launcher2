using System.IO;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 合并启动器滚动缓冲、游戏进程滚动输出与磁盘日志的诊断片段。
/// 大文件读取前限制为 512 KiB 首尾片段，并标注截断；原始日志文件保持不变。
/// </summary>
public static class CrashLogExportService
{
    /// <summary>组装有上限的诊断日志文本，供预览与导出共用。</summary>
    public static string BuildCombinedLog(GameProcessInfo? processInfo)
    {
        var sb = new StringBuilder();

        sb.AppendLine("========================================");
        sb.AppendLine("XCL2 崩溃诊断日志导出");
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("========================================");
        sb.AppendLine();

        sb.AppendLine("---------- 一、启动器日志（本次会话） ----------");
        var launcherLog = LauncherLogService.GetBufferedText();
        sb.AppendLine(string.IsNullOrWhiteSpace(launcherLog) ? "(空)" : launcherLog);
        sb.AppendLine();

        sb.AppendLine("---------- 二、游戏崩溃前的控制台输出 ----------");
        if (processInfo != null)
        {
            string gameOutput;
            lock (processInfo.OutputBuffer) gameOutput = processInfo.OutputBuffer.ToString();
            sb.AppendLine(string.IsNullOrWhiteSpace(gameOutput) ? "(没有捕获到任何输出)" : gameOutput);
        }
        else
        {
            sb.AppendLine("(没有可用的游戏进程输出)");
        }
        sb.AppendLine();

        sb.AppendLine("---------- 三、游戏日志文件（logs/latest.log） ----------");
        var gameLogText = TryReadGameLatestLog(processInfo);
        sb.AppendLine(gameLogText ?? "(没有找到游戏的 logs/latest.log 文件)");

        return DiagnosticRedactionService.Sanitize(sb.ToString());
    }

    /// <summary>从游戏进程记录的工作目录里找 logs/latest.log 并读取有上限的诊断片段。找不到就返回 null。</summary>
    private static string? TryReadGameLatestLog(GameProcessInfo? processInfo)
    {
        if (processInfo == null) return null;
        try
        {
            var logPath = Path.Combine(processInfo.GameDir, "logs", "latest.log");
            if (File.Exists(logPath))
                return DiagnosticLogReader.Read(logPath, includeHead: true);
        }
        catch { /* 读取失败就当作没有，不阻断其余内容的展示/导出 */ }
        return null;
    }

    /// <summary>把合并日志保存到指定路径，静默失败时抛出异常交给调用方处理（调用方通常会弹友好错误提示）。</summary>
    public static void ExportTo(string filePath, GameProcessInfo? processInfo)
    {
        File.WriteAllText(filePath, BuildCombinedLog(processInfo));
    }
}
