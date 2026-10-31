using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace XCL2.App.Services;

public class LauncherLogWindowService : IDisposable
{
    private Process? _cmdProcess;
    private string? _logFilePath;
    private DispatcherTimer? _syncTimer;
    private string _lastSyncedText = "";
    private long _lastSyncedRevision = -1;

    /// <summary>
    /// 弹出一个独立的 CMD 窗口，持续镜像显示当前会话的有界实时启动器日志
    /// （LauncherLogService.GetBufferedText()）。
    /// </summary>
    public void Open()
    {
        try
        {
            var logDir = Path.Combine(Path.GetTempPath(), "XCL2-launcher-logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"launcher-log-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            // 先把已有内容一次性写入文件，避免用户打开窗口时前面已经发生的日志漏看。
            LauncherLogService.TryGetBufferedText(ref _lastSyncedRevision, out var initial);
            File.WriteAllText(_logFilePath,
                "==== XCL2 启动器完整日志（独立窗口，实时镜像，只读）====" + Environment.NewLine, Encoding.UTF8);
            if (!string.IsNullOrEmpty(initial))
            {
                File.AppendAllText(_logFilePath, initial, Encoding.UTF8);
                _lastSyncedText = initial;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false
            };
            // 跟 GameConsoleWindowService 同样的道理：/k 而不是 /c，保证窗口不会因为
            // 某条命令跑完就自动关闭；powershell -Wait -Tail 持续跟踪文件末尾新增内容。
            var escapedLogPath = _logFilePath.Replace("'", "''");
            psi.Arguments =
                "/k \"chcp 65001>nul & title XCL2 - 启动器日志（独立窗口） & " +
                $"powershell -NoLogo -NoProfile -Command \"& {{ Get-Content -LiteralPath '{escapedLogPath}' -Wait -Tail 2000 -Encoding UTF8 }}\"\"";

            _cmdProcess = Process.Start(psi);

            // 每秒把最新的内存日志同步进文件——跟 LogsPage 里 FullLauncherLogBox 的自动刷新
            // 频率保持一致，用户在两边看到的内容几乎没有感知上的时间差。
            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _syncTimer.Tick += (_, _) => SyncToFile();
            _syncTimer.Start();
        }
        catch
        {
            // 常见原因：找不到 cmd.exe/powershell 或系统限制创建新控制台窗口。静默失败，
            // 不影响启动器主窗口内「完整启动器日志」Tab 本身的查看功能。
            Dispose();
        }
    }

    private void SyncToFile()
    {
        if (_logFilePath == null) return;
        try
        {
            var revision = _lastSyncedRevision;
            if (!LauncherLogService.TryGetBufferedText(ref revision, out var text)) return; // No copy or IO while unchanged.

            // Append until the bounded live buffer rotates; after rotation mirror its new tail and notice.
            if (text.StartsWith(_lastSyncedText, StringComparison.Ordinal))
            {
                var delta = text[_lastSyncedText.Length..];
                File.AppendAllText(_logFilePath, delta, Encoding.UTF8);
            }
            else
            {
                File.WriteAllText(_logFilePath,
                    "==== XCL2 启动器完整日志（独立窗口，实时镜像，只读）====" + Environment.NewLine + text,
                    Encoding.UTF8);
            }
            _lastSyncedText = text;
            _lastSyncedRevision = revision;
        }
        catch { /* 文件可能被占用或已被清理，忽略，不影响启动器本身 */ }
    }

    public void Dispose()
    {
        _syncTimer?.Stop();
        _syncTimer = null;
    }
}
