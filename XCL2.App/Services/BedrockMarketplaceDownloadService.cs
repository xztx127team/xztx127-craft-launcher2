using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 下载已由来源 API 枚举的基岩包。只保存文件，不启动浏览器、不执行或安装包。
/// CDN 请求使用独立无凭据客户端；内容通过检查后才在同一目录中原子提交。
/// </summary>
public sealed class BedrockMarketplaceDownloadService : IDisposable
{
    internal const long MaxDownloadBytes = 1024L * 1024 * 1024;
    internal const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxManifestBytes = 256 * 1024;
    private const int MaxNestedPackBytes = 128 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _idleTimeout;

    public BedrockMarketplaceDownloadService() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { }

    // 注入传输层仅用于独立回归测试；不接收携带 API key 的 HttpClient。
    internal BedrockMarketplaceDownloadService(HttpMessageHandler handler, TimeSpan? idleTimeout = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        if (_idleTimeout <= TimeSpan.Zero || _idleTimeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _http.Dispose();

    public async Task<string> DownloadAsync(BedrockMarketFile file, string destinationDirectory,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateFileName(file.Name);
        var source = ValidateDownloadUri(file.DownloadUrl);
        if (file.Size < 0 || file.Size > MaxDownloadBytes)
            throw new InvalidDataException("文件大小超出支持范围（最大 1 GiB）。");
        ValidateHash(file.Sha256, 64, "SHA-256");
        ValidateHash(file.Sha1, 40, "SHA-1");
        ValidateHash(file.Md5, 32, "MD5");
        ct.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".xcl-bedrock-" + Guid.NewGuid().ToString("N") + ".part");
        DownloadTransferPolicy.TrackTarget(Path.Combine(directory, file.Name));
        DownloadTransferPolicy.TrackTemporary(temporaryPath, resumable: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var transferToken = timeout.Token;
        try
        {
            progress?.Report(new ProgressInfo("连接基岩版资源服务器", 0, 1, file.Name));
            using var response = await GetResponseAsync(source, transferToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"资源服务器返回 HTTP {(int)response.StatusCode}，未保存文件。");
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载地址返回了网页或接口信息，未返回基岩版资源包。");
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is < 0 or > MaxDownloadBytes)
                throw new InvalidDataException("下载文件超过 1 GiB 限制。");
            if (file.Size > 0 && contentLength.HasValue && contentLength != file.Size)
                throw new InvalidDataException("来源提供的文件大小与下载响应不一致。");
            DownloadTransferPolicy.EnsureDiskSpace(temporaryPath, file.Size > 0 ? file.Size : contentLength ?? 0);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long downloaded = 0;
            long lastProgress = -1;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(transferToken);
            using (DownloadTransferPolicy.BeginPhase("传输"))
            {
                try
                {
                    readTimeout.CancelAfter(_idleTimeout);
                    await using var input = await response.Content.ReadAsStreamAsync(readTimeout.Token).ConfigureAwait(false);
                    readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        progress?.Report(new ProgressInfo("正在下载基岩版资源", 0,
                            Kilobytes(file.Size > 0 ? file.Size : contentLength ?? 0), file.Name) { Unit = "KiB" });
                        var buffer = new byte[81920];
                        while (true)
                        {
                            readTimeout.CancelAfter(_idleTimeout);
                            var count = await input.ReadAsync(buffer.AsMemory(), readTimeout.Token).ConfigureAwait(false);
                            // Only waiting for network bytes is an idle timeout. User-configured throttling,
                            // disk writes and package validation keep the task's cancellation token instead.
                            readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                            if (count == 0) break;
                            downloaded += count;
                            if (downloaded > MaxDownloadBytes || (contentLength.HasValue && downloaded > contentLength.Value)
                                || (file.Size > 0 && downloaded > file.Size))
                                throw new InvalidDataException("下载数据超过声明大小或 1 GiB 限制。");
                            await DownloadTransferPolicy.ConsumeAsync(count, transferToken).ConfigureAwait(false);
                            await output.WriteAsync(buffer.AsMemory(0, count), transferToken).ConfigureAwait(false);
                            sha256.AppendData(buffer, 0, count);
                            sha1.AppendData(buffer, 0, count);
                            md5.AppendData(buffer, 0, count);
                            var now = Environment.TickCount64;
                            if (lastProgress < 0 || now - lastProgress >= 100)
                            {
                                progress?.Report(new ProgressInfo("正在下载基岩版资源", Kilobytes(downloaded),
                                    Kilobytes(file.Size > 0 ? file.Size : contentLength ?? 0), file.Name) { Unit = "KiB" });
                                lastProgress = now;
                            }
                        }
                        await output.FlushAsync(transferToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException ex) when (!transferToken.IsCancellationRequested && readTimeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"资源服务器连续 {_idleTimeout.TotalSeconds:0.#} 秒没有返回下载数据，请重试或打开发布页更换下载来源。", ex);
                }
            }
            transferToken.ThrowIfCancellationRequested();
            if (downloaded == 0 || (contentLength.HasValue && downloaded != contentLength.Value)
                || (file.Size > 0 && downloaded != file.Size))
                throw new InvalidDataException("资源文件未下载完整。");
            using var verifying = DownloadTransferPolicy.BeginPhase("校验");
            CheckHash(file.Sha256, sha256.GetHashAndReset(), "SHA-256");
            CheckHash(file.Sha1, sha1.GetHashAndReset(), "SHA-1");
            CheckHash(file.Md5, md5.GetHashAndReset(), "MD5");
            progress?.Report(new ProgressInfo("正在检查基岩版资源包", 0, 1, file.Name));
            // ZipArchive/JSON 为同步 API，放到工作线程，避免冻结 WPF 下载列表。
            await Task.Run(() =>
            {
                using var stream = File.OpenRead(temporaryPath);
                ValidatePackage(stream, transferToken);
            }, transferToken).ConfigureAwait(false);
            transferToken.ThrowIfCancellationRequested();
            var savedPath = CommitWithoutOverwrite(temporaryPath, directory, file.Name, transferToken);
            DownloadTransferPolicy.TrackTarget(savedPath);
            DownloadTransferPolicy.TrackCompleted(savedPath);
            progress?.Report(new ProgressInfo("基岩版资源已下载", 1, 1, Path.GetFileName(savedPath)));
            return savedPath;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("资源下载或检查已超过 30 分钟，请重试。", ex);
        }
        finally
        {
            // 下载、取消、校验失败时都只清理自己的临时文件，绝不删除已有资源。
            try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<HttpResponseMessage> GetResponseAsync(Uri uri, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 8; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("XCL2-BedrockMarketplace/1.0");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var connecting = DownloadTransferPolicy.BeginPhase("连接");
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(_idleTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && connectTimeout.IsCancellationRequested)
            {
                throw new TimeoutException($"连接资源服务器超过 {_idleTimeout.TotalSeconds:0.#} 秒未响应，请重试或打开发布页更换下载来源。", ex);
            }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null) throw new InvalidDataException("下载服务器返回了无效重定向。");
            uri = ValidateDownloadUri(new Uri(uri, location).AbsoluteUri);
        }
        throw new InvalidDataException("下载服务器重定向次数过多。");
    }

    internal static Uri ValidateDownloadUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("资源只支持公开 HTTPS 直链。");
        var host = uri.IdnHost;
        if (!(host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("下载链接不属于已接入的 GitHub / CurseForge 文件服务器。");
        return uri;
    }

    internal static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name != name.Trim()
            || name.EndsWith('.') || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new InvalidDataException("来源返回的文件名无效。");
        var extension = Path.GetExtension(name);
        if (!extension.Equals(".mcpack", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mcaddon", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仅支持 .mcpack、.mcaddon 和包含基岩包的 .zip 文件。");
        var deviceName = name.Split('.')[0].TrimEnd(' ');
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (deviceName.Length == 4 && "123456789¹²³".Contains(deviceName[3])
                && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("来源返回的文件名使用了系统保留名称。");
    }

    private static void ValidateHash(string? value, int length, string label)
    {
        if (!string.IsNullOrWhiteSpace(value) && (value.Length != length || !value.All(Uri.IsHexDigit)))
            throw new InvalidDataException($"来源返回的 {label} 校验值格式无效。");
    }

    private static void CheckHash(string? expected, byte[] actual, string label)
    {
        if (!string.IsNullOrWhiteSpace(expected)
            && !expected.Equals(Convert.ToHexString(actual), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"资源文件 {label} 校验失败。");
    }

    private static int Kilobytes(long bytes) => (int)((bytes + 1023) / 1024);

    private static string CommitWithoutOverwrite(string temporaryPath, string directory, string name, CancellationToken ct)
    {
        for (var suffix = 0; suffix < 10000; suffix++)
        {
            ct.ThrowIfCancellationRequested();
            var candidateName = suffix == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({suffix}){Path.GetExtension(name)}";
            var path = Path.Combine(directory, candidateName);
            if (File.Exists(path) || Directory.Exists(path)) continue;
            try { File.Move(temporaryPath, path, overwrite: false); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new IOException("同名资源文件过多，请选择另一个保存文件夹。");
    }

    internal static void ValidatePackage(Stream stream, CancellationToken ct, long expandedLimit = MaxExpandedBytes)
    {
        var budget = new InspectionBudget { ExpandedLimit = expandedLimit };
        if (!InspectArchive(stream, 0, budget, ct))
            throw new InvalidDataException("文件中没有有效的基岩版行为包、资源包或脚本包 manifest.json；未保存该文件。");
    }

    private sealed class InspectionBudget
    {
        public long ExpandedLimit;
        public long Expanded;
        public long Read;
        public int Entries;
        public int NestedPacks;
    }

    private static bool InspectArchive(Stream stream, int depth, InspectionBudget budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckZipDirectory(stream, budget, ct);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith('/') || path.Contains(':') || path.Contains('\0')
                || path.Split('/').Any(segment => segment == ".."))
                throw new InvalidDataException("资源包包含不安全的文件路径。");
            if (++budget.Entries > 20000 || entry.Length < 0 || entry.Length > budget.ExpandedLimit - budget.Expanded)
                throw new InvalidDataException("资源包展开后的大小或文件数量超出限制。");
            budget.Expanded += entry.Length;
        }
        var hasPack = false;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                var data = ReadEntry(entry, MaxManifestBytes, budget, ct);
                // 非基岩格式的 manifest 不被当作成功；同一 ZIP 可包含多个真正的包。
                hasPack |= IsBedrockManifest(data);
            }
            else if (name.EndsWith(".mcpack", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".mcaddon", StringComparison.OrdinalIgnoreCase))
            {
                // 支持发布 ZIP → .mcaddon → .mcpack，继续嵌套则拒绝。
                if (depth >= 2 || ++budget.NestedPacks > 32)
                    throw new InvalidDataException("资源包嵌套层数或嵌套包数量超出限制。");
                var data = ReadEntry(entry, MaxNestedPackBytes, budget, ct);
                using var nested = new MemoryStream(data, writable: false);
                hasPack |= InspectArchive(nested, depth + 1, budget, ct);
            }
        }
        return hasPack;
    }

    private static void CheckZipDirectory(Stream stream, InspectionBudget budget, CancellationToken ct)
    {
        // ZipArchive.Entries 会一次构造中央目录。先检查 EOCD，避免海量空条目在
        // 逐项限额判断之前耗尽内存；本下载器的大小上限不需要 ZIP64/多卷 ZIP。
        ct.ThrowIfCancellationRequested();
        if (!stream.CanSeek || stream.Length < 22)
            throw new InvalidDataException("文件不是完整的 ZIP / 基岩版资源包。");
        stream.Position = 0;
        Span<byte> signature = stackalloc byte[4];
        stream.ReadExactly(signature);
        if (BinaryPrimitives.ReadUInt32LittleEndian(signature) != 0x04034b50)
            throw new InvalidDataException("下载内容不是 ZIP 格式的基岩版资源包。");
        var tail = new byte[(int)Math.Min(stream.Length, 65535 + 22)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var record = tail.AsSpan(i);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06054b50
                || i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) != tail.Length) continue;
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (disk != 0 || directoryDisk != 0 || diskEntries != entries || entries == ushort.MaxValue
                || entries > 20000 - budget.Entries || directorySize > 16 * 1024 * 1024
                || (long)directoryOffset + directorySize != stream.Length - tail.Length + i)
                throw new InvalidDataException("资源包中央目录过大、损坏，或使用了不支持的多卷 / ZIP64 格式。");
            stream.Position = directoryOffset;
            Span<byte> centralHeader = stackalloc byte[46];
            var directoryEnd = (long)directoryOffset + directorySize;
            for (var entry = 0; entry < entries; entry++)
            {
                ct.ThrowIfCancellationRequested();
                if (directoryEnd - stream.Position < centralHeader.Length)
                    throw new InvalidDataException("资源包中央目录中的条目数不一致。");
                stream.ReadExactly(centralHeader);
                if (BinaryPrimitives.ReadUInt32LittleEndian(centralHeader) != 0x02014b50)
                    throw new InvalidDataException("资源包中央目录中的条目无效。");
                var variableLength = (long)BinaryPrimitives.ReadUInt16LittleEndian(centralHeader[28..])
                    + BinaryPrimitives.ReadUInt16LittleEndian(centralHeader[30..])
                    + BinaryPrimitives.ReadUInt16LittleEndian(centralHeader[32..]);
                if (variableLength > directoryEnd - stream.Position)
                    throw new InvalidDataException("资源包中央目录中的文件名或附加字段不完整。");
                stream.Position += variableLength;
            }
            if (stream.Position != directoryEnd)
                throw new InvalidDataException("资源包中央目录中的条目数不一致。");
            stream.Position = 0;
            return;
        }
        throw new InvalidDataException("资源包缺少完整的 ZIP 中央目录。");
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximum, InspectionBudget budget, CancellationToken ct)
    {
        if (entry.Length > maximum || entry.Length > 256L * 1024 * 1024 - budget.Read)
            throw new InvalidDataException("资源包清单或嵌套包超出读取限制。");
        using var input = entry.Open();
        var data = new byte[(int)entry.Length];
        var offset = 0;
        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            var count = input.Read(data, offset, Math.Min(81920, data.Length - offset));
            if (count == 0) break;
            offset += count;
            budget.Read += count;
        }
        ct.ThrowIfCancellationRequested();
        if (offset != data.Length || input.ReadByte() != -1)
            throw new InvalidDataException("资源包清单或嵌套包长度异常。");
        return data;
    }

    private static bool IsBedrockManifest(byte[] data)
    {
        // Minecraft 的 manifest 允许 UTF-8 BOM、注释和尾逗号。
        ReadOnlyMemory<byte> json = data;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) json = json[3..];
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format_version", out var format) || !format.TryGetInt32(out var formatValue) || formatValue < 1
                || !root.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object
                || !header.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())
                || !HasPackIdentity(header)
                || !root.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
                return false;
            return modules.EnumerateArray().Any(module => module.ValueKind == JsonValueKind.Object
                && HasPackIdentity(module)
                && module.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() is "data" or "resources" or "script");
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool HasPackIdentity(JsonElement element)
    {
        if (!element.TryGetProperty("uuid", out var uuid) || uuid.ValueKind != JsonValueKind.String
            || !Guid.TryParse(uuid.GetString(), out _) || !element.TryGetProperty("version", out var version))
            return false;
        if (version.ValueKind == JsonValueKind.Array)
            return version.GetArrayLength() == 3 && version.EnumerateArray().All(part => part.TryGetInt32(out var number) && number >= 0);
        // Manifest v3 的版本使用 SemVer 字符串；不要把新资源包误判成无效包。
        // https://learn.microsoft.com/minecraft/creator/reference/content/addonsreference/packmanifest
        if (version.ValueKind != JsonValueKind.String || version.GetString() is not { Length: > 0 and <= 128 } text) return false;
        return Regex.IsMatch(text, @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
