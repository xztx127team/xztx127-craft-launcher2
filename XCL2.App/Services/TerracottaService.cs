using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 运行未经修改的 Terracotta；原生模式使用上游 v0.4.2 的 --hmcl 端口文件协议。
/// 通过实际 HTTP API 为 WPF 原生控件提供房间、成员、连接与日志数据，不打开网页。
/// </summary>
public class TerracottaService
{
    private static readonly object ExtractionLock = new();
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);
    private static (string Executable, Session Session)? CachedSession;
    private static readonly HttpClient LocalClient = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(3) };
    private readonly TerracottaApiClient _api = new(LocalClient);
    public sealed record Session(Uri Endpoint, string Version, string EasyTierVersion)
    {
        public TerracottaMetadata? Metadata { get; init; }
    }

    public Task<Session> LaunchNativeAsync(string? configuredOverridePath, CancellationToken token = default)
        => LaunchEmbeddedAsync(configuredOverridePath, token);
    public Task<TerracottaMetadata> GetMetadataAsync(Uri endpoint, CancellationToken token = default)
        => _api.GetMetadataAsync(endpoint, token);
    public Task<TerracottaRoomState> GetStateAsync(Uri endpoint, CancellationToken token = default)
        => _api.GetStateAsync(endpoint, token);
    public Task<TerracottaRoomState> CreateRoomAsync(Uri endpoint, string? room = null, string? player = null,
        IReadOnlyList<string>? publicNodes = null, CancellationToken token = default)
        => _api.CreateRoomAsync(endpoint, room, player, publicNodes, token);
    public Task<TerracottaRoomState> JoinRoomAsync(Uri endpoint, string roomCode, string? player = null,
        IReadOnlyList<string>? publicNodes = null, CancellationToken token = default)
        => _api.JoinRoomAsync(endpoint, roomCode, player, publicNodes, token);
    public Task<TerracottaRoomState> LeaveRoomAsync(Uri endpoint, CancellationToken token = default)
        => _api.LeaveRoomAsync(endpoint, token);
    /// <summary>显式退出全局服务；不能用于页面离开或取消等待。</summary>
    public Task<TerracottaServiceStopResult> StopServiceAsync(Uri endpoint, CancellationToken token = default)
        => _api.StopServiceAsync(endpoint, token);

    public const string ReleasesUrl = "https://github.com/burningtnt/Terracotta/releases";
    public const string ProjectHomeUrl = "https://github.com/burningtnt/Terracotta";

    private static string? EmbeddedResourceName => TerracottaArchitecture.ChooseEmbedded(
        Assembly.GetExecutingAssembly().GetManifestResourceNames(), System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
    public static bool HasEmbeddedExecutable => EmbeddedResourceName != null;

    private string EnsureExtracted()
    {
        lock (ExtractionLock) return ExtractCore();
    }

    private string ExtractCore()
    {
        var dir = Path.Combine(App.DataDir, "terracotta");
        Directory.CreateDirectory(dir);
        var resourceName = EmbeddedResourceName ?? throw new PlatformNotSupportedException(
            "[XCL-LAN-001] 当前构建没有兼容本机架构的陶瓦本体。32 位 Windows 需要 x86 版本；上游 v0.4.2 仅发布 Windows x64 和 ARM64。可通过“使用其他版本”选择兼容本体。");
        var targetPath = Path.Combine(dir, resourceName["XCL2.App.Resources.Terracotta.".Length..]);

        var assembly = Assembly.GetExecutingAssembly();
        using var resourceStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"当前构建没有携带内置陶瓦联机资源（{EmbeddedResourceName}）。请点击“使用其他版本”选择 Terracotta 可执行文件；如果你是从源码构建，也可以把对应 exe 放回 Resources\\Terracotta 后重新编译。");

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == resourceStream.Length)
        {
            using var current = File.OpenRead(targetPath);
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), SHA256.HashData(resourceStream)))
                return targetPath;
            resourceStream.Position = 0;
        }

        // 先写到临时文件再原子性地移动过去，避免"写到一半时用户正好又点了一次启动"
        // 或者程序被强制结束导致目标路径留下一个不完整、无法运行的半成品文件。
        var tempPath = targetPath + ".tmp";
        using (var fileStream = File.Create(tempPath))
        {
            resourceStream.CopyTo(fileStream);
        }
        File.Move(tempPath, targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>
    /// 解析当前应该使用的陶瓦联机可执行文件路径：
    /// 1. 如果用户手动指定过覆盖路径(AppConfig.TerracottaExecutablePath)且文件仍然存在，用那个；
    /// 2. 否则(默认情况)使用内置版本，首次调用时自动释放到本地。
    /// 如果当前构建未携带内置二进制且用户也没有指定覆盖路径，会抛出带修复说明的异常。
    /// </summary>
    public string ResolveExecutable(string? configuredOverridePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredOverridePath))
        {
            if (!File.Exists(configuredOverridePath)) throw new FileNotFoundException("配置的陶瓦本体不存在。", configuredOverridePath);
            TerracottaArchitecture.Validate(configuredOverridePath);
            return configuredOverridePath;
        }
        var path = EnsureExtracted();
        TerracottaArchitecture.Validate(path);
        return path;
    }

    /// <summary>拉起陶瓦联机的窗口。内部会先确保文件已就绪(内置版本首次使用时自动释放)。</summary>
    public void Launch(string? configuredOverridePath)
    {
        var executablePath = ResolveExecutable(configuredOverridePath);
        Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        });
    }

    public async Task<Session> LaunchEmbeddedAsync(string? configuredOverridePath, CancellationToken token = default)
    {
        await LaunchGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var executable = await Task.Run(() => ResolveExecutable(configuredOverridePath), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (CachedSession is { } cached && string.Equals(cached.Executable, executable, StringComparison.OrdinalIgnoreCase))
            {
                try { return await ReadSessionAsync(cached.Session.Endpoint, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or IOException) { CachedSession = null; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { CachedSession = null; }
            }
            var session = await LaunchEmbeddedCoreAsync(executable, token).ConfigureAwait(false);
            CachedSession = (executable, session);
            return session;
        }
        finally { LaunchGate.Release(); }
    }

    private static async Task<Session> ReadSessionAsync(Uri endpoint, CancellationToken token)
    {
        var metadata = await new TerracottaApiClient(LocalClient).GetMetadataAsync(endpoint, token).ConfigureAwait(false);
        return new Session(endpoint, metadata.Version, metadata.EasyTierVersion) { Metadata = metadata };
    }

    private static async Task<Session> LaunchEmbeddedCoreAsync(string executable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(App.DataDir, "terracotta", "sessions");
        Directory.CreateDirectory(directory);
        var portFile = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        Process? helper = null;
        try
        {
            // 上游 Windows --hmcl 会自行委托 --hmcl2，父进程退出不等于联机服务失败。
            token.ThrowIfCancellationRequested();
            helper = Process.Start(TerracottaProtocol.CreateEmbeddedStart(executable, portFile))
                ?? throw new InvalidOperationException("陶瓦进程未能启动。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(14));
            Exception? lastFailure = null;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(portFile) && new FileInfo(portFile).Length is > 0 and <= 4096)
                    {
                        var endpoint = TerracottaProtocol.ReadEndpoint(await File.ReadAllTextAsync(portFile, timeout.Token).ConfigureAwait(false));
                        return await ReadSessionAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or KeyNotFoundException)
                {
                    lastFailure = ex;
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
                {
                    lastFailure = new TimeoutException("本地服务正在准备中。");
                }
                if (timeout.IsCancellationRequested && lastFailure != null)
                    LauncherLogService.AppendLine("[陶瓦启动等待] " + lastFailure.Message);
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("等待陶瓦启动超时（14 秒）。可以使用“打开程序”检查，或更换兼容的官方版本。");
        }
        finally
        {
            // 上游父进程也会观察该端口文件，保留 15 秒让其完成握手；取消时也清理迟到文件。
            // 不结束全局 Terracotta 核心：它可能正被其他页面或启动器复用。
            _ = CleanupHandshakeAsync(portFile, helper);
        }
    }

    private static async Task CleanupHandshakeAsync(string portFile, Process? helper)
    {
        await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        try { File.Delete(portFile); File.Delete(portFile + ".tmp"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { helper?.Dispose(); }
    }

    public async Task<string> ReadLogAsync(Uri endpoint, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        token = timeout.Token;
        using var response = await LocalClient.GetAsync(TerracottaProtocol.CreateApiUri(endpoint, TerracottaApiOperation.Log), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var tail = new System.Text.StringBuilder();
        var buffer = new char[4096];
        var total = 0;
        while (total < 2 * 1024 * 1024)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            tail.Append(buffer, 0, count);
            if (tail.Length > 20000) tail.Remove(0, tail.Length - 20000);
        }
        return (total >= 2 * 1024 * 1024 ? "日志过大，仅显示已读取部分。\n" : "") + tail.ToString();
    }

    /// <summary>打开系统默认浏览器访问陶瓦联机的 GitHub Releases 页面。
    /// 默认流程已经不需要用户下载，目前联机页 UI 上没有暴露调用该方法的按钮
    /// (内置版本已覆盖绝大多数场景)；有意保留该方法 + 上面两个 URL 常量，方便以后
    /// 需要加"查看官网"/"检查是否有新版本"入口时直接复用，不算废弃代码。</summary>
    public void OpenReleasesPage()
    {
        Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
    }
}
