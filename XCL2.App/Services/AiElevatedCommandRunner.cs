using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

internal sealed class AiElevatedCommandResult
{
    public int? ExitCode { get; set; }
    public string Output { get; set; } = "";
    public bool Cancelled { get; set; }
    public bool TimedOut { get; set; }
    public bool StopConfirmed { get; set; }
    public bool UacDenied { get; set; }
    public bool PermissionRevoked { get; set; }
    public bool CommandNotStarted { get; set; }
    public bool AwaitingUac { get; set; }
    public string Nonce { get; set; } = "";
}

/// <summary>Uses the OS consent prompt; no saved administrator credentials and no silent privilege changes.</summary>
internal static class AiElevatedCommandRunner
{
    internal const int MaximumEncodedCommandLength = 30000;

    public static async Task<AiElevatedCommandResult> RunAsync(ProcessStartInfo command, int timeoutSeconds,
        Action validateBeforeConsent, Action authorizeExecution, Func<bool> isStillAllowed, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Failure("管理员权限重试仅支持 Windows。", true);
        if (!Path.IsPathFullyQualified(command.FileName) || !File.Exists(command.FileName))
            return Failure("解释器路径不是有效的绝对路径，未提权执行。", true);
        if (!Path.IsPathFullyQualified(command.WorkingDirectory) || !Directory.Exists(command.WorkingDirectory))
            return Failure("工作目录无效，未提权执行。", true);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return Failure("无法创建当前用户的临时结果目录，未提权执行。", true);
        var folder = Path.Combine(local, "XCL2", "AiElevatedCommands", Guid.NewGuid().ToString("N"));
        var nonce = Guid.NewGuid().ToString("N");
        Process? wrapper = null;
        var launchReturned = false;
        var commandAuthorized = false;
        var cleanup = false;
        try
        {
            Directory.CreateDirectory(folder);
            RejectLinkedParents(folder);
            using var parent = Process.GetCurrentProcess();
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                Executable = command.FileName, Arguments = command.ArgumentList.ToArray(),
                WorkingDirectory = command.WorkingDirectory, Timeout = Math.Clamp(timeoutSeconds, 1, 120),
                Folder = folder, Nonce = nonce, ParentId = Environment.ProcessId,
                ParentStart = parent.StartTime.ToUniversalTime().Ticks
            })));
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(WorkerScript.Replace("__XCL_PAYLOAD__", payload)));
            if (encoded.Length > MaximumEncodedCommandLength)
            {
                cleanup = true;
                return Failure("命令过长，无法安全传入 Windows 提权窗口；未重试。请拆成较短的具体命令。", true);
            }
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded
            };
            void Stop()
            {
                try { File.WriteAllText(Path.Combine(folder, "cancel"), "stop", Encoding.UTF8); }
                catch { /* An inaccessible cancellation channel is reported as unconfirmed below. */ }
            }
            using var cancellation = ct.Register(Stop);
            try
            {
                // UAC can wait for the user; keep the launcher dispatcher responsive. The callback
                // rechecks the exact command and current mode immediately before ShellExecute.
                var launch = Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    validateBeforeConsent();
                    ct.ThrowIfCancellationRequested();
                    return Process.Start(start);
                });
                using var consentTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 120)));
                using var consentWait = CancellationTokenSource.CreateLinkedTokenSource(ct, consentTimeout.Token);
                try { wrapper = await launch.WaitAsync(consentWait.Token); launchReturned = true; }
                catch (OperationCanceledException) when (consentWait.IsCancellationRequested)
                {
                    Stop();
                    // Do not abandon or misreport the native runas call. It cannot be forcibly
                    // dismissed safely; its late worker gets cancellation and never authorization.
                    _ = ObserveLateLaunchAsync(launch, folder, nonce);
                    return new AiElevatedCommandResult { Cancelled = ct.IsCancellationRequested,
                        TimedOut = !ct.IsCancellationRequested, CommandNotStarted = true, AwaitingUac = true,
                        StopConfirmed = false, Output = "已取消本次授权等待，原命令未以管理员权限启动。Windows UAC 窗口可能仍在等待；请在该窗口选择“否”。即使稍后允许，辅助进程也只会读取取消信号并退出，不会执行命令。" };
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                cleanup = true;
                return new AiElevatedCommandResult { UacDenied = true, StopConfirmed = true, Output = "用户在 Windows UAC 窗口中拒绝了管理员权限，命令未重试。" };
            }
            // The elevated worker cannot start a command until this post-UAC one-shot grant is
            // written. Switching to Plan/revoking settings while UAC is open therefore wins.
            ct.ThrowIfCancellationRequested();
            authorizeExecution();
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(folder, "authorize.tmp"), nonce, Encoding.UTF8);
            ct.ThrowIfCancellationRequested();
            if (!isStillAllowed()) throw new UnauthorizedAccessException("系统：授权信号写入前，模式或命令权限已撤销。");
            File.Move(Path.Combine(folder, "authorize.tmp"), Path.Combine(folder, "authorize"));
            commandAuthorized = true;
            var waiting = Stopwatch.StartNew();
            var stopRequestedAt = (long?)null;
            var revoked = false;
            var timeout = Math.Clamp(timeoutSeconds, 1, 120);
            while (true)
            {
                var result = ReadResult(folder, nonce);
                if (result != null)
                {
                    result.PermissionRevoked = revoked;
                    cleanup = result.StopConfirmed;
                    return result;
                }
                if (!revoked && !isStillAllowed()) revoked = true;
                if (ct.IsCancellationRequested || revoked || waiting.Elapsed.TotalSeconds > timeout + 4)
                {
                    if (!stopRequestedAt.HasValue) { stopRequestedAt = waiting.ElapsedMilliseconds; Stop(); }
                    if (waiting.ElapsedMilliseconds - stopRequestedAt.Value >= 6000)
                        return new AiElevatedCommandResult { Cancelled = ct.IsCancellationRequested,
                            TimedOut = !ct.IsCancellationRequested && !revoked, PermissionRevoked = revoked, StopConfirmed = false,
                            Output = "已请求终止管理员命令，但尚未收到终止确认。请检查系统进程；本次可能已产生部分修改。" };
                }
                if (HasExited(wrapper))
                {
                    // The result file is atomically renamed before the wrapper exits.
                    result = ReadResult(folder, nonce);
                    if (result != null) { result.PermissionRevoked = revoked; cleanup = result.StopConfirmed; return result; }
                    return Failure("管理员执行窗口已退出，但未返回有效结果，无法确认子进程状态。请检查系统进程。", false);
                }
                await Task.Delay(100);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var stopped = launchReturned ? await StopAndReadAsync(folder, nonce, wrapper) : null;
            cleanup = !launchReturned || stopped?.StopConfirmed == true;
            return new AiElevatedCommandResult { Cancelled = true, StopConfirmed = cleanup,
                CommandNotStarted = !commandAuthorized,
                Output = !commandAuthorized ? "管理员命令启动前已取消。" : cleanup ? "已请求停止，管理员主进程已结束；可能已有部分修改。" : "已取消等待，无法确认管理员进程已结束。" };
        }
        catch (Exception ex)
        {
            var stopped = launchReturned ? await StopAndReadAsync(folder, nonce, wrapper) : null;
            cleanup = !launchReturned || stopped?.StopConfirmed == true;
            return Failure("管理员重试失败：" + ex.Message + (!commandAuthorized ? "（原命令未提权启动）" : cleanup ? "（已请求停止，主进程已结束，可能已有部分修改）" : "（无法确认子进程状态）"), cleanup);
        }
        finally
        {
            wrapper?.Dispose();
            // If stopping is not confirmed, leave the cancellation channel in place for the worker.
            if (cleanup) Cleanup(folder);
        }
    }

    private static AiElevatedCommandResult Failure(string output, bool stopped)
        => new() { Output = output, StopConfirmed = stopped };

    private static async Task ObserveLateLaunchAsync(Task<Process?> launch, string folder, string nonce)
    {
        try
        {
            using var process = await launch.ConfigureAwait(false);
            // This path never writes an authorization file. Even a late UAC approval can only
            // start the fixed helper, which exits before launching any requested command.
            var result = await StopAndReadAsync(folder, nonce, process).ConfigureAwait(false);
            if (result?.StopConfirmed == true || HasExited(process)) Cleanup(folder);
        }
        catch { Cleanup(folder); } // native launch failed/was declined; no command authorization exists
    }

    private static async Task<AiElevatedCommandResult?> StopAndReadAsync(string folder, string nonce, Process? wrapper)
    {
        try
        {
            File.WriteAllText(Path.Combine(folder, "cancel"), "stop", Encoding.UTF8);
            var until = Stopwatch.StartNew();
            while (until.ElapsedMilliseconds < 5500)
            {
                var result = ReadResult(folder, nonce);
                if (result != null) return result;
                if (HasExited(wrapper)) break;
                await Task.Delay(100);
            }
        }
        catch { }
        return null;
    }

    private static bool HasExited(Process? process)
    {
        if (process == null) return false;
        try { return process.HasExited; } catch { return false; }
    }

    private static AiElevatedCommandResult? ReadResult(string folder, string nonce)
    {
        var file = Path.Combine(folder, "result.json");
        if (!File.Exists(file)) return null;
        RejectLinkedParents(file);
        if (new FileInfo(file).Length > 262144) throw new IOException("管理员返回结果超过上限。");
        var result = JsonSerializer.Deserialize<AiElevatedCommandResult>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result?.Nonce != nonce) throw new IOException("管理员结果与本次请求不匹配。");
        result.Output ??= "";
        if (result.Output.Length > 40000) result.Output = result.Output[..40000] + "\n……（输出已截断）";
        return result;
    }

    private static void RejectLinkedParents(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("提权结果目录不能使用文件系统链接。");
    }

    private static void Cleanup(string folder)
    {
        try
        {
            RejectLinkedParents(folder);
            foreach (var name in new[] { "result.json", "result.tmp", "cancel", "authorize", "authorize.tmp" })
            {
                var file = Path.Combine(folder, name);
                if (File.Exists(file)) { RejectLinkedParents(file); File.Delete(file); }
            }
            Directory.Delete(folder, false);
        }
        catch { /* Unknown files/links are never traversed or recursively removed. */ }
    }

    // Command text and arguments are JSON data, then base64; none are interpolated into PowerShell
    // syntax. The fixed worker uses bounded native .NET output handlers, not PS runspace callbacks.
    private const string WorkerScript = """
$ErrorActionPreference='Stop'
$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__XCL_PAYLOAD__')) | ConvertFrom-Json
$code = @'
using System;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
public sealed class XclElevatedResult {
  public int? ExitCode { get; set; }
  public string Output { get; set; }
  public bool Cancelled { get; set; }
  public bool TimedOut { get; set; }
  public bool StopConfirmed { get; set; }
  public bool CommandNotStarted { get; set; }
  public string Nonce { get; set; }
}
public static class XclElevatedWorker {
  private static string Quote(string value) {
    if (value.Length > 0 && value.IndexOfAny(new char[] {' ', '\t', '\n', '\v', '"'}) < 0) return value;
    var text = new StringBuilder("\""); int slashes = 0;
    foreach (char ch in value) {
      if (ch == '\\') { slashes++; continue; }
      if (ch == '"') { text.Append('\\', slashes * 2 + 1); text.Append(ch); slashes = 0; continue; }
      text.Append('\\', slashes); slashes = 0; text.Append(ch);
    }
    text.Append('\\', slashes * 2); text.Append('"'); return text.ToString();
  }
  private static bool ParentAlive(int id, long ticks) {
    try { using (var p = Process.GetProcessById(id)) return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == ticks; }
    catch { return false; }
  }
  private static void Drain(StreamReader reader, StringBuilder target, object gate) {
    try { var buffer = new char[1024]; int count; while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
      lock (gate) { var keep = Math.Min(count, Math.Max(0, 16000 - target.Length)); if (keep > 0) target.Append(buffer, 0, keep); }
    } catch (IOException) { } catch (ObjectDisposedException) { }
  }
  private static bool Stop(Process proc) {
    try {
      var killStart = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"), "/PID " + proc.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true };
      using (var killer = Process.Start(killStart)) { if (killer != null) killer.WaitForExit(2500); }
      return proc.WaitForExit(2000);
    } catch { return false; }
  }
  public static XclElevatedResult Run(string executable, string[] args, string directory, int seconds, string folder, string nonce, int parentId, long parentStart) {
    var result = new XclElevatedResult { Nonce = nonce, Output = "", StopConfirmed = true, CommandNotStarted = true };
    var output = new StringBuilder(); var error = new StringBuilder(); var gate = new object();
    var consent = Stopwatch.StartNew(); var approved = false;
    while (consent.Elapsed.TotalSeconds < 15) {
      if (File.Exists(Path.Combine(folder, "cancel")) || !ParentAlive(parentId, parentStart)) { result.Cancelled = true; return result; }
      var authorization = Path.Combine(folder, "authorize");
      if (File.Exists(authorization) && File.ReadAllText(authorization).Trim() == nonce) { approved = true; break; }
      Thread.Sleep(50);
    }
    if (!approved) { result.Cancelled = true; result.Output = "未收到当前请求的启动授权，管理员命令未执行。"; return result; }
    if (File.Exists(Path.Combine(folder, "cancel")) || !ParentAlive(parentId, parentStart)) { result.Cancelled = true; return result; }
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
      RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    var joined = new StringBuilder(); foreach (var arg in args) { if (joined.Length > 0) joined.Append(' '); joined.Append(Quote(arg)); }
    start.Arguments = joined.ToString();
    using (var proc = new Process { StartInfo = start }) {
      if (!proc.Start()) throw new InvalidOperationException("无法启动管理员子进程。");
      result.CommandNotStarted = false;
      var stdout = Task.Run(delegate { Drain(proc.StandardOutput, output, gate); });
      var stderr = Task.Run(delegate { Drain(proc.StandardError, error, gate); });
      try {
        proc.StandardInput.Close();
        var watch = Stopwatch.StartNew();
        while (!proc.WaitForExit(100)) {
          var cancel = File.Exists(Path.Combine(folder, "cancel")) || !ParentAlive(parentId, parentStart);
          if (!cancel && watch.Elapsed.TotalSeconds <= seconds) continue;
          result.Cancelled = cancel; result.TimedOut = !cancel; result.StopConfirmed = Stop(proc); break;
        }
        if (proc.HasExited) { result.ExitCode = proc.ExitCode; result.StopConfirmed = true; }
      } catch (Exception ex) {
        result.StopConfirmed = Stop(proc); result.Output = "管理员子进程执行失败：" + ex.Message + "\n";
      }
      try { Task.WaitAll(new Task[] { stdout, stderr }, 500); } catch { }
      try { proc.StandardOutput.Close(); proc.StandardError.Close(); } catch { }
      lock (gate) result.Output += output.ToString() + (error.Length > 0 ? "\n[stderr]\n" + error : "");
      if (output.Length >= 16000 || error.Length >= 16000) result.Output += "\n……（输出已截断）";
    }
    return result;
  }
}
'@
try {
  Add-Type -TypeDefinition $code -Language CSharp
  $r=[XclElevatedWorker]::Run([string]$p.Executable, [string[]]$p.Arguments, [string]$p.WorkingDirectory, [int]$p.Timeout, [string]$p.Folder, [string]$p.Nonce, [int]$p.ParentId, [long]$p.ParentStart)
} catch {
  $r=@{ Nonce=[string]$p.Nonce; ExitCode=$null; Output=('管理员执行器失败：' + $_.Exception.Message); StopConfirmed=$false; Cancelled=$false; TimedOut=$false }
}
$json=ConvertTo-Json -InputObject $r -Depth 5 -Compress
[IO.File]::WriteAllText([IO.Path]::Combine([string]$p.Folder,'result.tmp'),$json,[Text.Encoding]::UTF8)
[IO.File]::Move([IO.Path]::Combine([string]$p.Folder,'result.tmp'),[IO.Path]::Combine([string]$p.Folder,'result.json'))
""";
}
