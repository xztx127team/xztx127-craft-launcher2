using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>统一镜像候选、进程内健康退避与有校验的原子下载。</summary>
public static class DownloadEndpoints
{
    private sealed class HostHealth
    {
        public int ConsecutiveFailures;
        public DateTime RetryAfterUtc;
        public int Penalty
        {
            get { lock (this) return DateTime.UtcNow < RetryAfterUtc ? ConsecutiveFailures : 0; }
        }
    }

    private static readonly SemaphoreSlim RangeConnections = AdaptiveDownloadConcurrency.RangeConnections;
    private static readonly ConcurrentDictionary<string, HostHealth> Health = new(StringComparer.OrdinalIgnoreCase);
    private sealed class SourceProbe
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTime ExpiresUtc;
        public double ResponseMilliseconds = double.MaxValue;
    }
    private static readonly SemaphoreSlim ProbeConnections = new(4, 4);
    private static readonly ConcurrentDictionary<string, SourceProbe> SourceProbes = new(StringComparer.OrdinalIgnoreCase);
    public static bool PreferMirror => ConfigService.Active?.Config.Source == DownloadSource.BMCLAPI;

    public static void ReportFailure(string url)
    {
        if (HostOf(url) is not { } host) return;
        var health = Health.GetOrAdd(host, _ => new HostHealth());
        lock (health)
        {
            if (DateTime.UtcNow >= health.RetryAfterUtc) health.ConsecutiveFailures = 0;
            health.ConsecutiveFailures = Math.Min(health.ConsecutiveFailures + 1, 5);
            health.RetryAfterUtc = DateTime.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, health.ConsecutiveFailures)));
        }
    }

    public static void ReportSuccess(string url)
    {
        if (HostOf(url) is { } host) Health.TryRemove(host, out _);
    }

    private static string? HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    public static IReadOnlyList<string> Candidates(string officialUrl, bool preferMirror)
    {
        var mirrors = DownloadMirrorPolicy.Mirrors(officialUrl, ConfigService.Active?.Config.DownloadMirrors);
        var official = new[] { officialUrl }.Concat(DownloadMirrorPolicy.OfficialCdnAlternatives(officialUrl));
        return OrderCandidates(preferMirror ? mirrors.Concat(official) : official.Concat(mirrors));
    }

    public static string? ToMirror(string officialUrl) =>
        DownloadMirrorPolicy.Mirrors(officialUrl, ConfigService.Active?.Config.DownloadMirrors).FirstOrDefault();

    public static IReadOnlyList<string> AllCandidates(string officialUrl, bool preferMirror) => Candidates(officialUrl, preferMirror);

    public static IReadOnlyList<string> OrderCandidates(IEnumerable<string> candidates) => candidates
        .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal)
        .OrderBy(url => Health.TryGetValue(HostOf(url) ?? "", out var h) ? h.Penalty : 0)
        .ThenBy(url => IsPreferredOfficialCdn(url) ? 0 : 1).ToArray();

    private static bool IsPreferredOfficialCdn(string url) =>
        DownloadMirrorPolicy.IsPreferredOfficialCdn(url, ConfigService.Active?.Config.DownloadMirrors);

    public static IReadOnlyList<string> UnhealthyHosts() =>
        Health.Where(kv => kv.Value.Penalty > 0).Select(kv => kv.Key).ToArray();

    public static void ResetHealth()
    {
        Health.Clear();
        SourceProbes.Clear();
    }

    /// <summary>Probe public download sources concurrently; reuse the host result for a batch of small files.</summary>
    internal static async Task<IReadOnlyList<string>> ProbeCandidatesAsync(HttpClient http,
        IReadOnlyList<string> candidates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Never send credential-bearing/signed requests to another endpoint, including a HEAD probe.
        if (candidates.Count < 2 || HasCredentialHeaders(http) ||
            candidates.Any(url => !DownloadMirrorPolicy.MayMirror(url))) return candidates;
        if (SourceProbes.Count > 128) SourceProbes.Clear();
        using var probing = DownloadTransferPolicy.BeginPhase("探测下载源");
        var results = await Task.WhenAll(candidates.Take(8).Select(async url =>
        {
            var authority = new Uri(url).GetLeftPart(UriPartial.Authority);
            var state = SourceProbes.GetOrAdd(authority, _ => new SourceProbe());
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (DateTime.UtcNow < state.ExpiresUtc) return (Url: url, Delay: state.ResponseMilliseconds);
                await ProbeConnections.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var request = new HttpRequestMessage(HttpMethod.Head, url);
                    var timer = Stopwatch.StartNew();
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    // A missing file or a server without HEAD support remains a GET fallback;
                    // neither proves that the entire Maven/assets host is broken.
                    state.ResponseMilliseconds = response.IsSuccessStatusCode ? timer.Elapsed.TotalMilliseconds : double.MaxValue;
                    state.ExpiresUtc = DateTime.UtcNow.AddMinutes(2);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                {
                    state.ResponseMilliseconds = double.MaxValue;
                    state.ExpiresUtc = DateTime.UtcNow.AddSeconds(20);
                    ReportFailure(url);
                }
                finally { ProbeConnections.Release(); }
                return (Url: url, Delay: state.ResponseMilliseconds);
            }
            finally { state.Gate.Release(); }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var measured = results.ToDictionary(result => result.Url, result => result.Delay, StringComparer.Ordinal);
        return candidates.OrderBy(url => Health.TryGetValue(HostOf(url) ?? "", out var health) ? health.Penalty : 0)
            .ThenBy(url => IsPreferredOfficialCdn(url) ? 0 : 1)
            // Preserve the API-supplied file host ahead of its official sibling when
            // both are healthy. A fast third-party mirror must not undo the CDN setting.
            .ThenBy(url => IsPreferredOfficialCdn(url) ? 0 : measured.GetValueOrDefault(url, double.MaxValue)).ToArray();
    }

    private static bool HasCredentialHeaders(HttpClient http) => http.DefaultRequestHeaders.Any(header =>
        header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Contains("token", StringComparison.OrdinalIgnoreCase));

    internal static async Task<IReadOnlyList<string>> ResolveCandidatesAsync(HttpClient http, string officialUrl,
        bool preferMirror, string name, string? sha1, CancellationToken ct)
    {
        // 不把认证头或可能含签名的 URL 转交给公共/自定义镜像。
        if (HasCredentialHeaders(http) || !DownloadMirrorPolicy.MayMirror(officialUrl)) return new[] { officialUrl };
        var candidates = Candidates(officialUrl, preferMirror);
        if (ConfigService.Active?.Config.DownloadMirrors?.PreferCurseForgeCdn != false)
        {
            var cdn = await CurseForgeNeoForgeCdnService.TryResolveAsync(officialUrl, ct);
            if (!string.IsNullOrWhiteSpace(cdn))
                candidates = OrderCandidates(Candidates(cdn, preferMirror).Concat(candidates));
        }
        return OrderCandidates(await Plugins.PluginProviderRegistry.AddMirrorAsync(
            officialUrl, name, sha1, candidates, ct));
    }

    internal static Task<string> PrepareRequestUrlAsync(HttpClient http, string url,
        string name, string? sha1, CancellationToken ct)
    {
        // Candidate selection alone is insufficient: an interceptor can replace a
        // direct authenticated request's origin after the mirror check has passed.
        // Private URLs must not be exposed to plugin callbacks either.
        if (HasCredentialHeaders(http) || !DownloadMirrorPolicy.MayMirror(url)) return Task.FromResult(url);
        return Plugins.PluginInterceptionRegistry.PrepareDownloadAsync(url, name, sha1, ct);
    }

    public static async Task<string> GetStringWithFallbackAsync(HttpClient http, string officialUrl,
        bool preferMirror, string friendlyMessage, CancellationToken ct = default)
    {
        var candidates = await ResolveCandidatesAsync(http, officialUrl, preferMirror, "metadata.json", null, ct);
        Exception? lastError = null;
        foreach (var url in candidates)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var resolved = await PrepareRequestUrlAsync(http, url, "metadata.json", null, attempt.Token);
                var result = await http.GetStringAsync(resolved, attempt.Token);
                // 镜像的 200 HTML 错误页不能阻断后续官方候选。
                using var document = JsonDocument.Parse(result);
                ReportSuccess(url);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not Plugins.PluginFlowCanceledException)
            {
                ReportFailure(url);
                lastError = ex;
            }
        }
        throw new InvalidOperationException(friendlyMessage, lastError);
    }

    public static Task<string> DownloadFileWithFallbackAsync(HttpClient http, string officialUrl, bool preferMirror,
        string destPath, string friendlyMessage, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default,
        string? expectedSha1 = null, string? expectedSha512 = null, string? expectedMd5 = null,
        long? expectedSize = null, string? userAgent = null, string? expectedSha256 = null, Action<string>? validateFile = null) =>
        DownloadFilesWithFallbackAsync(http, new[] { officialUrl }, preferMirror, destPath, friendlyMessage,
            progress, ct, expectedSha1, expectedSha512, expectedMd5, expectedSize, userAgent, expectedSha256, validateFile);

    /// <summary>所有官方清单 URL 都参与回退；哈希不匹配与空文件同样触发换源，不提交坏文件。</summary>
    public static async Task<string> DownloadFilesWithFallbackAsync(HttpClient http, IEnumerable<string> officialUrls,
        bool preferMirror, string destPath, string friendlyMessage, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default, string? expectedSha1 = null, string? expectedSha512 = null,
        string? expectedMd5 = null, long? expectedSize = null, string? userAgent = null, string? expectedSha256 = null, Action<string>? validateFile = null)
    {
        IDisposable targetLease;
        using (DownloadTransferPolicy.BeginPhase("等待文件占用"))
            targetLease = await DownloadTargetLock.AcquireAsync(destPath, ct);
        using var ownership = targetLease;
        ct.ThrowIfCancellationRequested();
        DownloadTransferPolicy.TrackTarget(destPath);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
        ArchiveImportSafety.CheckLinks(destPath);
        Directory.CreateDirectory(parent);
        var name = Path.GetFileName(destPath);
        var expanded = new List<string>();
        foreach (var url in officialUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal).Take(16))
            expanded.AddRange(await ResolveCandidatesAsync(http, url, preferMirror, name, expectedSha1, ct));
        var candidates = OrderCandidates(expanded);
        if (candidates.Count == 0) throw new InvalidOperationException("没有可用的下载地址。");
        candidates = await ProbeCandidatesAsync(http, candidates, ct);
        Exception? lastError = null;
        DownloadTransferPolicy.EnsureDiskSpace(destPath, expectedSize ?? 0);
        // 首轮迅速换源，全部失败后再进行一次有限重试；不对同一个坏源连续请求三次。
        for (var round = 0; round < 2; round++)
        {
            if (round > 0) await DownloadTransferPolicy.WaitRetryAsync(TimeSpan.FromMilliseconds(500), ct, lastError?.Message);
            foreach (var url in OrderCandidates(candidates))
            {
                var temporary = destPath + ".fbpart-" + Guid.NewGuid().ToString("N");
                DownloadTransferPolicy.TrackTemporary(temporary, resumable: false, targetPath: destPath);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    var resolved = await PrepareRequestUrlAsync(http, url, name, expectedSha1, attempt.Token);
                    using var request = new HttpRequestMessage(HttpMethod.Get, resolved);
                    if (!string.IsNullOrWhiteSpace(userAgent)) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                    using var connecting = DownloadTransferPolicy.BeginPhase("连接");
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                    connecting.Dispose();
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                        throw new InvalidDataException("服务端返回了未请求的部分文件，正在尝试其他下载源。");
                    var length = response.Content.Headers.ContentLength;
                    if (expectedSize is > 0 && length is > 0 && expectedSize != length)
                        throw new InvalidDataException("下载大小与资源清单不一致。");
                    DownloadTransferPolicy.EnsureDiskSpace(destPath, expectedSize is > 0 ? expectedSize.Value : length ?? 0);
                    var reporter = new TransferProgress(progress, name, HostOf(url), expectedSize is > 0 ? expectedSize.Value : length ?? 0);
                    var cfg = ConfigService.Active?.Config;
                    using var adaptiveTransfer = length is >= 8 * 1024 * 1024 ? AdaptiveDownloadConcurrency.EnterTransfer() : null;
                    var parallel = AdaptiveDownloadConcurrency.Select(resolved, length ?? 0,
                        cfg?.EnableMultiThreadDownload != false ? cfg?.MaxDownloadThreads ?? 4 : 1,
                        response.Headers.AcceptRanges.Contains("bytes"), DownloadTransferPolicy.EffectiveBytesPerSecond);
                    var transferWatch = Stopwatch.StartNew();
                    // 用 GET 响应头判断，无额外 HEAD；按实际条件选片数，与游戏核心下载共享 8 路连接池。
                    var canVerify = !string.IsNullOrWhiteSpace(expectedSha1) || !string.IsNullOrWhiteSpace(expectedSha256) ||
                        !string.IsNullOrWhiteSpace(expectedSha512) || !string.IsNullOrWhiteSpace(expectedMd5) ||
                        response.Headers.ETag is { IsWeak: false } || response.Content.Headers.LastModified.HasValue;
                    if (parallel > 1 && canVerify && length is >= 8 * 1024 * 1024 && response.Headers.AcceptRanges.Contains("bytes"))
                    {
                        var tag = response.Headers.ETag is { IsWeak: false } etag ? etag : null;
                        var modified = response.Content.Headers.LastModified;
                        response.Dispose();
                        attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                        try
                        {
                            await DownloadRangesAsync(http, resolved, temporary, length.Value, parallel, userAgent,
                                tag, modified, reporter, ct).ConfigureAwait(false);
                        }
                        catch (UnsupportedRangeException)
                        {
                            parallel = 1;
                            // CDN声明支持却返回200/错误区间时，全部分片已停止后重新走单流。
                            reporter.Reset();
                            using var singleRequest = new HttpRequestMessage(HttpMethod.Get, resolved);
                            if (!string.IsNullOrWhiteSpace(userAgent)) singleRequest.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                            attempt.CancelAfter(TimeSpan.FromSeconds(45));
                            using var single = await http.SendAsync(singleRequest, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                            single.EnsureSuccessStatusCode();
                            if (single.StatusCode == System.Net.HttpStatusCode.PartialContent)
                                throw new InvalidDataException("服务端返回了未请求的部分文件。");
                            if (single.Content.Headers.ContentLength is { } singleLength && singleLength != length)
                                throw new InvalidDataException("回退时服务端文件大小已改变。");
                            await CopySingleAsync(single, temporary, reporter, attempt, ct).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        parallel = 1;
                        await CopySingleAsync(response, temporary, reporter, attempt, ct).ConfigureAwait(false);
                    }
                    attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                    ct.ThrowIfCancellationRequested();
                    using (DownloadTransferPolicy.BeginPhase("校验"))
                        await Task.Run(() => VerifyFile(temporary, expectedSha1, expectedSha512, expectedMd5, expectedSize, expectedSha256, ct), ct);
                    if (validateFile != null) await Task.Run(() => validateFile(temporary), ct);
                    ct.ThrowIfCancellationRequested();
                    ArchiveImportSafety.CheckLinks(destPath);
                    File.Move(temporary, destPath, overwrite: true);
                    AdaptiveDownloadConcurrency.Record(resolved, length ?? 0, transferWatch.Elapsed, parallel);
                    DownloadTransferPolicy.TrackCompleted(destPath);
                    ReportSuccess(url);
                    return destPath;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not (Plugins.PluginFlowCanceledException or DownloadDiskSpaceException))
                {
                    if (ex is HttpRequestException or TimeoutException or OperationCanceledException) AdaptiveDownloadConcurrency.Failed(url);
                    ReportFailure(url);
                    lastError = ex;
                    DownloadTransferPolicy.ReportIssue($"{name} 下载失败，正在尝试其他下载源：{ex.Message}");
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }
        throw new IOException(friendlyMessage + "（已尝试所有可用下载源）", lastError);
    }

    private sealed class UnsupportedRangeException : IOException { }

    // 不把每个网络buffer都排进WPF Dispatcher；大文件和多分片下载最多每100ms刷新一次。
    private sealed class TransferProgress(IProgress<ProgressInfo>? progress, string name, string? host, long total)
    {
        private readonly object _gate = new();
        private long _done, _lastReport;
        public void Reset() { lock (_gate) { _done = 0; _lastReport = 0; } }
        public void Add(int count, bool complete = false)
        {
            lock (_gate)
            {
                _done += count;
                var now = Stopwatch.GetTimestamp();
                if (!complete && _lastReport != 0 && Stopwatch.GetElapsedTime(_lastReport, now).TotalMilliseconds < 100) return;
                _lastReport = now;
                progress?.Report(new ProgressInfo("正在下载 " + name, Kilobytes(_done), Kilobytes(total),
                    $"{DownloadTransferPolicy.FormatCapacity(_done)} · {host}") { Unit = "KiB" });
            }
        }
    }

    private static async Task CopySingleAsync(HttpResponseMessage response, string path, TransferProgress progress,
        CancellationTokenSource attempt, CancellationToken ct)
    {
        using var phase = DownloadTransferPolicy.BeginPhase("传输");
        await using var input = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long done = 0;
        while (true)
        {
            attempt.CancelAfter(TimeSpan.FromSeconds(45));
            var read = await input.ReadAsync(buffer, attempt.Token).ConfigureAwait(false);
            if (read == 0) break;
            // 限速等待和磁盘写入不计作服务器无响应；用户取消仍立即生效。
            attempt.CancelAfter(Timeout.InfiniteTimeSpan);
            await DownloadTransferPolicy.ConsumeAsync(read, ct).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress.Add(read);
        }
        if (response.Content.Headers.ContentLength is { } length && done != length)
            throw new InvalidDataException("下载连接提前结束，文件不完整。");
        progress.Add(0, complete: true);
    }

    private static async Task DownloadRangesAsync(HttpClient http, string url, string path, long total, int count,
        string? userAgent, System.Net.Http.Headers.EntityTagHeaderValue? etag, DateTimeOffset? modified,
        TransferProgress progress, CancellationToken ct)
    {
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) output.SetLength(total);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? firstFailure = null;
        var unsupported = 0;
        var tasks = Enumerable.Range(0, count).Select(async index =>
        {
            var from = total * index / count;
            var to = total * (index + 1) / count - 1;
            using (DownloadTransferPolicy.BeginPhase("等待连接"))
                await RangeConnections.WaitAsync(batch.Token).ConfigureAwait(false);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(batch.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(45));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                if (etag != null) request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(etag);
                else if (modified.HasValue) request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(modified.Value);
                if (!string.IsNullOrWhiteSpace(userAgent)) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                using var connecting = DownloadTransferPolicy.BeginPhase("连接");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                connecting.Dispose();
                response.EnsureSuccessStatusCode();
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != System.Net.HttpStatusCode.PartialContent || range?.From != from || range.To != to || range.Length != total ||
                    etag != null && response.Headers.ETag != null && !etag.Equals(response.Headers.ETag))
                    throw new UnsupportedRangeException();
                using var phase = DownloadTransferPolicy.BeginPhase("传输");
                await using var input = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                    128 * 1024, FileOptions.Asynchronous);
                output.Position = from;
                var buffer = new byte[128 * 1024];
                long done = 0;
                while (true)
                {
                    attempt.CancelAfter(TimeSpan.FromSeconds(45));
                    var read = await input.ReadAsync(buffer, attempt.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (read > to - from + 1 - done) throw new UnsupportedRangeException();
                    attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                    await DownloadTransferPolicy.ConsumeAsync(read, batch.Token).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read), batch.Token).ConfigureAwait(false);
                    done += read;
                    progress.Add(read);
                }
                if (done != to - from + 1) throw new InvalidDataException("下载分片提前结束，文件不完整。");
            }
            catch (UnsupportedRangeException)
            { Interlocked.Exchange(ref unsupported, 1); await batch.CancelAsync().ConfigureAwait(false); throw; }
            catch (OperationCanceledException) when (batch.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { Interlocked.CompareExchange(ref firstFailure, ex, null); await batch.CancelAsync().ConfigureAwait(false); throw; }
            finally { RangeConnections.Release(); }
        }).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch
        {
            ct.ThrowIfCancellationRequested();
            if (firstFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
            if (Volatile.Read(ref unsupported) != 0) throw new UnsupportedRangeException();
            throw;
        }
        progress.Add(0, complete: true);
    }

    private static int Kilobytes(long bytes) => (int)Math.Clamp(bytes / 1024, 0, int.MaxValue);

    internal static void VerifyFile(string path, string? sha1, string? sha512, string? md5, long? expectedSize, string? sha256 = null, CancellationToken ct = default)
    {
        var length = new FileInfo(path).Length;
        if (length == 0 || expectedSize is > 0 && expectedSize != length)
            throw new InvalidDataException("下载文件为空或大小不匹配。");
        foreach (var (expected, algorithm) in new[] { (sha512, HashAlgorithmName.SHA512), (sha256, HashAlgorithmName.SHA256), (sha1, HashAlgorithmName.SHA1), (md5, HashAlgorithmName.MD5) })
        {
            if (string.IsNullOrWhiteSpace(expected)) continue;
            using var hash = IncrementalHash.CreateHash(algorithm);
            using var stream = File.OpenRead(path);
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"文件校验失败（{algorithm.Name} 不匹配），正在尝试其他下载源。");
        }
    }
}
