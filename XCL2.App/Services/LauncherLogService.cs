using System.IO;
using System.Text;

namespace XCL2.App.Services;

public static class LauncherLogService
{
    private static readonly object Lock = new();
    private static readonly BoundedLogBuffer Buffer = new();
    private static string? _sessionLogPath;
    private static string? _spoolPath;
    private static bool _spoolIncomplete;
    private static bool _written;

    /// <summary>这一次启动器运行会话最终要写入的日志文件完整路径。
    /// 在 <see cref="BeginSession"/> 调用后才有值。</summary>
    public static string? SessionLogPath => _sessionLogPath;

    /// <summary>
    /// 在启动器刚启动、尽早的时机调用一次：确定本次会话的日志文件名并记下"启动"这一行。
    /// 需要传入 ConfigService 以便读取/更新"今日第几次启动"的计数并持久化。
    /// </summary>
    public static void BeginSession(ConfigService configService)
    {
        try
        {
            var now = DateTime.Now;
            var today = now.ToString("yyyy-MM-dd");
            var cfg = configService.Config;

            // 跨天自动重置：记录的日期跟今天不一样，说明是新的一天的第一次启动。
            if (cfg.LastLaunchCountDate != today)
            {
                cfg.LastLaunchCountDate = today;
                cfg.TodayLauncherStartCount = 0;
            }
            cfg.TodayLauncherStartCount++;
            configService.Save();

            // 文件名格式：日期-时间-分钟-今日第几次启动启动器.log
            // 例如：2026-08-04-14-30-3启动器.log（今天第 3 次打开启动器，14 点 30 分）
            var fileName = $"{now:yyyy-MM-dd-HH-mm}-{cfg.TodayLauncherStartCount}启动器.log";
            _sessionLogPath = Path.Combine(App.DataDir, "logs", fileName);

            AppendLine($"===== XCL2 启动器启动 {now:yyyy-MM-dd HH:mm:ss}（今日第 {cfg.TodayLauncherStartCount} 次启动）=====");
        }
        catch (Exception ex)
        {
            // 会话日志是锦上添花的功能，初始化失败不应该阻止启动器继续运行。
            ErrorPresenter.LogTechnicalDetail($"[{DateTime.Now}] 会话日志初始化失败：{ex}\n");
        }
    }

    /// <summary>追加一行日志到本次会话的内存缓冲（自动带时间戳）。</summary>
    public static void AppendLine(string line)
    {
        // Sanitize before the first storage boundary: the live buffer can spill to
        // disk long before a user opens the diagnostic export/preview dialog.
        var safeLine = DiagnosticRedactionService.Sanitize(line);
        lock (Lock)
        {
            var discarded = Buffer.Append('[' + DateTime.Now.ToString("HH:mm:ss") + "] " + safeLine + Environment.NewLine);
            if (discarded.Length == 0) return;
            try
            {
                _spoolPath ??= Path.Combine(Path.GetTempPath(), "XCL2-session-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N") + ".log");
                File.AppendAllText(_spoolPath, discarded, new UTF8Encoding(false));
            }
            catch { _spoolIncomplete = true; } // The live view remains bounded even if disk is unavailable.
        }
    }

    /// <summary>Bounded diagnostic snapshot, without allocating the full session log.</summary>
    public static string GetBufferedTail(int maxChars = 384 * 1024)
    {
        maxChars = Math.Clamp(maxChars, 256, 384 * 1024);
        lock (Lock)
        {
            return Buffer.GetTail(maxChars);
        }
    }

    /// <summary>返回有界的实时会话日志；发生裁剪时包含明确提示，磁盘会话日志仍保留完整前缀。</summary>
    public static string GetBufferedText()
    {
        lock (Lock) return Buffer.GetText();
    }

    /// <summary>No string allocation or projection is required while the revision stays unchanged.</summary>
    public static bool TryGetBufferedText(ref long revision, out string text)
    {
        lock (Lock) return Buffer.TryGetChangedText(ref revision, out text);
    }

    /// <summary>
    /// 启动器关闭时调用：把本次会话缓冲的全部日志一次性写入文件。
    /// 幂等：重复调用只会真正写盘一次（MainWindow.Closed 和 App 退出兜底都可能触发一次，
    /// 避免出现"写了一半又被截断重写"的竞争）。
    /// </summary>
    public static void EndSessionAndFlush()
    {
        lock (Lock)
        {
            if (_written || _sessionLogPath == null) return;
            _written = true;
            try
            {
                AppendLine($"===== XCL2 启动器关闭 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                Directory.CreateDirectory(Path.GetDirectoryName(_sessionLogPath)!);
                using (var destination = File.Create(_sessionLogPath))
                {
                    if (_spoolIncomplete)
                    {
                        var warning = Encoding.UTF8.GetBytes("[较早日志写入临时文件失败，本文件可能缺少部分内容]" + Environment.NewLine);
                        destination.Write(warning);
                    }
                    if (_spoolPath != null && File.Exists(_spoolPath))
                        using (var prefix = File.OpenRead(_spoolPath)) prefix.CopyTo(destination);
                    using var writer = new StreamWriter(destination, new UTF8Encoding(false));
                    writer.Write(Buffer.GetText(includeTruncationNotice: false));
                }
                if (_spoolPath != null) { try { File.Delete(_spoolPath); } catch { } }
            }
            catch (Exception ex)
            {
                // 关闭阶段写文件失败没有办法再补救，静默忽略，不能在退出流程里抛异常。
                ErrorPresenter.LogTechnicalDetail($"[{DateTime.Now}] 会话日志写入失败：{ex}\n");
            }
        }
    }
}
